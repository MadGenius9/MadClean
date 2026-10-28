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
    def test_complete_evidence_binds_real_files_and_changes_nothing_during_validation(self):
        _, summary, files = self.prepare()
        self.assertTrue(summary['passed'])
        self.assertEqual(set(p.OWNED) - {"ci-diagnostics.json", "native-ci-diagnostics.json"},set(files))
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
    def test_passing_report_cannot_omit_actual_installed_app_search_proof(self):
        self.report['checks']=[check for check in self.report['checks'] if check['name']!='ui-installed-app-search']
        self.save(); self.rejected()
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
