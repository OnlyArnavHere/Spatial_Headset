import importlib.util
import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock

_spec = importlib.util.spec_from_file_location("gen_licenses", Path(__file__).parent / "packaging" / "gen_licenses.py")
gl = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(gl)


def entry(name, lic, **kw):
    return {"Name": name, "Version": "1.0", "License": lic, "URL": "https://x", "LicenseText": "text of " + name,
            "NoticeText": "UNKNOWN", **kw}


class GenLicensesTests(unittest.TestCase):
    def run_main(self, entries):
        with tempfile.TemporaryDirectory() as d:
            out = Path(d) / "L.txt"
            with mock.patch("sys.stderr"), mock.patch("builtins.print"):
                code = gl.main([str(out)], loader=lambda: entries)
            return code, out.read_text(encoding="utf-8") if out.exists() else None

    def test_classify(self):
        self.assertEqual(gl.classify("MIT License"), "other")
        self.assertEqual(gl.classify("GNU Lesser General Public License v2 or later (LGPLv2+)"), "lgpl")
        self.assertEqual(gl.classify("LGPL-2.1-or-later"), "lgpl")
        self.assertEqual(gl.classify("GPL-3.0-only"), "gpl")
        self.assertEqual(gl.classify("GNU Affero General Public License v3"), "gpl")
        self.assertEqual(gl.classify("GNU General Public License v2 or later (GPLv2+)"), "gpl")

    def test_gpl_fails_without_writing(self):
        code, text = self.run_main([entry("a", "MIT"), entry("evil", "GPL-3.0-only")])
        self.assertEqual(code, 1)
        self.assertIsNone(text)

    def test_agpl_fails(self):
        self.assertEqual(self.run_main([entry("x", "AGPL-3.0")])[0], 1)

    def test_pyinstaller_exempt(self):
        code, _ = self.run_main([entry("pyinstaller", "GPL-2.0-or-later WITH Bootloader-exception"),
                                 entry("pyinstaller_hooks_contrib", "GPL-2.0-or-later")])
        self.assertEqual(code, 0)

    def test_lgpl_allowed_and_listed(self):
        code, text = self.run_main([entry("zeroconf", "LGPL-2.1-or-later"), entry("m", "MIT")])
        self.assertEqual(code, 0)
        self.assertIn("LGPL components", text)
        self.assertIn("zeroconf", text)
        self.assertIn("License: MIT", text)
        self.assertIn("text of m", text)

    def test_header_has_native_components_and_av_warning(self):
        _, text = self.run_main([entry("m", "MIT")])
        for name in ("Python", "Tcl/Tk", "OpenSSL", "libsrtp", "FFmpeg", "mediamtx", "x264/x265", "stage 9"):
            self.assertIn(name, text)
        self.assertLess(text.index("Native components"), text.index("Python packages"))

    def test_loader_failure_exits_2(self):
        def boom():
            raise subprocess.CalledProcessError(1, "x")
        with mock.patch("sys.stderr"):
            self.assertEqual(gl.main(["x"], loader=boom), 2)

    def test_json_roundtrip_shape(self):
        data = json.loads(json.dumps([entry("m", "MIT")]))
        self.assertEqual(gl.check(data), ([], []))


if __name__ == "__main__":
    unittest.main()
