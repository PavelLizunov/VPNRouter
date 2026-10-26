"""Data-only orchestration contracts; never invoke a native media command."""
import ast
import copy
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

HERE = Path(__file__).resolve().parent


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


ci = module('full_history_ci_contracts', HERE/'ci.py')
public = module('full_history_public_contracts', HERE/'public.py')
fixture = module('full_history_existing_trace_fixtures', HERE.parent/'diagnostics/test_fframes_input.py')


def compatible_trace():
    text = fixture.with_state(fixture.trace())
    values = {
        'i_width': '854', 'i_height': '480', 'i_csp': '2', 'i_bitdepth': '8',
        'i_threads': '1', 'i_lookahead_threads': '1', 'i_sync_lookahead': '0',
        'b_sliced_threads': '0', 'b_deterministic': '1', 'b_interlaced': '0',
        'rc.i_aq_mode': '1', 'rc.f_aq_strength': '3f19999a', 'rc.b_mb_tree': '1',
        'rc.i_vbv_buffer_size': '0', 'rc.i_vbv_max_bitrate': '0',
        'rc.b_stat_read': '0', 'rc.b_stat_write': '0', 'rc.i_zones': '0',
        'b_opencl': '0', 'b_repeat_headers': '0', 'b_annexb': '1',
    }
    rows = []
    for row in text.splitlines():
        fields = row.split('\t')
        if fields[0] == 'state':
            fields = [field.split('=', 1)[0]+'='+values[field.split('=', 1)[0][6:]]
                      if field.startswith('param.') and field.split('=', 1)[0][6:] in values
                      else field for field in fields]
        rows.append('\t'.join(fields))
    return '\n'.join(rows)+'\n'


class PublicContracts(unittest.TestCase):
    def parse(self, text):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary)/'public.tsv'
            path.write_text(text)
            return public.read_public(path, 45)

    def test_full_original_lifecycle_and_relevant_mutations(self):
        text = compatible_trace()
        baseline = self.parse(text)
        self.assertEqual(len(baseline['frames']), 1080)
        self.assertEqual(len(baseline['segments']), 45)
        self.assertTrue(public.compare_public(baseline, baseline)['observed_inputs_equal'])
        for old, new in (
            ('param.i_threads=1', 'param.i_threads=2'),
            ('param.rc.i_vbv_buffer_size=0', 'param.rc.i_vbv_buffer_size=1'),
            ('param.rc.i_aq_mode=1', 'param.rc.i_aq_mode=0'),
            ('param.rc.f_aq_strength=3f19999a', 'param.rc.f_aq_strength=3f19999b'),
            ('pic.prop.quant_offsets=0', 'pic.prop.quant_offsets=1'),
            ('pic.extra_sei.num_payloads=0', 'pic.extra_sei.num_payloads=1'),
        ):
            self.assertIn(old, text)
            with self.subTest(field=old), self.assertRaises((ValueError, AssertionError)):
                self.parse(text.replace(old, new, 1))

    def test_encountered_lifetime_partition_order_cannot_be_sorted_away(self):
        text = compatible_trace()
        blocks = text.split('open\t')[1:]
        blocks[0], blocks[1] = blocks[1], blocks[0]
        rows = []
        for generation, block in enumerate(blocks, 1):
            lines = ('open\t'+block).splitlines()
            for row in lines:
                fields = row.split('\t')
                fields[1] = str(generation)
                rows.append('\t'.join(fields))
        with self.assertRaisesRegex(ValueError, 'generation partition'):
            self.parse('\n'.join(rows)+'\n')

    def test_unused_stat_filename_presence_not_promoted_to_semantic_equality(self):
        text = compatible_trace()
        old = self.parse(text)
        changed = self.parse(text.replace('param.rc.psz_stat_in=0', 'param.rc.psz_stat_in=1', 1))
        compared = public.compare_public(old, changed)
        self.assertFalse(compared['observed_inputs_equal'])
        self.assertFalse(compared['complete_semantic_input_equality_established'])

    def test_payload_changes_are_distinct_from_observed_input_changes(self):
        original = self.parse(compatible_trace())
        changed = copy.deepcopy(original)
        item = changed['segments'][0]
        call = list(item['calls'][0])
        call[-2] = 'b'*64
        item['calls'][0] = tuple(call)
        compared = public.compare_public(original, changed)
        self.assertTrue(compared['observed_inputs_equal'])
        self.assertFalse(compared['native_payloads_equal'])


class DriverContracts(unittest.TestCase):
    def operand(self):
        return {'boundary': object(), 'internal': object(), 'decoded': {'0': 'a'},
                'media_sha256': {'render-tmp/0000000000.mp4': 'a'}}

    def run_series(self, failure=None, changed_stages=()):
        calls = []
        class Public:
            @staticmethod
            def read_public(*args):
                return object()
            @staticmethod
            def compare_public(left, right):
                return {'observed_inputs_equal': failure != 'history',
                        'native_payloads_equal': not changed_stages,
                        'complete_semantic_input_equality_established': False}
        class Internal:
            @staticmethod
            def compare_internal(left, right):
                return {name+'_changed_pts': [0] if name in changed_stages else [] for name in
                        ('entry', 'coded', 'post_aq', 'pre_slice', 'qpm')}
        def fake_launch(label, *args):
            calls.append(label)
            if failure == 'native-error' and label == 'film-A':
                raise RuntimeError('injected partial native failure')
            return self.operand()
        def compared(left, right):
            passed = not ((failure == 'smoke' and len(calls) == 2)
                          or (failure == 'film' and len(calls) == 5))
            return {'passed': passed, 'observed_inputs_equal': passed,
                    'native_payloads_equal': passed, 'decoded_equal': passed}
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)/'out'
            output.mkdir()
            (output/'frozen-inputs.json').write_text('{}')
            with patch.object(ci, 'capacity'), patch.object(ci, 'frozen_inputs', return_value={}), \
                 patch.object(ci, 'launch', side_effect=fake_launch), \
                 patch.object(ci, 'transparency', side_effect=compared), \
                 patch.object(ci, 'load', side_effect=lambda name: Public if name == 'public' else Internal):
                if failure:
                    with self.assertRaises(RuntimeError):
                        ci.experiment(Path(temporary), output, Path(temporary))
                else:
                    ci.experiment(Path(temporary), output, Path(temporary))
            result = json.loads((output/'verdict.json').read_text()) if (output/'verdict.json').exists() else None
            self.assertTrue((output/'frozen-inputs-after.json').is_file())
            return calls, result, sorted(p.name for p in output.glob('launch-*.json'))

    def test_fixed_manifest_budget_and_no_reproduction_stop(self):
        self.assertEqual(len(ci.MANIFEST), 6)
        self.assertEqual(sum(1080 if x[3] else 48 for x in ci.MANIFEST), 3384)
        self.assertEqual(sum(45 if x[3] else 2 for x in ci.MANIFEST), 141)
        calls, verdict, manifests = self.run_series()
        self.assertEqual(calls, [x[0] for x in ci.MANIFEST])
        self.assertEqual(verdict['status'], 'NO_REPRODUCTION_FULL_HISTORY')
        self.assertTrue(verdict['mandatory_stop_reached'])
        self.assertFalse(verdict['retry_authorized'])
        self.assertEqual(len(manifests), 6)

    def test_verdict_localization_uses_source_phase_chronology(self):
        stages = ('entry', 'coded', 'post_aq', 'qpm', 'pre_slice')
        for index, expected in enumerate(stages):
            with self.subTest(first_stage=expected):
                calls, verdict, _ = self.run_series(changed_stages=stages[index:])
                self.assertEqual(len(calls), 6)
                self.assertEqual(verdict['status'], 'OBSERVED_DIVERGENCE_FULL_HISTORY')
                self.assertEqual(verdict['first_measured_different_stage'], expected)

    def test_smoke_and_full_transparency_history_and_native_errors_stop_before_b(self):
        for failure, expected in (('smoke', 2), ('film', 5), ('history', 5), ('native-error', 5)):
            with self.subTest(failure=failure):
                calls, verdict, manifests = self.run_series(failure)
                self.assertEqual(len(calls), expected)
                self.assertNotIn('film-B', calls)
                self.assertEqual(len(manifests), expected)

    def test_partial_process_output_persists_in_exclusive_operand_on_error(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            root, output, work = (base/name for name in ('root', 'output', 'work'))
            for path in (root, output, work):
                path.mkdir()
            (work/'smoke').write_bytes(b'inert binary hash fixture')
            (work/'plain-prefix/lib').mkdir(parents=True)
            (work/'plain-prefix/lib/libx264.so.164').write_bytes(b'inert library')
            def fail(argv, log, **kwargs):
                log.write_text('original partial process log')
                (kwargs['cwd']/'synthetic-0000000000.h264').write_bytes(b'partial')
                raise subprocess.CalledProcessError(125, 'injected')
            with patch.object(ci, 'command', side_effect=fail), self.assertRaises(subprocess.CalledProcessError):
                ci.launch('synthetic-plain', 'plain', False, False, root, output, work)
            folder = output/'synthetic-plain'
            self.assertEqual((folder/'synthetic-0000000000.h264').read_bytes(), b'partial')
            self.assertTrue((folder/'invocation.json').is_file())
            with self.assertRaises(FileExistsError):
                ci.launch('synthetic-plain', 'plain', False, False, root, output, work)

    def test_media_report_capture_includes_required_observations(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary)
            tools = {'ffmpeg': {'path': '/usr/bin/ffmpeg'}, 'ffprobe': {'path': '/usr/bin/ffprobe'}}
            with patch.object(ci, 'media_command') as command:
                ci.media_reports(base/'inert.mp4', base/'reports', tools)
            probe = command.call_args_list[0].args[0]
            self.assertIn('-count_packets', probe)
            self.assertIn('-show_packets', probe)
            self.assertIn('-show_data_hash', probe)
            decode = command.call_args_list[1].args[0]
            self.assertEqual(decode[decode.index('-pix_fmt')+1], 'yuv420p')
            self.assertEqual(command.call_args_list[1].args[0], command.call_args_list[2].args[0])

    def test_media_identity_drift_and_unretained_loader_fail_closed(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            tool_path, library = root/'ffmpeg', root/'libexample.so'
            tool_path.write_bytes(b'inert-media-tool')
            library.write_bytes(b'inert-library')
            tool = {'path': str(tool_path), 'sha256': ci.digest(tool_path),
                    'libraries': [{'path': str(library), 'sha256': ci.digest(library)}]}
            ci.verify_media_tool(tool)
            library.write_bytes(b'drift')
            with self.assertRaisesRegex(RuntimeError, 'identity drift'):
                ci.verify_media_tool(tool)
            library.write_bytes(b'inert-library')
            def fake_command(args, log, **kwargs):
                log.write_text('inert-report')
                (root/'decode-loader.123').write_text('123: calling init: '+str(library)+'\n')
            with patch.object(ci, 'command', side_effect=fake_command):
                ci.media_command([tool_path, '-version'], root/'report.txt', tool, 'decode')
            self.assertTrue((root/'decode-invocation.json').is_file())
            with patch.object(ci, 'command'), \
                 patch.object(ci, 'loaded_media_files', return_value={'/usr/lib/unretained.so'}):
                with self.assertRaisesRegex(RuntimeError, 'unretained shared library'):
                    ci.media_command([tool_path, '-version'], root/'other.txt', tool, 'other')

    def test_saved_test_preparation_extracts_only_digest_bound_inert_members(self):
        import hashlib
        import shutil
        archive = (Path(os.environ['FFH_TEST_MEDIA']).parent/'saved-media-tests.zip'
                   if 'FFH_TEST_MEDIA' in os.environ else
                   HERE.parents[1]/'tools/help-videos/fframes-vpnrouter/out/native-importer-20261026/run-40359957912/artifact.zip')
        self.assertEqual(hashlib.sha256(archive.read_bytes()).hexdigest(), ci.SAVED_MEDIA_SHA)
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)
            def saved_fetch(url, target, expected, token):
                self.assertEqual(expected, ci.SAVED_MEDIA_SHA)
                self.assertIn(str(ci.SAVED_MEDIA_ARTIFACT), url)
                shutil.copyfile(archive, target)
            with patch.dict(os.environ, {'GH_TOKEN': 'inert-test-placeholder'}), \
                 patch.object(ci.HELPERS, 'fetch', side_effect=saved_fetch):
                ci.prepare_test_media(output)
            identity = json.loads((output/'saved-media-tests-identity.json').read_text())
            self.assertFalse(identity['native_payloads_extracted_or_executed'])
            self.assertEqual(len(identity['files']), 27)
            self.assertTrue(all(Path(name).suffix in ('.tsv', '.sha256', '.json', '.mp4', '.h264')
                                for name in identity['files']))
            self.assertFalse((output/'saved-media-tests/importer_segment').exists())

    def test_native_entrypoint_refuses_control_plane(self):
        argv = ['ci.py', 'experiment', '--root', '.', '--output', '.', '--work', '.']
        with patch.dict(os.environ, {'GITHUB_ACTIONS': 'false'}, clear=True), \
             patch('sys.argv', argv), self.assertRaises(SystemExit):
            ci.main()

    def test_source_is_original_binary_not_reduced_adapter(self):
        text = (HERE/'ci.py').read_text()
        ast.parse(text)
        self.assertNotIn('--example', text)
        self.assertNotIn('importer_segment', text)
        self.assertIn("'--bin', 'vpnrouter-film'", text)
        self.assertIn("'--scale', '0.4444444444444444', 'render'", text)
        self.assertNotIn('--keep-temp', text)
        self.assertIn("read_public(traces[0], lifetimes)", text)
        self.assertIn("folder/'render-tmp'", text)
        self.assertIn("'--archive', output/'x264-source.tar.gz'", text)


if __name__ == '__main__':
    unittest.main()
