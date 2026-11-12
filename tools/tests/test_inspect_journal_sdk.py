"""Synthetic header-text/owned-file checks; no installed Windows SDK is inspected."""
import copy
import hashlib
import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location("journal_sdk_reference", Path(__file__).parents[1] / "inspect_journal_sdk.py")
sdk = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sdk)


class SyntheticSdkReferenceTests(unittest.TestCase):
    def test_code_comment_string_and_identifier_collisions_remain_distinct(self):
        fixture = b'// synthetic fixture only: CreateDirectory2W\nconst char *s="CreateFile3";\nint CreateFile3Extra;\nHANDLE CreateDirectory2W(void);\n'
        result = sdk.parse_header(fixture)
        symbols = {row["symbol"]: row for row in result["symbols"]}
        self.assertEqual(2, symbols["CreateDirectory2W"]["matchingLineCount"])
        self.assertEqual(1, symbols["CreateFile3"]["matchingLineCount"])
        mentions = {fragment["matchLine"]: fragment["mentions"][0]["kind"] for fragment in result["fragments"]}
        self.assertEqual({1: "comment-or-string-only", 2: "comment-or-string-only", 4: "code-token"}, mentions)
        self.assertEqual("unknown-not-observed", symbols["DIRECTORY_FLAGS"]["state"])

    def test_actual_conditional_branches_retained_without_evaluating_os_support(self):
        fixture = b'// synthetic fixture only\n#if SOME_UNKNOWN_TARGET\n#ifdef OTHER_GUARD\nDIRECTORY_FLAGS f;\n#else\nCreateDirectory2W();\n#endif\n#endif\n'
        result = sdk.parse_header(fixture)
        fragment = next(item for item in result["fragments"] if item["matchLine"] == 6)
        self.assertEqual([2, 3, 5], [item["line"] for item in fragment["preprocessorContext"]])
        self.assertFalse(fragment["preprocessorContextUnknown"])
        self.assertEqual("not-evaluated", result["preprocessorInterpretation"])

    def test_prefixed_redirect_flag_is_preserved_as_actual_source_not_guessed_definition(self):
        fixture = b'// synthetic fixture only\n#define FIXTURE_DISALLOW_PATH_REDIRECTS 123\n'
        result = sdk.parse_header(fixture)
        fragment = result['fragments'][0]
        self.assertEqual([{'symbol': 'DISALLOW_PATH_REDIRECTS', 'kind': 'code-token'}], fragment['mentions'])
        self.assertTrue(any('FIXTURE_DISALLOW_PATH_REDIRECTS' in line['text'] for line in fragment['lines']))
        symbol = next(item for item in result['symbols'] if item['symbol'] == 'DISALLOW_PATH_REDIRECTS')
        self.assertEqual('unknown', symbol['definitionState'])

    def test_truncated_long_guard_and_unclosed_comment_remain_unknown(self):
        fixture = ("#if " + "A" * 600 + "\nCreateFile3();\n/* unfinished synthetic fixture").encode()
        result = sdk.parse_header(fixture)
        self.assertTrue(result["fragments"][0]["preprocessorContextUnknown"])
        self.assertTrue(result["fragments"][0]["fragmentTruncated"])

    def test_strict_encoding_and_source_bounds_refuse_lossy_guessing(self):
        for fixture in (b'// synthetic fixture\xff', b'CreateFile3\x00', b'x' * (sdk.MAX_HEADER_BYTES + 1)):
            with self.assertRaises(sdk.SourceError):
                sdk.parse_header(fixture)
        utf16 = '// synthetic fixture only\nCreateFile3();'.encode('utf-16')
        self.assertEqual("utf-16", sdk.parse_header(utf16)["encoding"])

    def test_matching_lines_and_context_are_bounded_with_explicit_omissions(self):
        fixture = ("// synthetic fixture only\n" + "CreateFile3();\n" * 100).encode()
        result = sdk.parse_header(fixture)
        symbol = next(item for item in result["symbols"] if item["symbol"] == 'CreateFile3')
        self.assertEqual(100, symbol["matchingLineCount"])
        self.assertEqual(96, symbol["matchingLinesOmitted"])
        self.assertEqual(4, len(result["fragments"]))
        self.assertTrue(all(len(item["lines"]) <= 40 for item in result["fragments"]))

    def test_owned_source_has_actual_hash_and_missing_source_remains_unknown(self):
        with tempfile.TemporaryDirectory(prefix='synthetic-sdk-fixture-') as root:
            folder = Path(root)
            (folder / 'um').mkdir()
            data = b'// synthetic fixture only\n#define INVALID_HANDLE_VALUE fixture\n'
            (folder / 'um' / 'handleapi.h').write_bytes(data)
            result = sdk.header_record(folder, 'um/handleapi.h')
            self.assertEqual(hashlib.sha256(data).hexdigest(), result['sha256'])
            self.assertEqual(len(data), result['byteCount'])
            absent = sdk.header_record(folder, 'um/fileapi.h')
            self.assertEqual('unknown', absent['state'])
            self.assertIsNone(absent['sha256'])
            with self.assertRaises(sdk.SourceError):
                sdk.header_record(folder, '../outside.h')

    def test_oversized_owned_file_not_read_or_hashed(self):
        with tempfile.TemporaryDirectory(prefix='synthetic-sdk-fixture-') as root:
            folder = Path(root)
            (folder / 'um').mkdir()
            with (folder / 'um' / 'fileapi.h').open('wb') as stream:
                stream.truncate(sdk.MAX_HEADER_BYTES + 1)
            result = sdk.header_record(folder, 'um/fileapi.h')
            self.assertEqual('header-size-limit', result['reason'])
            self.assertIsNone(result['sha256'])

    def test_source_read_error_does_not_export_exception_paths(self):
        with tempfile.TemporaryDirectory(prefix='synthetic-sdk-fixture-') as root:
            folder = Path(root)
            (folder / 'um').mkdir()
            (folder / 'um' / 'fileapi.h').write_bytes(b'// synthetic fixture only')
            with mock.patch.object(Path, 'open', side_effect=PermissionError('PRIVATE-PATH-MUST-NOT-LEAK')):
                result = sdk.header_record(folder, 'um/fileapi.h')
            self.assertEqual('source-unreadable', result['reason'])
            self.assertNotIn('PRIVATE-PATH', repr(result))

    def test_four_newest_numeric_versions_not_lexical_order_or_unlimited_scan(self):
        with tempfile.TemporaryDirectory(prefix='synthetic-sdk-fixture-') as root:
            folder = Path(root)
            for name in ('10.0.9.0', '10.0.11.0', '10.0.100.0', '10.0.2.0', '10.0.12.0', 'not-a-version', '10.0.01.0'):
                (folder / name).mkdir()
            self.assertEqual(['10.0.100.0', '10.0.12.0', '10.0.11.0', '10.0.9.0'], [item['sdkVersion'] for item in sdk.collect(folder)])
            for index in range(256):
                (folder / ('ignored-' + str(index))).mkdir()
            with self.assertRaises(sdk.SourceError):
                sdk.collect(folder)

    def test_links_are_refused_and_existing_report_is_never_replaced(self):
        with tempfile.TemporaryDirectory(prefix='synthetic-sdk-fixture-') as root:
            folder = Path(root)
            sdk.write_report(folder, b'{"syntheticFixtureOnly":true}')
            report = folder / 'artifacts/sdk-journal-reference/report.json'
            before = report.read_bytes()
            with self.assertRaises(FileExistsError):
                sdk.write_report(folder, b'new')
            self.assertEqual(before, report.read_bytes())
            link = folder / 'linked'
            try:
                link.symlink_to(folder, target_is_directory=True)
            except OSError:
                self.skipTest('Owned directory-link creation is unavailable.')
            with self.assertRaises(sdk.SourceError):
                sdk.ancestry(link)

    def test_report_bound_explicitly_marks_removed_fragments(self):
        fragment = {"lines": [{"line": index, "text": 'synthetic fixture ' * 30} for index in range(40)]}
        report = {"reportTruncated": False, "versions": [{"headers": [{"fragments": [copy.deepcopy(fragment) for _ in range(100)], "fragmentsOmittedForReportBound": 0}]}]}
        encoded = sdk.encode_report(report)
        self.assertLessEqual(len(encoded), sdk.MAX_REPORT_BYTES)
        self.assertTrue(report['reportTruncated'])
        self.assertGreater(report['versions'][0]['headers'][0]['fragmentsOmittedForReportBound'], 0)

    def test_main_has_no_argument_override_and_is_windows_only(self):
        with mock.patch.object(sdk, 'collect') as collect, mock.patch.object(sdk, 'write_report') as write:
            self.assertEqual(2, sdk.main(['--sdk-path', 'synthetic']))
            with mock.patch.object(sys, 'platform', 'linux'):
                self.assertEqual(2, sdk.main([]))
            collect.assert_not_called()
            write.assert_not_called()


if __name__ == '__main__':
    unittest.main()
