"""paths: frozen / unfrozen locations and helper command lines, no_window, logging setup."""
import logging
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import agent
import hostos
import paths


class FrozenTests(unittest.TestCase):
    def test_not_frozen_by_default(self):
        self.assertFalse(paths.frozen())

    def test_frozen_flag(self):
        with mock.patch.object(sys, "frozen", True, create=True):
            self.assertTrue(paths.frozen())

    def test_unfrozen_dirs_are_next_to_the_sources(self):
        self.assertEqual(paths.tools_dir(), paths.HERE / "bin")
        self.assertEqual(paths.runtime_dir(), paths.HERE / ".runtime")

    def test_frozen_dirs_are_under_the_data_dir(self):
        with mock.patch.object(sys, "frozen", True, create=True), \
                mock.patch("identity.default_home", return_value=Path("H")):
            self.assertEqual(paths.data_dir(), Path("H"))
            self.assertEqual(paths.tools_dir(), Path("H") / "bin")
            self.assertEqual(paths.runtime_dir(), Path("H") / "runtime")
            self.assertEqual(paths.logs_dir(), Path("H") / "logs")


class HelperArgvTests(unittest.TestCase):
    def test_unfrozen_runs_the_script_isolated(self):
        self.assertEqual(paths.helper_argv("reaper", Path("x") / "_reaper.py"),
                         [sys.executable, "-I", str(Path("x") / "_reaper.py")])

    def test_frozen_reruns_the_executable_with_a_flag(self):
        with mock.patch.object(sys, "frozen", True, create=True):
            self.assertEqual(paths.helper_argv("reaper", "ignored.py"), [sys.executable, "--_reaper"])

    def test_linux_dialog_argv_frozen_and_not(self):
        from hostos import _linux_pure as pure
        self.assertEqual(pure.tk_argv("d.py", "t", "c", 7), [sys.executable, "-I", "d.py", "t", "c", "7"])
        with mock.patch.object(sys, "frozen", True, create=True):
            self.assertEqual(pure.tk_argv("d.py", "t", "c", 7), [sys.executable, "--_linux_dialog", "t", "c", "7"])


class NoWindowTests(unittest.TestCase):
    def test_adds_the_flag_on_windows(self):
        with mock.patch.object(sys, "platform", "win32"):
            out = paths.no_window({"timeout": 3})
        self.assertEqual(out["timeout"], 3)
        self.assertEqual(out["creationflags"], 0x08000000)

    def test_merges_existing_creation_flags(self):
        with mock.patch.object(sys, "platform", "win32"):
            out = paths.no_window({"creationflags": 0x200})
        self.assertEqual(out["creationflags"], 0x200 | 0x08000000)

    def test_leaves_other_platforms_alone_and_does_not_mutate(self):
        original = {"creationflags": 1}
        with mock.patch.object(sys, "platform", "darwin"):
            self.assertEqual(paths.no_window(original), {"creationflags": 1})
        with mock.patch.object(sys, "platform", "win32"):
            paths.no_window(original)
        self.assertEqual(original, {"creationflags": 1})


class SetupLoggingTests(unittest.TestCase):
    def setUp(self):
        self.root = logging.getLogger()
        self.saved = list(self.root.handlers)
        self.addCleanup(self._restore)

    def _restore(self):
        for h in list(self.root.handlers):
            if h not in self.saved:
                self.root.removeHandler(h)
                h.close()

    def _setup(self, stderr):
        with tempfile.TemporaryDirectory() as d, mock.patch("identity.default_home", return_value=Path(d)), \
                mock.patch.object(sys, "stderr", stderr):
            path = paths.setup_logging()
            added = [h for h in self.root.handlers if h not in self.saved]
            self._restore()
        return path, added

    def test_file_handler_and_console(self):
        path, added = self._setup(sys.__stderr__ or object())
        self.assertEqual(path.name, "agent.log")
        self.assertEqual({type(h).__name__ for h in added}, {"RotatingFileHandler", "StreamHandler"})

    def test_no_console_handler_without_stderr(self):
        _, added = self._setup(None)
        self.assertEqual([type(h).__name__ for h in added], ["RotatingFileHandler"])


class ProbeFallbackTests(unittest.TestCase):
    ENC = [("x264", "libx264", "yuv420p")]
    FALL = [("mf", "h264_mf", "nv12"), ("openh264", "libopenh264", "yuv420p")]

    def _run(self, working):
        def fake_run(command, **kwargs):
            codec = command[command.index("-c:v") + 1]
            return subprocess.CompletedProcess(command, 0 if codec in working else 1, "", "no such encoder")
        return fake_run

    def test_falls_back_to_h264_mf(self):
        with mock.patch.object(hostos, "ENCODERS", self.ENC), mock.patch.object(hostos, "FALLBACK_ENCODERS", self.FALL), \
                mock.patch("agent.subprocess.run", side_effect=self._run({"h264_mf"})) as run:
            self.assertEqual(agent.probe_encoder("ffmpeg", "auto"), ("mf", "h264_mf", "nv12"))
        self.assertEqual(run.call_count, 2)

    def test_primary_encoder_wins(self):
        with mock.patch.object(hostos, "ENCODERS", self.ENC), mock.patch.object(hostos, "FALLBACK_ENCODERS", self.FALL), \
                mock.patch("agent.subprocess.run", side_effect=self._run({"libx264", "h264_mf"})):
            self.assertEqual(agent.probe_encoder("ffmpeg", "auto")[0], "x264")

    def test_named_fallback_and_none_working(self):
        with mock.patch.object(hostos, "ENCODERS", self.ENC), mock.patch.object(hostos, "FALLBACK_ENCODERS", self.FALL):
            with mock.patch("agent.subprocess.run", side_effect=self._run({"libopenh264"})):
                self.assertEqual(agent.probe_encoder("ffmpeg", "openh264")[0], "openh264")
            with mock.patch("agent.subprocess.run", side_effect=self._run(set())):
                with self.assertRaises(SystemExit):
                    agent.probe_encoder("ffmpeg", "auto")

    def test_windows_encoder_args_for_fallbacks(self):
        if sys.platform != "win32":
            self.skipTest("windows backend")
        mf = hostos.encoder_args("mf", "h264_mf", 30, 6_000_000)
        self.assertIn("display_remoting", mf)
        self.assertIn("cbr", mf)
        self.assertIn("-allow_skip_frames", hostos.encoder_args("openh264", "libopenh264", 30, 6_000_000))


class AppDispatchTests(unittest.TestCase):
    def test_version_needs_no_tk(self):
        import app
        with mock.patch("builtins.print") as out:
            self.assertEqual(app.main(["--version"]), 0)
        out.assert_called_once_with(app.VERSION)
        self.assertNotIn("tkinter", sys.modules) if "tkinter" not in self._before else None

    _before = set(sys.modules)

    def test_frozen_linux_dialog_dispatch(self):
        import app
        from hostos import _linux_dialog
        with mock.patch.object(_linux_dialog, "main", return_value=1) as main:
            self.assertEqual(app.main(["--_linux_dialog", "text", "cap", "7"]), 1)
        main.assert_called_once_with(["_linux_dialog.py", "text", "cap", "7"])

    def test_reaper_dispatch(self):
        import app
        from hostos import _reaper
        with mock.patch.object(_reaper, "main") as main, mock.patch.object(_reaper, "main_linux") as linux:
            self.assertEqual(app.main(["--_reaper", "42"]), 0)
        self.assertEqual(main.call_count + linux.call_count, 1)


if __name__ == "__main__":
    unittest.main()
