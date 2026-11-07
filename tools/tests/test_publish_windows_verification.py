"""Development-only integrity fixtures; no remote Git operation or production inventory."""
import copy
import datetime as dt
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zlib

spec = importlib.util.spec_from_file_location('publisher', Path(__file__).resolve().parents[1] / 'publish_windows_verification.py')
p = importlib.util.module_from_spec(spec)
spec.loader.exec_module(p)

def png():
    def chunk(kind, body):
        return struct.pack('>I', len(body)) + kind + body + struct.pack('>I', zlib.crc32(kind + body) & 0xffffffff)
    pixels = (b'\x00' + b'\x12\x34\x56\xff' * 100) * 100
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', 100, 100, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(pixels)) + chunk(b'IEND', b'')

class PublisherTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix='MadCleanPublisherTests-')
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name) / 'verification'
        self.download = Path(self.directory.name) / 'download'
        self.root.mkdir(); self.download.mkdir()
        subprocess.run(['git','init','-q','-b',p.BRANCH,str(self.root)],check=True,capture_output=True)
        subprocess.run(['git','-C',str(self.root),'remote','add','origin','https://github.com/'+p.REPOSITORY+'.git'],check=True,capture_output=True)
        (self.root/'verification-manifest.json').write_text(json.dumps({'formatVersion':1,'purpose':'MadClean Windows validation','sourceBranch':'feat/phase-one-foundation'}))
        self.env = {'GITHUB_REPOSITORY':p.REPOSITORY,'GITHUB_REF':p.SOURCE_REF,'GITHUB_EVENT_NAME':'push','GITHUB_SHA':'a'*40,'GITHUB_RUN_ID':'100','GITHUB_RUN_ATTEMPT':'1'}
        self.report = {'formatVersion':1,'sourceCommit':'a'*40,'runId':'100','runAttempt':1,'passed':True,'state':'passed',
            'startedUtc':dt.datetime.now(dt.timezone.utc).isoformat(),'completedUtc':dt.datetime.now(dt.timezone.utc).isoformat(),'limits':'TEST-ONLY fixture scope',
            'checks':[{'name':name,'state':'passed','detail':'TEST-ONLY fixture evidence'} for name in sorted(p.REQUIRED_CHECKS)],'screenshots':[]}
        data = png()
        for name in p.IMAGES:
            (self.download/name).write_bytes(data)
            self.report['screenshots'].append({'file':name,'width':100,'height':100,'sha256':hashlib.sha256(data).hexdigest()})
        self.save()
        (self.download/'core-windows.trx').write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult outcome="Passed"/><UnitTestResult outcome="Passed"/><UnitTestResult outcome="Passed"/></Results><ResultSummary><Counters total="3" executed="3" passed="3" failed="0" notExecuted="0"/></ResultSummary></TestRun>')
    def save(self):
        (self.download/'report.json').write_text(json.dumps(self.report))
    def prepare(self, build='success', verify='success'):
        return p.prepare(self.root,self.download,build,verify,self.env,core_directory=self.download)
    def rejected(self):
        before = {f.name:f.read_bytes() for f in self.root.iterdir() if f.is_file()}
        with self.assertRaises(p.EvidenceError): self.prepare()
        self.assertEqual(before,{f.name:f.read_bytes() for f in self.root.iterdir() if f.is_file()})
    def test_absolute_shortcut_control_cannot_be_omitted_from_passed_evidence(self):
        self.report['checks'] = [check for check in self.report['checks'] if check['name'] != 'shortcut-rename-win32-absolute-control']
        self.save(); self.rejected()
    def test_component_store_preview_check_cannot_be_omitted_from_passed_evidence(self):
        self.report['checks'] = [check for check in self.report['checks'] if check['name'] != 'ui-component-store-preview-interaction']
        self.save(); self.rejected()
    def test_packaged_settings_preview_check_cannot_be_omitted_from_passed_evidence(self):
        self.report['checks'] = [check for check in self.report['checks'] if check['name'] != 'ui-packaged-settings-preview-interaction']
        self.save(); self.rejected()
    def test_complete_evidence_binds_real_files_and_changes_nothing_during_validation(self):
        _, summary, files = self.prepare()
        self.assertTrue(summary['passed'])
        self.assertEqual(set(p.OWNED) - {"ci-diagnostics.json", "native-ci-diagnostics.json", "msi-request.json", "component-store.json", "component-scan.json", "published-app-update.json", "sfc-client.json"},set(files))
        self.assertFalse((self.root/'latest.json').exists())
    def test_another_existing_directory_cannot_impersonate_the_checkout_root(self):
        actual_git = p.git
        def altered_git(root,*arguments):
            return str(self.download) if arguments == ('rev-parse','--show-toplevel') else actual_git(root,*arguments)
        with patch.object(p,'git',side_effect=altered_git):
            with self.assertRaisesRegex(p.EvidenceError,'^unexpected-worktree$'): self.prepare()
    @unittest.skipUnless(os.name=='nt','Requires actual Windows 8.3 alias support')
    def test_windows_short_alias_of_same_checkout_is_accepted(self):
        import ctypes
        buffer=ctypes.create_unicode_buffer(32768)
        get_short=ctypes.windll.kernel32.GetShortPathNameW
        get_short.argtypes=[ctypes.c_wchar_p,ctypes.c_wchar_p,ctypes.c_uint]
        get_short.restype=ctypes.c_uint
        length=get_short(str(self.root),buffer,len(buffer))
        if not length or length>=len(buffer) or buffer.value.lower()==str(self.root).lower():
            self.skipTest('Filesystem did not provide a distinct 8.3 alias')
        _, summary, _=p.prepare(Path(buffer.value),self.download,'success','success',self.env,core_directory=self.download)
        self.assertTrue(summary['passed'])
    def test_native_failed_download_cannot_admit_leftover_logs(self):
        with patch.object(p.ci_diagnostics, '_safe', side_effect=AssertionError('must not read')):
            _, summary, files=p.prepare(self.root,self.download,'success','failure',self.env,
                native_command_logs=self.download,native_log_download_result='failure')
        self.assertFalse(summary['nativeDiagnosticsPresent'])
        self.assertNotIn('native-ci-diagnostics.json',files)
    def test_native_logs_have_separate_fixed_export_and_never_substitute_for_report(self):
        def collect(directory,context,outcome):
            return {'scope':'TEST ONLY','stages':[]} if directory==self.download and outcome=='success' else None
        with patch.object(p.ci_diagnostics,'collect',side_effect=collect):
            _, summary, files=p.prepare(self.root,self.download,'success','success',self.env,
                report_download_result='skipped',native_command_logs=self.download,native_log_download_result='success')
        self.assertTrue(summary['nativeDiagnosticsPresent'])
        self.assertFalse(summary['passed'])
        self.assertIn('native-ci-diagnostics.json',files)
        self.assertNotIn('ci-diagnostics.json',files)
    def test_failed_log_artifact_never_opens_the_download_directory(self):
        with patch.object(p.ci_diagnostics, '_safe', side_effect=AssertionError('must not read')):
            _, summary, files = p.prepare(self.root,self.download,'failure','skipped',self.env,
                command_logs=self.download,log_download_result='failure')
        self.assertFalse(summary['passed'])
        self.assertNotIn('ci-diagnostics.json',files)
    def test_absent_job_outputs_remain_unknown_without_losing_failure_evidence(self):
        self.env['MADCLEAN_STAGE_SDK']=''
        _, summary, _ = self.prepare(build='cancelled',verify='skipped')
        self.assertEqual('unknown',summary['stageOutcomes']['sdk'])
        self.assertEqual('unknown',summary['stageOutcomes']['restore'])
        self.assertFalse(summary['passed'])
    def test_rejected_logs_do_not_suppress_actual_stage_failure(self):
        self.env['MADCLEAN_STAGE_BUILD']='failure'
        with patch.object(p.ci_diagnostics,'collect',side_effect=ValueError('TEST rejection')):
            _, summary, files=p.prepare(self.root,self.download,'failure','skipped',self.env,
                command_logs=self.download,log_download_result='success')
        self.assertFalse(summary['passed'])
        self.assertEqual('failure',summary['stageOutcomes']['build'])
        self.assertEqual('log-evidence-rejected',summary['diagnosticsError'])
        self.assertNotIn('ci-diagnostics.json',files)
    def test_logs_alone_cannot_establish_a_pass(self):
        with patch.object(p.ci_diagnostics,'collect',return_value={'scope':'TEST ONLY','stages':[]}):
            _, summary, files=p.prepare(self.root,self.download,'success','success',self.env,
                report_download_result='skipped',core_download_result='skipped',
                command_logs=self.download,log_download_result='success')
        self.assertFalse(summary['passed'])
        self.assertIn('ci-diagnostics.json',files)
    def test_missing_report_cannot_become_pass_from_successful_jobs(self):
        (self.download/'report.json').unlink()
        _, summary, files = self.prepare()
        self.assertFalse(summary['passed']); self.assertFalse(summary['reportPresent'])
        self.assertEqual({'latest.json','core-tests.json'},set(files))
    def test_failed_early_report_remains_publishable_without_old_captures(self):
        self.report.update(passed=False,state='failed',checks=[{'name':'ui-capture','state':'failed','detail':'TEST-ONLY early failure'}],screenshots=[]); self.save()
        _, summary, files = self.prepare(verify='failure')
        self.assertFalse(summary['passed']); self.assertEqual({'latest.json','report.json','core-tests.json'},set(files))
    def test_inconclusive_source_is_not_promoted_even_with_green_job_inputs(self):
        self.report.update(passed=False,state='inconclusive',checks=[{'name':'diagnostics-provider','state':'inconclusive','detail':'TEST-ONLY denied source'}],screenshots=[]); self.save()
        self.assertFalse(self.prepare()[1]['passed'])
    def test_passing_empty_or_incomplete_check_set_is_rejected(self):
        for checks in ([],self.report['checks'][:-1]):
            self.report['checks']=checks; self.save(); self.rejected()
    def test_passing_report_cannot_omit_selected_msi_native_proof(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='msi-registration-contract']
        self.save(); self.rejected()
    def test_passing_report_cannot_omit_independent_wer_count_proof(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='wer-store-count-contract']
        self.save(); self.rejected()
    def test_passing_report_cannot_omit_independent_native_nt_control(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='shortcut-rename-nt-control']
        self.save(); self.rejected()
    def test_passing_report_cannot_omit_selected_shortcut_file_evidence(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='shortcut-file-evidence-fixture']
        self.save(); self.rejected()
    def test_passing_report_cannot_omit_independent_dacl_facts(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='shortcut-dacl-fixture']
        self.save(); self.rejected()
    def test_passing_report_requires_selected_pair_and_directory_sharing_proofs(self):
        for name in ('duplicate-pair-recheck-fixture','ui-duplicate-pair-recheck','shortcut-directory-sharing-observation'):
            with self.subTest(name=name):
                original=self.report['checks']
                self.report['checks']=[check for check in original if check['name']!=name]
                self.save(); self.rejected()
                self.report['checks']=original
    def test_device_driver_ui_interaction_cannot_be_omitted(self):
        self.report['checks'] = [check for check in self.report['checks'] if check['name'] != 'ui-device-driver-inventory-interaction']
        self.save(); self.rejected()

    def test_device_driver_properties_contract_cannot_be_omitted(self):
        self.report['checks'] = [check for check in self.report['checks'] if check['name'] != 'device-driver-properties-contract']
        self.save(); self.rejected()

    def test_cleanup_selection_review_interaction_cannot_be_omitted(self):
        self.report['checks'] = [check for check in self.report['checks'] if check['name'] != 'ui-cleanup-selection-review-interaction']
        self.save(); self.rejected()

    def test_wua_offer_metadata_contract_cannot_be_omitted(self):
        self.report['checks'] = [check for check in self.report['checks'] if check['name'] != 'wua-offer-metadata-contract']
        self.save(); self.rejected()

    def test_update_discovery_provider_contract_cannot_be_omitted(self):
        self.report['checks'] = [check for check in self.report['checks'] if check['name'] != 'update-discovery-provider-contract']
        self.save(); self.rejected()

    def test_published_executable_startup_cannot_be_omitted(self):
        self.report['checks'] = [check for check in self.report['checks'] if check['name'] != 'published-executable-startup']
        self.save(); self.rejected()

    def test_current_pair_initial_image_is_required(self):
        (self.download/'tool-27.png').unlink()
        self.rejected()
    def test_passing_report_requires_inert_process_observation(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='system-tool-process-observation-fixture']
        self.save(); self.rejected()
    def test_relative_directory_create_control_cannot_be_omitted(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='owned-relative-directory-create-control']
        self.save()
        self.rejected()
    def test_private_journal_scope_cannot_be_omitted(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='system-tool-private-journal-scope']
        self.save()
        self.rejected()
    def test_owned_confirmation_protocol_cannot_be_omitted(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='cleanup-confirmation-owned-protocol']
        self.save()
        self.rejected()
    def test_scan_history_and_selected_msi_ui_cannot_be_omitted(self):
        original = self.report['checks']
        for name in ('defender-scan-history-contract', 'ui-msi-action-binding-interaction'):
            with self.subTest(name=name):
                self.report['checks']=[check for check in original if check['name']!=name]
                self.save()
                self.rejected()
        self.report['checks']=original
    def test_msi_action_binding_contract_cannot_be_omitted(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='msi-action-binding-contract']
        self.save()
        self.rejected()
    def test_diagnostic_channel_interaction_cannot_be_omitted(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='ui-diagnostic-channel-coverage-interaction']
        self.save()
        self.rejected()
    def test_mapped_disposition_evidence_cannot_be_omitted(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='cleanup-mapped-disposition-owned-probe']
        self.save()
        self.rejected()
    def test_independent_builtin_import_control_cannot_be_omitted(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='dism-builtin-import-control-contract']
        self.save()
        self.rejected()
    def test_owned_disposition_evidence_cannot_be_omitted(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='cleanup-disposition-owned-probe']
        self.save()
        self.rejected()
    def test_populated_cleanup_interaction_checks_cannot_be_omitted(self):
        for name in ('ui-cleanup-file-evidence-interaction','ui-cleanup-file-evidence-selection-race','ui-cleanup-file-evidence-cancellation'):
            with self.subTest(name=name):
                original=copy.deepcopy(self.report)
                self.report['checks']=[check for check in self.report['checks'] if check['name']!=name]
                self.save()
                self.rejected()
                self.report=original
    def test_cleanup_file_evidence_checks_cannot_be_omitted(self):
        for name in ('cleanup-file-evidence-fixture','ui-cleanup-file-evidence'):
            with self.subTest(name=name):
                original=copy.deepcopy(self.report)
                self.report['checks']=[check for check in self.report['checks'] if check['name']!=name]
                self.save()
                self.rejected()
                self.report=original
    def test_versioned_journal_and_dism_metadata_evidence_cannot_be_omitted(self):
        for name in ('system-tool-journal-lifecycle-fixture','dism-powershell-capability-contract'):
            with self.subTest(name=name):
                original=copy.deepcopy(self.report)
                self.report['checks']=[check for check in self.report['checks'] if check['name']!=name]
                self.save()
                self.rejected()
                self.report=original
    def test_cross_instance_store_evidence_cannot_be_omitted(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='system-tool-journal-cross-instance-fixture']
        self.save()
        self.rejected()
    def test_journal_store_evidence_cannot_be_omitted(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='system-tool-journal-store-fixture']
        self.save()
        self.rejected()
    def test_namespace_observation_cannot_be_omitted(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='startup-transaction-namespace-observation']
        self.save()
        self.rejected()
    def test_passing_report_cannot_omit_a_directory_guard_profile(self):
        for name in ('shortcut-rename-source-guard','shortcut-rename-destination-guard','shortcut-rename-ancestor-guard'):
            with self.subTest(name=name):
                original=self.report['checks']
                self.report['checks']=[check for check in original if check['name']!=name]
                self.save(); self.rejected()
                self.report['checks']=original
    def test_baseline_success_does_not_promote_an_unverified_directory_guard(self):
        next(check for check in self.report['checks'] if check['name']=='shortcut-rename-destination-guard').update(state='inconclusive',detail='TEST-ONLY guard compatibility unverified; no operation')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='shortcut-rename-destination-guard')['state'])
        self.assertEqual('passed',next(check for check in published['checks'] if check['name']=='shortcut-rename-nt-control')['state'])
    def test_unverified_dacl_facts_remain_inconclusive_with_other_checks_passed(self):
        next(check for check in self.report['checks'] if check['name']=='shortcut-dacl-fixture').update(state='inconclusive',detail='TEST-ONLY descriptor facts unverified; no native query')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='shortcut-dacl-fixture')['state'])
    def test_passing_report_cannot_omit_shortcut_evidence_panel_or_image(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='ui-shortcut-file-evidence']
        self.save(); self.rejected()
    def test_shortcut_evidence_initial_image_is_required(self):
        (self.download/'tool-26.png').unlink()
        self.rejected()
    def test_pending_native_nt_control_cannot_be_promoted_by_other_proofs(self):
        proof=next(check for check in self.report['checks'] if check['name']=='shortcut-rename-nt-control')
        proof.update(state='inconclusive',detail='TEST-ONLY native terminal outcome unverified; no native operation')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='shortcut-rename-nt-control')['state'])
    def test_native_nt_control_cannot_promote_unknown_public_win32_backends(self):
        for name in ('shortcut-rename-handle-probe','shortcut-rename-cpp-control'):
            next(check for check in self.report['checks'] if check['name']==name).update(state='inconclusive',detail='TEST-ONLY public Win32 result unverified; no native operation')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('passed',next(check for check in published['checks'] if check['name']=='shortcut-rename-nt-control')['state'])
        for name in ('shortcut-rename-handle-probe','shortcut-rename-cpp-control'):
            self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']==name)['state'])
    def test_mapping_witness_cannot_promote_unverified_rename_backends(self):
        for name in ('shortcut-rename-handle-probe','shortcut-rename-cpp-control'):
            proof=next(check for check in self.report['checks'] if check['name']==name)
            proof.update(state='inconclusive',detail='TEST-ONLY native rename not established; no native operation performed')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('passed',next(check for check in published['checks'] if check['name']=='shortcut-mapped-write-witness')['state'])
        for name in ('shortcut-rename-handle-probe','shortcut-rename-cpp-control'):
            self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']==name)['state'])
    def test_passing_report_cannot_omit_independent_cpp_shortcut_control(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='shortcut-rename-cpp-control']
        self.save(); self.rejected()
    def test_cpp_control_cannot_promote_unverified_managed_rename(self):
        managed=next(check for check in self.report['checks'] if check['name']=='shortcut-rename-handle-probe')
        managed.update(state='inconclusive',detail='TEST-ONLY managed Win32 outcome unverified; no native operation performed')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('passed',next(check for check in published['checks'] if check['name']=='shortcut-rename-cpp-control')['state'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='shortcut-rename-handle-probe')['state'])
    def test_passing_report_cannot_omit_owned_shortcut_primitive_proofs(self):
        for name in ('shortcut-rename-handle-probe','shortcut-mapped-write-witness'):
            with self.subTest(name=name):
                original=list(self.report['checks'])
                self.report['checks']=[check for check in original if check['name']!=name]
                self.save(); self.rejected()
                self.report['checks']=original
    def test_unobserved_owned_shortcut_or_mapping_cannot_become_overall_pass(self):
        for name in ('shortcut-rename-handle-probe','shortcut-mapped-write-witness'):
            with self.subTest(name=name):
                proof=next(check for check in self.report['checks'] if check['name']==name)
                proof.update(state='inconclusive',detail='TEST-ONLY native primitive unavailable; no actual fixture operations')
                self.report.update(passed=False,state='inconclusive'); self.save()
                _, summary, files=self.prepare()
                self.assertFalse(summary['passed'])
                published=json.loads(files['report.json'])
                self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']==name)['state'])
                proof.update(state='passed',detail='TEST-ONLY fixture evidence')
    def test_passing_report_cannot_omit_actual_inspection_ui_lifecycle(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='ui-inspection-evidence']
        self.save(); self.rejected()
    def test_unobserved_inspection_ui_cancel_lifecycle_cannot_become_pass(self):
        evidence=next(check for check in self.report['checks'] if check['name']=='ui-inspection-evidence')
        evidence.update(state='inconclusive',detail='TEST-ONLY cancellation/reset observation unavailable; no query performed')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='ui-inspection-evidence')['state'])
    def test_passing_report_cannot_omit_inspection_evidence_contracts(self):
        for name in ('storage-inspection-evidence-contract','defender-inspection-evidence-contract'):
            with self.subTest(name=name):
                original=list(self.report['checks'])
                self.report['checks']=[check for check in original if check['name']!=name]
                self.save(); self.rejected()
                self.report['checks']=original
    def test_unavailable_inspection_evidence_cannot_become_overall_pass(self):
        evidence=next(check for check in self.report['checks'] if check['name']=='defender-inspection-evidence-contract')
        evidence.update(state='inconclusive',detail='TEST-ONLY missing actual status source; no query performed')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='defender-inspection-evidence-contract')['state'])
    def test_missing_inspection_evidence_image_is_rejected(self):
        (self.download/'tool-25.png').unlink()
        self.save(); self.rejected()
    def test_passing_report_cannot_omit_held_catalog_observation(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='system-tool-held-catalog-observation']
        self.save(); self.rejected()
    def test_unverified_held_catalog_preserves_inconclusive_result(self):
        held=next(check for check in self.report['checks'] if check['name']=='system-tool-held-catalog-observation')
        held.update(state='inconclusive',detail='TEST-ONLY held catalog native wait pending; no query performed')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='system-tool-held-catalog-observation')['state'])
    def test_passing_report_cannot_omit_isolated_catalog_observation(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='system-tool-catalog-observation']
        self.save(); self.rejected()
    def test_catalog_no_candidate_remains_inconclusive_despite_other_passing_checks(self):
        catalog=next(check for check in self.report['checks'] if check['name']=='system-tool-catalog-observation')
        catalog.update(state='inconclusive',detail='TEST-ONLY no first catalog observed; no native lookup performed')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='system-tool-catalog-observation')['state'])
    def test_valid_store_count_does_not_promote_inconclusive_wer_enumeration(self):
        read=next(check for check in self.report['checks'] if check['name']=='wer-store-read-contract')
        read.update(state='inconclusive',detail='TEST-ONLY null enumeration key; independent count success is not enumeration proof')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('passed',next(check for check in published['checks'] if check['name']=='wer-store-count-contract')['state'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='wer-store-read-contract')['state'])
    def test_passing_report_cannot_omit_actual_installed_app_search_proof(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='ui-installed-app-search']
        self.save(); self.rejected()
    def test_passing_report_cannot_omit_populated_package_ui_proof(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='ui-packaged-app-search']
        self.save(); self.rejected()
    def test_package_provider_success_cannot_promote_inconclusive_package_ui(self):
        package_ui=next(check for check in self.report['checks'] if check['name']=='ui-packaged-app-search')
        package_ui.update(state='inconclusive',detail='TEST-ONLY package UI supplied no usable rows; independent provider contract may pass')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='ui-packaged-app-search')['state'])
    def test_search_without_actual_rows_stays_inconclusive_despite_green_jobs(self):
        search=next(check for check in self.report['checks'] if check['name']=='ui-installed-app-search')
        search.update(state='inconclusive',detail='TEST-ONLY actual registration query returned no usable rows')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='ui-installed-app-search')['state'])
    def test_passing_report_cannot_omit_isolated_transaction_probe(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='startup-transaction-handle-probe']
        self.save(); self.rejected()
    def test_passing_report_cannot_omit_isolated_interference_probe(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='startup-transaction-interference-probe']
        self.save(); self.rejected()
    def test_nonqualifying_interference_sequence_remains_inconclusive(self):
        probe=next(check for check in self.report['checks'] if check['name']=='startup-transaction-interference-probe')
        probe.update(state='inconclusive',detail='TEST-ONLY no pre-cleanup abort evidence; no native activity')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='startup-transaction-interference-probe')['state'])
    def test_pending_isolated_transaction_cannot_be_promoted_by_green_jobs(self):
        probe=next(check for check in self.report['checks'] if check['name']=='startup-transaction-handle-probe')
        probe.update(state='inconclusive',detail='TEST-ONLY pending fixture worker; owned tree retained')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='startup-transaction-handle-probe')['state'])
    def test_passing_report_cannot_omit_actual_msi_panel_capture(self):
        self.report['screenshots']=[image for image in self.report['screenshots'] if image['file']!='tool-20.png']
        self.save(); self.rejected()
    def test_passing_report_cannot_omit_actual_package_panel_capture(self):
        self.report['screenshots']=[image for image in self.report['screenshots'] if image['file']!='tool-21.png']
        self.save(); self.rejected()
    def test_passing_report_cannot_omit_duplicate_pair_worksheet_capture(self):
        self.report['screenshots']=[image for image in self.report['screenshots'] if image['file']!='tool-22.png']
        self.save(); self.rejected()
    def test_passing_report_cannot_omit_selected_shortcut_recheck_capture(self):
        self.report['screenshots']=[image for image in self.report['screenshots'] if image['file']!='tool-23.png']
        self.save(); self.rejected()
    def test_passing_report_cannot_omit_actual_shortcut_recheck_fixture(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='shortcut-selected-recheck-fixture']
        self.save(); self.rejected()
    def test_passing_report_cannot_omit_actual_system_tool_identity(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='system-tool-identity-contract']
        self.save(); self.rejected()
    def test_passing_report_cannot_omit_actual_embedded_version_evidence(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='system-tool-embedded-version-fixture']
        self.save(); self.rejected()
    def test_signature_proof_cannot_promote_missing_embedded_metadata(self):
        version=next(check for check in self.report['checks'] if check['name']=='system-tool-embedded-version-fixture')
        version.update(state='inconclusive',detail='TEST-ONLY held file has no qualified embedded original-name evidence; signature alone is insufficient')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='system-tool-embedded-version-fixture')['state'])
    def test_unavailable_signature_identity_cannot_be_promoted_by_disabled_launch_gate(self):
        identity=next(check for check in self.report['checks'] if check['name']=='system-tool-identity-contract')
        identity.update(state='inconclusive',detail='TEST-ONLY genuine identity unavailable; disabled launch is not signature proof')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='system-tool-identity-contract')['state'])
    def test_shortcut_recheck_without_real_current_evidence_stays_inconclusive(self):
        shortcut=next(check for check in self.report['checks'] if check['name']=='shortcut-selected-recheck-fixture')
        shortcut.update(state='inconclusive',detail='TEST-ONLY current selected metadata unavailable; historical scan cannot substitute')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='shortcut-selected-recheck-fixture')['state'])
    def test_passing_report_cannot_omit_actual_duplicate_pair_fixture(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='duplicate-pair-review-fixture']
        self.save(); self.rejected()
    def test_missing_independent_copy_cannot_become_positive_pair_proof(self):
        pair=next(check for check in self.report['checks'] if check['name']=='duplicate-pair-review-fixture')
        pair.update(state='inconclusive',detail='TEST-ONLY no independent owned-fixture copy with required typed evidence')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='duplicate-pair-review-fixture')['state'])
    def test_passing_report_cannot_omit_current_user_package_proof(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='packaged-application-contract']
        self.save(); self.rejected()
    def test_empty_package_query_does_not_become_positive_projection_proof(self):
        package=next(check for check in self.report['checks'] if check['name']=='packaged-application-contract')
        package.update(state='inconclusive',detail='TEST-ONLY empty completed package query; no positive-row projection evidence')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='packaged-application-contract')['state'])
    def test_selected_msi_inconclusive_cannot_be_promoted_by_other_passing_evidence(self):
        msi=next(check for check in self.report['checks'] if check['name']=='msi-registration-contract')
        msi.update(state='inconclusive',detail='TEST-ONLY no exact selected MSI match; no real metadata')
        self.report.update(passed=False,state='inconclusive'); self.save()
        _, summary, files=self.prepare()
        self.assertFalse(summary['passed'])
        published=json.loads(files['report.json'])
        self.assertEqual('inconclusive',next(check for check in published['checks'] if check['name']=='msi-registration-contract')['state'])
    def test_contradictory_pass_and_check_state_is_rejected(self):
        self.report['checks'][0]['state']='failed'; self.save(); self.rejected()
    def test_owned_private_parent_security_preflight_is_required_for_pass(self):
        self.report['checks'] = [check for check in self.report['checks'] if check['name'] != 'owned-private-parent-security-preflight']
        self.save(); self.rejected()
    def test_v2_passed_report_requires_structured_winget_contract_even_when_legacy_check_is_present(self):
        self.report['formatVersion'] = 2
        self.report['checks'] = [check for check in self.report['checks'] if check['name'] != 'structured-winget-discovery-contract']
        self.save(); self.rejected()

    def test_legacy_v1_preserves_acceptance_without_structured_winget_contract(self):
        self.assertEqual(1, self.report['formatVersion'])
        _, summary, _ = self.prepare()
        self.assertTrue(summary['passed'])

    def test_v2_accepts_separate_structured_check_without_relabeling_other_checks(self):
        self.report['formatVersion'] = 2
        self.report['checks'].append({'name': 'structured-winget-discovery-contract', 'state': 'passed', 'detail': 'TEST-ONLY fixed diagnostic evidence'})
        self.save()
        _, summary, _ = self.prepare()
        self.assertTrue(summary['passed'])

    def test_v3_requires_published_query_proof_even_with_development_sdk_check(self):
        self.report['formatVersion'] = 3
        self.report['checks'].append({'name': 'structured-winget-discovery-contract', 'state': 'passed', 'detail': 'TEST-ONLY development query'})
        self.save(); self.rejected()

    def test_v3_accepts_both_sdk_checks_without_changing_legacy_acceptance(self):
        self.report['formatVersion'] = 3
        for name in ('structured-winget-discovery-contract', 'structured-winget-published-query-contract'):
            self.report['checks'].append({'name': name, 'state': 'passed', 'detail': 'TEST-ONLY fixed proof'})
        self.save()
        _, summary, _ = self.prepare()
        self.assertTrue(summary['passed'])

    def test_v4_cannot_substitute_direct_sdk_proofs_for_actual_app_controls(self):
        self.report['formatVersion'] = 4
        for name in ('structured-winget-discovery-contract', 'structured-winget-published-query-contract'):
            self.report['checks'].append({'name': name, 'state': 'passed', 'detail': 'TEST-ONLY SDK path'})
        self.save(); self.rejected()

    def test_v4_accepts_separate_actual_app_check_and_preserves_legacy_versions(self):
        self.report['formatVersion'] = 4
        for name in ('structured-winget-discovery-contract', 'structured-winget-published-query-contract', 'app-update-public-ui-contract'):
            self.report['checks'].append({'name': name, 'state': 'passed', 'detail': 'TEST-ONLY fixed proof'})
        self.save()
        _, summary, _ = self.prepare()
        self.assertTrue(summary['passed'])

    def test_v5_cannot_substitute_identity_or_update_proofs_for_sfc_preview_interaction(self):
        self.report['formatVersion'] = 5
        for name in ('structured-winget-discovery-contract', 'structured-winget-published-query-contract', 'app-update-public-ui-contract'):
            self.report['checks'].append({'name': name, 'state': 'passed', 'detail': 'TEST-ONLY earlier workflow proof'})
        self.save(); self.rejected()

    def test_v5_accepts_separate_sfc_preview_proof_without_requiring_it_from_v4(self):
        self.report['formatVersion'] = 5
        for name in ('structured-winget-discovery-contract', 'structured-winget-published-query-contract', 'app-update-public-ui-contract', 'ui-sfc-preview-interaction'):
            self.report['checks'].append({'name': name, 'state': 'passed', 'detail': 'TEST-ONLY fixed workflow proof'})
        self.save()
        _, summary, files = self.prepare()
        self.assertTrue(summary['passed'])
        self.assertEqual(5, json.loads(files['report.json'])['formatVersion'])
        self.report['formatVersion'] = 4
        self.report['checks'] = [check for check in self.report['checks'] if check['name'] != 'ui-sfc-preview-interaction']
        self.save()
        _, summary, _ = self.prepare()
        self.assertTrue(summary['passed'])

    def test_v5_unknown_sfc_preview_cannot_be_claimed_as_passed(self):
        self.report['formatVersion'] = 5
        for name in ('structured-winget-discovery-contract', 'structured-winget-published-query-contract', 'app-update-public-ui-contract'):
            self.report['checks'].append({'name': name, 'state': 'passed', 'detail': 'TEST-ONLY earlier workflow proof'})
        self.report['checks'].append({'name': 'ui-sfc-preview-interaction', 'state': 'inconclusive', 'detail': 'TEST-ONLY missing accepted identity; no preview result'})
        self.save(); self.rejected()
        self.report.update(passed=False, state='inconclusive')
        self.save()
        _, summary, files = self.prepare()
        self.assertFalse(summary['passed'])
        published = json.loads(files['report.json'])
        self.assertEqual('inconclusive', next(check for check in published['checks'] if check['name'] == 'ui-sfc-preview-interaction')['state'])

    def test_v6_cannot_substitute_hardware_metadata_for_actual_inventory_interaction(self):
        self.report['formatVersion'] = 6
        for name in ('structured-winget-discovery-contract', 'structured-winget-published-query-contract', 'app-update-public-ui-contract', 'ui-sfc-preview-interaction'):
            self.report['checks'].append({'name': name, 'state': 'passed', 'detail': 'TEST-ONLY earlier workflow proof'})
        self.assertTrue(any(check['name'] == 'hardware-metadata-contract' for check in self.report['checks']))
        self.save(); self.rejected()

    def test_v6_accepts_actual_hardware_interaction_without_requiring_it_from_v5(self):
        self.report['formatVersion'] = 6
        for name in ('structured-winget-discovery-contract', 'structured-winget-published-query-contract', 'app-update-public-ui-contract', 'ui-sfc-preview-interaction', 'ui-hardware-inventory-interaction'):
            self.report['checks'].append({'name': name, 'state': 'passed', 'detail': 'TEST-ONLY fixed workflow proof'})
        self.save()
        _, summary, files = self.prepare()
        self.assertTrue(summary['passed'])
        self.assertEqual(6, json.loads(files['report.json'])['formatVersion'])
        self.report['formatVersion'] = 5
        self.report['checks'] = [check for check in self.report['checks'] if check['name'] != 'ui-hardware-inventory-interaction']
        self.save()
        _, summary, files = self.prepare()
        self.assertTrue(summary['passed'])
        self.assertEqual(5, json.loads(files['report.json'])['formatVersion'])

    def test_v6_unknown_hardware_workflow_cannot_be_claimed_as_passed(self):
        self.report['formatVersion'] = 6
        for name in ('structured-winget-discovery-contract', 'structured-winget-published-query-contract', 'app-update-public-ui-contract', 'ui-sfc-preview-interaction'):
            self.report['checks'].append({'name': name, 'state': 'passed', 'detail': 'TEST-ONLY earlier workflow proof'})
        self.report['checks'].append({'name': 'ui-hardware-inventory-interaction', 'state': 'inconclusive', 'detail': 'TEST-ONLY hardware provider unavailable; no populated result'})
        self.save(); self.rejected()
        self.report.update(passed=False, state='inconclusive')
        self.save()
        _, summary, files = self.prepare()
        self.assertFalse(summary['passed'])
        published = json.loads(files['report.json'])
        self.assertEqual('inconclusive', next(check for check in published['checks'] if check['name'] == 'ui-hardware-inventory-interaction')['state'])

    def test_standard_report_versions_are_closed_and_strictly_integer(self):
        for value in (0, 8, True, '7', 7.0, None):
            with self.subTest(version=value):
                self.report['formatVersion'] = value
                self.save(); self.rejected()

    def test_v7_requires_metadata_workflow_separately_from_discovery(self):
        self.report['formatVersion'] = 7
        for name in ('structured-winget-discovery-contract', 'structured-winget-published-query-contract', 'app-update-public-ui-contract', 'ui-sfc-preview-interaction', 'ui-hardware-inventory-interaction'):
            self.report['checks'].append({'name': name, 'state': 'passed', 'detail': 'TEST-ONLY earlier workflow proof'})
        self.save(); self.rejected()

    def test_v7_accepts_metadata_proof_and_preserves_v6_scope(self):
        self.report['formatVersion'] = 7
        for name in ('structured-winget-discovery-contract', 'structured-winget-published-query-contract', 'app-update-public-ui-contract', 'ui-sfc-preview-interaction', 'ui-hardware-inventory-interaction', 'app-package-metadata-public-ui-contract'):
            self.report['checks'].append({'name': name, 'state': 'passed', 'detail': 'TEST-ONLY separate workflow proof'})
        self.save()
        _, summary, files = self.prepare()
        self.assertTrue(summary['passed'])
        self.assertEqual(7, json.loads(files['report.json'])['formatVersion'])
        self.report['formatVersion'] = 6
        self.report['checks'] = [check for check in self.report['checks'] if check['name'] != 'app-package-metadata-public-ui-contract']
        self.save()
        _, summary, _ = self.prepare()
        self.assertTrue(summary['passed'])

    def test_v7_unknown_metadata_cannot_be_promoted_by_discovery_success(self):
        self.report['formatVersion'] = 7
        for name in ('structured-winget-discovery-contract', 'structured-winget-published-query-contract', 'app-update-public-ui-contract', 'ui-sfc-preview-interaction', 'ui-hardware-inventory-interaction'):
            self.report['checks'].append({'name': name, 'state': 'passed', 'detail': 'TEST-ONLY earlier workflow proof'})
        self.report['checks'].append({'name': 'app-package-metadata-public-ui-contract', 'state': 'inconclusive', 'detail': 'TEST-ONLY no current Available offer or accepted metadata'})
        self.save(); self.rejected()
        self.report.update(passed=False, state='inconclusive')
        self.save()
        _, summary, files = self.prepare()
        self.assertFalse(summary['passed'])
        self.assertEqual('inconclusive', next(check for check in json.loads(files['report.json'])['checks'] if check['name'] == 'app-package-metadata-public-ui-contract')['state'])

    def test_passed_report_requires_same_query_winget_observation(self):
        self.report['checks'] = [check for check in self.report['checks'] if check['name'] != 'winget-query-observation-contract']
        self.save(); self.rejected()
    def test_duplicate_check_names_are_rejected(self):
        self.report['checks'].append(copy.deepcopy(self.report['checks'][0])); self.save(); self.rejected()
    def test_ill_typed_check_state_is_rejected_safely(self):
        self.report['checks'][0]['state']=[]; self.save(); self.rejected()
    def test_source_run_and_attempt_must_match_actual_ci_context(self):
        for field, value in [('sourceCommit','b'*40),('runId','99'),('runAttempt',2),('runAttempt',True)]:
            old=self.report[field]; self.report[field]=value; self.save(); self.rejected(); self.report[field]=old
    def test_fork_pull_request_and_other_branch_cannot_publish(self):
        for key,value in [('GITHUB_REPOSITORY','other/MadClean'),('GITHUB_EVENT_NAME','pull_request'),('GITHUB_REF','refs/heads/main')]:
            old=self.env[key]; self.env[key]=value; self.rejected(); self.env[key]=old
    def test_digest_mismatch_is_rejected_without_copying_untrusted_image(self):
        (self.download/p.IMAGES[0]).write_bytes(png()+b'changed'); self.rejected()
    def test_matching_hash_does_not_hide_corrupt_png_chunks(self):
        data=bytearray(png()); data[-5]^=1
        (self.download/p.IMAGES[0]).write_bytes(data)
        self.report['screenshots'][0]['sha256']=hashlib.sha256(data).hexdigest(); self.save(); self.rejected()
    def test_dimensions_must_match_the_actual_png(self):
        self.report['screenshots'][0]['width']=101; self.save(); self.rejected()
    def test_passing_report_requires_all_current_capture_files(self):
        self.report['screenshots'].pop(); self.save(); self.rejected()
    def test_extra_unlisted_files_are_not_exported(self):
        (self.download/'private-output.txt').write_text('TEST-ONLY must not be exported')
        self.assertNotIn('private-output.txt',self.prepare()[2])
    def test_path_traversal_and_duplicate_image_entries_are_rejected(self):
        old=self.report['screenshots'][0]['file']; self.report['screenshots'][0]['file']='../private.png'; self.save(); self.rejected()
        self.report['screenshots'][0]['file']=old; self.report['screenshots'].append(copy.deepcopy(self.report['screenshots'][0])); self.save(); self.rejected()
    def test_newer_run_or_attempt_is_never_overwritten(self):
        for run,attempt in [('101',1),('100',2)]:
            (self.root/'latest.json').write_text(json.dumps({'formatVersion':1,'runId':run,'runAttempt':attempt})); self.rejected()
    def test_malformed_json_and_duplicate_properties_are_rejected(self):
        for data in ['not-json','{"formatVersion":1,"formatVersion":1}']:
            (self.download/'report.json').write_text(data); self.rejected()
    def test_wrong_origin_or_branch_is_rejected(self):
        subprocess.run(['git','-C',str(self.root),'remote','set-url','origin','https://github.com/other/MadClean.git'],check=True,capture_output=True); self.rejected()
    @unittest.skipUnless(hasattr(os,'symlink'),'Requires symbolic links')
    def test_symbolic_download_or_destination_is_not_followed(self):
        target=Path(self.directory.name)/'outside.txt'; target.write_text('TEST-ONLY retained')
        try: (self.root/'report.json').symlink_to(target)
        except OSError: self.skipTest('Symlink creation unavailable')
        self.rejected(); self.assertEqual('TEST-ONLY retained',target.read_text())

    def test_publication_removes_only_fixed_stale_generated_outputs(self):
        (self.root/'overview-wide.png').write_bytes(png())
        (self.root/'private-note.txt').write_text('TEST-ONLY retained')
        self.report.update(passed=False,state='failed',checks=[{'name':'ui-capture','state':'failed','detail':'TEST-ONLY early failure'}],screenshots=[]); self.save()
        root, summary, files = self.prepare(verify='failure')
        with patch.object(p,'git',return_value=''):
            p.publish(root,files,self.env['GITHUB_SHA'])
        self.assertFalse((self.root/'overview-wide.png').exists())
        self.assertTrue((self.root/'report.json').exists())
        self.assertEqual('TEST-ONLY retained',(self.root/'private-note.txt').read_text())
    def test_trx_records_actual_failure_without_exporting_stack_or_other_files(self):
        (self.download/'core-windows.trx').write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testName="Example" outcome="Failed"><Output><ErrorInfo><Message>TEST-ONLY assertion</Message><StackTrace>not retained</StackTrace></ErrorInfo></Output></UnitTestResult></Results><ResultSummary><Counters total="3" executed="2" passed="1" failed="1" notExecuted="1"/></ResultSummary></TestRun>')
        result=p.core_test_summary(self.download)
        self.assertEqual(1,result['counts']['failed']); self.assertEqual('Example',result['failures'][0]['name'])
        self.assertNotIn('not retained',json.dumps(result))
    def test_trx_external_entity_is_refused(self):
        (self.download/'core-windows.trx').write_text('<!DOCTYPE x [<!ENTITY y SYSTEM "file:///private">]><x/>')
        with self.assertRaises(p.EvidenceError): p.core_test_summary(self.download)

    def test_failed_server_integrity_outcome_never_admits_extracted_evidence(self):
        root, summary, files = p.prepare(self.root,self.download,'success','success',self.env,report_download_result='failure')
        self.assertFalse(summary['passed']); self.assertFalse(summary['reportPresent'])
        self.assertEqual({'latest.json'},set(files))
    def test_failed_core_download_never_admits_extracted_trx(self):
        (self.download/'core-windows.trx').write_text('unverified extracted data')
        _, summary, files = p.prepare(self.root,self.download,'success','success',self.env,core_directory=self.download,core_download_result='failure')
        self.assertFalse(summary['coreTestsPresent']); self.assertNotIn('core-tests.json',files)

    def test_zero_test_and_inconsistent_counters_never_allow_overall_pass(self):
        for counters in ['total="0" executed="0" passed="0" failed="0" notExecuted="0"','total="3" executed="3" passed="1" failed="0" notExecuted="0"']:
            (self.download/'core-windows.trx').write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary><Counters '+counters+'/></ResultSummary></TestRun>')
            self.assertFalse(self.prepare()[1]['passed'])
    def test_missing_core_evidence_never_allows_overall_pass(self):
        (self.download/'core-windows.trx').unlink()
        self.assertFalse(self.prepare()[1]['passed'])
    def test_trx_reconciles_vstest_zero_raw_not_executed_against_actual_records(self):
        (self.download/'core-windows.trx').write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult outcome="Passed"/><UnitTestResult outcome="Passed"/><UnitTestResult outcome="NotExecuted"/></Results><ResultSummary><Counters total="3" executed="2" passed="2" failed="0" notExecuted="0" error="0" warning="0" pending="0"/></ResultSummary></TestRun>')
        core=p.core_test_summary(self.download)
        self.assertEqual({'Passed':2,'NotExecuted':1},core['resultOutcomeTally'])
        self.assertEqual(3,core['resultRecordCount'])
        self.assertEqual(0,core['counterAttributes']['notExecuted'])
        self.assertEqual(0,core['counterAttributes']['warning'])
        self.assertTrue(core['countsConsistent'])
        self.assertTrue(core['passed'])
        self.assertIn('not necessarily an intentional skip',core['accountingNote'])
    def test_known_counter_skips_are_reported_separately_from_passed_tests(self):
        (self.download/'core-windows.trx').write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult outcome="Passed"/><UnitTestResult outcome="NotExecuted"/></Results><ResultSummary><Counters total="2" executed="1" passed="1" failed="0" notExecuted="1"/></ResultSummary></TestRun>')
        core=p.core_test_summary(self.download)
        self.assertTrue(core['countsConsistent'])
        self.assertEqual(1,core['counts']['passed'])
        self.assertEqual(1,core['resultOutcomeTally']['NotExecuted'])
    def test_trx_diagnostics_never_forward_unknown_metadata_or_outcome_text(self):
        (self.download/'core-windows.trx').write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult outcome="TEST-private-payload"/><UnitTestResult/></Results><ResultSummary><Counters total="2" executed="0" passed="0" failed="0" notExecuted="2" private="TEST-private-payload"/></ResultSummary></TestRun>')
        core=p.core_test_summary(self.download)
        self.assertEqual({'Unknown':2},core['resultOutcomeTally'])
        self.assertEqual(1,core['unrecognizedCounterAttributeCount'])
        self.assertNotIn('TEST-private-payload',json.dumps(core))
        self.assertFalse(core['passed'])
    def test_missing_or_invalid_trx_counters_still_reject(self):
        for attributes in ['total="2" executed="1" passed="1" failed="0"', 'total="2" executed="1" passed="1" failed="0" notExecuted="1" error="private"']:
            (self.download/'core-windows.trx').write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary><Counters '+attributes+'/></ResultSummary></TestRun>')
            with self.assertRaisesRegex(p.EvidenceError,'^invalid-trx-counter$'): p.core_test_summary(self.download)
    def test_trx_incomplete_unknown_or_contradictory_results_cannot_pass(self):
        for records, counters in [
            ('', 'total="1" executed="1" passed="1" failed="0" notExecuted="0"'),
            ('<UnitTestResult outcome="Unknown"/>', 'total="1" executed="1" passed="1" failed="0" notExecuted="0"'),
            ('<UnitTestResult outcome="Error"/>', 'total="1" executed="1" passed="1" failed="0" notExecuted="0"'),
            ('<UnitTestResult outcome="Passed"/>', 'total="1" executed="1" passed="0" failed="1" notExecuted="0"'),
            ('<UnitTestResult outcome="Passed"/>', 'total="1" executed="1" passed="1" failed="0" notExecuted="1"'),
            ('<UnitTestResult outcome="Passed"/>', 'total="1" executed="1" passed="1" failed="0" notExecuted="0" error="1"'),
        ]:
            with self.subTest(records=records,counters=counters):
                (self.download/'core-windows.trx').write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>'+records+'</Results><ResultSummary><Counters '+counters+'/></ResultSummary></TestRun>')
                core=p.core_test_summary(self.download)
                self.assertFalse(core['countsConsistent'])
                self.assertFalse(self.prepare()[1]['passed'])
    def test_fully_accounted_but_unexecuted_run_cannot_pass(self):
        (self.download/'core-windows.trx').write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult outcome="NotExecuted"/></Results><ResultSummary><Counters total="1" executed="0" passed="0" failed="0" notExecuted="0"/></ResultSummary></TestRun>')
        core=p.core_test_summary(self.download)
        self.assertTrue(core['countsConsistent'])
        self.assertFalse(core['passed'])
        self.assertFalse(self.prepare()[1]['passed'])
    def test_reconciled_test_results_still_require_successful_actual_job_results(self):
        self.assertFalse(self.prepare(build='failure')[1]['passed'])
        self.assertFalse(self.prepare(verify='cancelled')[1]['passed'])
    def test_utf16_trx_doctype_is_rejected_before_any_entity_expansion(self):
        data='<?xml version="1.0" encoding="utf-16"?><!DOCTYPE x [<!ENTITY a "TEST-ONLY">]><x>&a;</x>'
        (self.download/'core-windows.trx').write_bytes(data.encode('utf-16'))
        with self.assertRaises(p.EvidenceError): p.core_test_summary(self.download)

    def test_unexpected_report_check_or_image_fields_cannot_export_private_payloads(self):
        for target in (self.report,self.report['checks'][0],self.report['screenshots'][0]):
            target['providerOutput']='TEST-ONLY private payload'; self.save(); self.rejected(); del target['providerOutput']
    def test_unknown_limits_text_is_not_forwarded(self):
        self.report['limits']='TEST-ONLY must not be forwarded'; self.save()
        report=json.loads(self.prepare()[2]['report.json'])
        self.assertNotIn('TEST-ONLY must not be forwarded',report['limits'])
    def test_unparseable_or_reversed_report_times_are_rejected(self):
        self.report['startedUtc']='private path instead of time'; self.save(); self.rejected()
        self.report['startedUtc']='2026-10-27T00:00:00Z'; self.report['completedUtc']='2026-10-26T00:00:00Z'; self.save(); self.rejected()

if __name__=='__main__': unittest.main()
