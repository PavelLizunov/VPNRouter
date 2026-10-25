"""Small pipeline regressions; run on the render worker before exporting."""
import hashlib
from pathlib import Path
import subprocess
import tempfile
import unittest
import sys

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))
from verify_styles import selected_styles, verify_checksums, verify_streams, STYLES


class PipelineTests(unittest.TestCase):
    def test_preview_is_all_eight_and_final_is_explicit_subset(self):
        self.assertEqual(selected_styles('preview', []), STYLES)
        self.assertEqual(selected_styles('final', ['anime', 'swiss']), ('anime', 'swiss'))
        for mode, values in [('final', []), ('final', ['unknown']), ('final', ['anime', 'anime']),
                             ('preview', ['anime']), ('wrong', [])]:
            with self.assertRaises(ValueError):
                selected_styles(mode, values)

    def test_invalid_export_args_fail_before_any_build(self):
        for args in [('final',), ('wrong',), ('final', '../bad'), ('final', 'anime', 'anime'),
                     ('preview', 'anime'), ('final', 'anime swiss') ]:
            result = subprocess.run(['bash', str(ROOT/'export-styles.sh'), *args],
                                    capture_output=True, text=True)
            self.assertEqual(result.returncode, 2, result.stderr)

    def test_mode_dimensions_and_title_are_consumer_checks(self):
        def sample(width, height):
            return {'streams': [dict(codec_type='video', width=width, height=height,
                    codec_name='h264', pix_fmt='yuv420p', avg_frame_rate='30/1', nb_frames='1080', duration='36.0'),
                    dict(codec_type='audio', codec_name='aac', duration='36.0')],
                    'format': {'tags': {'title': 'Choose your routes | anime'}}}
        verify_streams(sample(854, 480), 'preview', 'anime')
        verify_streams(sample(1920, 1080), 'final', 'anime')
        with self.assertRaises(AssertionError):
            verify_streams(sample(854, 480), 'final', 'anime')
        data = sample(854, 480)
        data['format']['tags']['title'] = 'repository-name'
        with self.assertRaises(AssertionError):
            verify_streams(data, 'preview', 'anime')

    def test_checksums_reject_changed_bytes_and_path_escape(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root/'film.mp4').write_bytes(b'original bytes')
            digest = hashlib.sha256(b'original bytes').hexdigest()
            manifest = root/'SHA256SUMS.txt'
            manifest.write_text(f'{digest}  film.mp4\n')
            verify_checksums(manifest, root)
            (root/'film.mp4').write_bytes(b'changed')
            with self.assertRaises(AssertionError):
                verify_checksums(manifest, root)
            manifest.write_text(f'{digest}  ../outside.mp4\n')
            with self.assertRaises(AssertionError):
                verify_checksums(manifest, root)

    def test_authored_video_source_excludes_repository_and_owner_text(self):
        # Filenames and crate identifiers are allowed; displayed string literals
        # and metadata titles must not contain the old unclickable repository CTA.
        import re
        for path in (ROOT/'src').glob('*.rs'):
            if path.name == 'checks.rs':
                continue
            strings = re.findall(r'"([^"\n]*)"', path.read_text())
            for value in strings:
                if value in ('out/vpnrouter-film-silent.mp4',):
                    continue
                for forbidden in ('VPNRouter', 'VPNROUTER', 'PavelLizunov', 'Pavel Lizunov', 'github.com'):
                    self.assertNotIn(forbidden, value, (path, value))


if __name__ == '__main__':
    unittest.main()
