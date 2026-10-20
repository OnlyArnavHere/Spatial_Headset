"""Tests for the Linux (X11) backend. Everything but the real-system classes runs on every OS: the logic is in
hostos/_linux_pure.py and the X server is replaced by a fake connection."""
import contextlib
import io
import os
import signal
import subprocess
import sys
import time
import unittest
from pathlib import Path
from unittest import mock

import hostos.linux_x11 as backend
from hostos import _linux_pure as pure
from hostos import _reaper
from hostos._ffmpeg import common_args
from hostos.types import Rect

XRANDR = """Monitors: 2
 0: +*eDP-1 1920/344x1080/194+0+0  eDP-1
 1: +HDMI-1 2560/597x1440/336+1920+0  HDMI-1
"""
MONITORS = pure.parse_listmonitors(XRANDR)
LINUX = sys.platform.startswith("linux")


class MonitorTests(unittest.TestCase):
    def test_parse_listmonitors(self):
        self.assertEqual([m.rect for m in MONITORS], [Rect(0, 0, 1920, 1080), Rect(1920, 0, 4480, 1440)])
        self.assertEqual([m.primary for m in MONITORS], [True, False])

    def test_negative_offsets_and_no_primary_flag(self):
        monitors = pure.parse_listmonitors("Monitors: 2\n 0: +DP-1 800/10x600/10-800+0  DP-1\n 1: +DP-2 1024/1x768/1+0+0  DP-2\n")
        self.assertEqual(monitors[0].rect, Rect(-800, 0, 0, 600))
        self.assertEqual([m.primary for m in monitors], [True, False])
        self.assertEqual(pure.parse_listmonitors("garbage\n"), [])

    def test_window_is_clamped_to_its_monitor_and_floored_to_even(self):
        # centre on monitor 0, hanging off its right edge
        self.assertEqual(pure.visible_rect(Rect(1700, 100, 2100, 500), MONITORS), Rect(1700, 100, 1920, 500))
        self.assertEqual(pure.capture_rect(Rect(1701, 101, 2100, 501), MONITORS), Rect(1700, 100, 1918, 500))
        self.assertEqual(pure.capture_rect(Rect(11, 21, 112, 223), MONITORS), Rect(10, 20, 110, 222))

    def test_offscreen_or_tiny_windows_count_as_minimized(self):
        for rect in (Rect(-5000, 0, -4000, 100), Rect(100, 100, 110, 400), Rect(100, 100, 400, 108)):
            with self.assertRaisesRegex(LookupError, "minimized"):
                pure.capture_rect(rect, MONITORS)


class FfmpegTests(unittest.TestCase):
    def test_x11grab_args(self):
        args = pure.x11grab_args(":0", Rect(100, 200, 900, 800), 30, 640, 480, "yuv420p")
        self.assertEqual(args[:8], ["-f", "x11grab", "-draw_mouse", "1", "-framerate", "30", "-video_size", "800x600"])
        self.assertEqual(args[8:10], ["-i", ":0+100,200"])
        self.assertIn("scale=640:480:flags=bilinear,format=yuv420p", args)

    def test_encoder_args_reuse_the_common_args(self):
        args = pure.encoder_args("x264", "libx264", 30, 4_000_000)
        self.assertEqual(args[:2], ["-c:v", "libx264"])
        self.assertEqual(args[-len(common_args(30, 4_000_000)):], common_args(30, 4_000_000))
        self.assertEqual(backend.encoder_args("x264", "libx264", 30, 4_000_000), args)
        self.assertEqual(backend.ENCODERS, [("x264", "libx264", "yuv420p")])

    def test_has_x11grab(self):
        self.assertTrue(pure.has_x11grab("Devices:\n D  x11grab         X11 screen capture, using XCB\n DE v4l2\n"))
        self.assertFalse(pure.has_x11grab("Devices:\n DE v4l2\n  E xv\n"))

    def test_selectors(self):
        self.assertEqual(backend.window_selector(77), "window=77")
        self.assertEqual(backend.monitor_selector(1), "monitor=1")


class FakeX:
    """Stands in for _xlib.Connection."""
    root = 1

    def __init__(self):
        self.atoms = {}
        self.props = {}
        self.rects = {}
        self.sent = []

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        pass

    def intern(self, name):
        return self.atoms.setdefault(name, 1000 + len(self.atoms))

    def property(self, window, name):
        return self.props.get((window, name))

    def geometry(self, window):
        return self.rects.get(window)

    def send_client_message(self, window, message, fmt, data):
        self.sent.append((window, message, fmt, tuple(data)))


class WindowTests(unittest.TestCase):
    def setUp(self):
        self.x = FakeX()
        self.x.props[(1, "_NET_CLIENT_LIST")] = [10, 20, 30]
        self.x.props[(1, "_NET_CLIENT_LIST_STACKING")] = [10, 20, 30]
        self.x.props[(10, "_NET_WM_NAME")] = "Terminal é".encode()
        self.x.props[(20, "WM_NAME")] = b"Legacy"
        self.x.rects = {10: Rect(0, 0, 500, 500), 20: Rect(100, 100, 600, 600), 30: Rect(0, 0, 50, 50)}
        self.x.props[(20, "_NET_WM_STATE")] = [self.x.intern("_NET_WM_STATE_HIDDEN")]
        patcher = mock.patch.object(backend, "_connect", lambda: self.x)
        patcher.start()
        self.addCleanup(patcher.stop)

    def test_list_windows_uses_net_wm_name_and_skips_untitled(self):
        self.assertEqual([(w.handle, w.title) for w in backend.list_windows()], [(10, "Terminal é"), (20, "Legacy")])
        self.assertEqual(backend.find_window("termin").handle, 10)
        self.assertIsNone(backend.find_window("nothing"))

    def test_exists_rect_and_minimized(self):
        self.assertTrue(backend.window_exists(30))
        self.assertFalse(backend.window_exists(99))
        self.assertEqual(backend.window_rect(10), Rect(0, 0, 500, 500))
        self.assertTrue(backend.window_is_minimized(20))
        self.assertFalse(backend.window_is_minimized(10))

    def test_minimized_window_cannot_be_captured(self):
        with mock.patch.object(backend, "list_monitors", lambda: MONITORS):
            with self.assertRaisesRegex(LookupError, "minimized"):
                backend.capture_input_args("window=20", 30, 640, 480, "yuv420p")
            with mock.patch.dict(os.environ, {"DISPLAY": ":1"}):
                args = backend.capture_input_args("window=10", 30, 640, 480, "yuv420p")
                self.assertIn(":1+0,0", args)
                self.assertIn("500x500", args)
                self.assertIn(":1+0,0", backend.capture_input_args("monitor=0", 30, 640, 480, "yuv420p"))
            with self.assertRaisesRegex(LookupError, "gone"):
                backend.capture_input_args("window=99", 30, 640, 480, "yuv420p")

    def test_focus_is_a_net_active_window_client_message(self):
        self.assertEqual(pure.active_window_message(), (32, (2, 0, 0, 0, 0)))
        backend.raise_window(10)
        self.assertEqual(self.x.sent, [(10, "_NET_ACTIVE_WINDOW", 32, (2, 0, 0, 0, 0))])
        self.x.props[(1, "_NET_ACTIVE_WINDOW")] = [10]
        self.assertTrue(backend.focus_window(10))
        self.assertEqual(backend.foreground_window(), 10)

    def test_window_at_is_the_topmost_visible_window(self):
        self.assertEqual(backend.window_at(10, 10), 30)
        self.assertEqual(backend.window_at(300, 300), 10)  # 20 is minimized
        self.assertEqual(backend.window_at(900, 900), 0)


class SecretsTests(unittest.TestCase):
    def test_key_file_check(self):
        self.assertIsNone(pure.key_file_problem(0o100600, 1000, 1000))
        self.assertIsNone(pure.key_file_problem(0o100400, 1000, 1000))
        for mode in (0o100640, 0o100604, 0o100644, 0o100660, 0o100777):
            self.assertIn("readable by its owner only", pure.key_file_problem(mode, 1000, 1000), oct(mode))
        self.assertIn("owned by uid 0", pure.key_file_problem(0o100600, 0, 1000))

    def test_protect_returns_its_input(self):
        self.assertEqual(backend.protect(b"secret"), b"secret")

    def test_check_key_file_refuses_a_bad_file(self):
        for mode, uid, ok in ((0o100600, 5, True), (0o100644, 5, False), (0o100600, 6, False)):
            info = mock.Mock(st_mode=mode, st_uid=uid)
            with mock.patch.object(Path, "stat", return_value=info), mock.patch.object(os, "getuid", lambda: 5, create=True):
                if ok:
                    backend.check_key_file("/x/identity.key")
                else:
                    with self.assertRaises(OSError):
                        backend.check_key_file("/x/identity.key")
        self.assertEqual(backend.unprotect(b"blob"), b"blob")

    def test_identity_checks_the_file_it_reads_wherever_the_home_is(self):
        import tempfile
        import hostos
        import identity
        with tempfile.TemporaryDirectory() as home:
            identity.load_or_create(Path(home))
            seen = []
            with mock.patch.object(hostos, "check_key_file", seen.append):
                identity.load_or_create(Path(home))
            self.assertEqual(seen, [Path(home) / identity.KEY_FILE])
            with mock.patch.object(hostos, "check_key_file", side_effect=OSError("group readable")):
                with self.assertRaises(identity.IdentityError):
                    identity.load_or_create(Path(home))

    def test_default_home(self):
        with mock.patch.dict(os.environ, {"XDG_DATA_HOME": "/data"}):
            self.assertEqual(backend.default_home(), Path("/data") / "SpatialLauncher" / "agent")


class SessionRowTests(unittest.TestCase):
    def test_wayland_detection(self):
        self.assertTrue(pure.is_wayland({"XDG_SESSION_TYPE": "wayland"}))
        self.assertTrue(pure.is_wayland({"XDG_SESSION_TYPE": "Wayland", "DISPLAY": ":0"}))
        self.assertTrue(pure.is_wayland({"WAYLAND_DISPLAY": "wayland-0"}))
        self.assertFalse(pure.is_wayland({"XDG_SESSION_TYPE": "x11", "DISPLAY": ":0"}))
        self.assertFalse(pure.is_wayland({}))

    def test_rows(self):
        ok = pure.permission_rows({"XDG_SESSION_TYPE": "x11", "DISPLAY": ":0"}, True)
        self.assertEqual([(r.name, r.ok) for r in ok], [("X11 session", True), ("ffmpeg x11grab", True), ("Input", False)])
        self.assertEqual(len(pure.permission_rows({"DISPLAY": ":0"}, None)), 2)
        wayland = pure.session_row({"XDG_SESSION_TYPE": "wayland", "DISPLAY": ":0"})
        self.assertFalse(wayland.ok)
        self.assertIn("xdg-desktop-portal ScreenCast and RemoteDesktop", wayland.text)
        nodisplay = pure.session_row({})
        self.assertFalse(nodisplay.ok)
        self.assertIn("DISPLAY", nodisplay.text)
        self.assertFalse(pure.permission_rows({"DISPLAY": ":0"}, False)[1].ok)

    def test_preflight_skips_the_ffmpeg_probe_without_a_session(self):
        with mock.patch.dict(os.environ, {"XDG_SESSION_TYPE": "wayland"}), \
                mock.patch.object(backend, "_ffmpeg_has_x11grab", side_effect=AssertionError):
            rows = backend.preflight()
        self.assertEqual([(r.name, r.ok) for r in rows], [("X11 session", False), ("Input", False)])


def parsed(*argv):
    import agent
    with mock.patch.object(sys, "argv", ["agent.py", *argv]):
        return agent.parse_args()


class CheckPermissionsTests(unittest.TestCase):
    def run_check(self, argv, env):
        import agent
        err = io.StringIO()
        refused = None
        with contextlib.redirect_stderr(err), contextlib.redirect_stdout(io.StringIO()):
            try:
                agent.check_permissions(parsed(*argv), pure.permission_rows(env, None))
            except SystemExit as e:
                refused = str(e)
        return refused, err.getvalue()

    def test_wayland_and_missing_display_stop_the_agent(self):
        for env in ({"XDG_SESSION_TYPE": "wayland"}, {}):
            for argv in ([], ["--input"], ["--list"]):
                refused, err = self.run_check(argv, env)
                self.assertIsNotNone(refused, (env, argv))
                self.assertTrue(err.startswith("error: X11 session"), err)
                self.assertNotIn("restart the app", refused)

    def test_pairing_does_not_need_x(self):
        refused, err = self.run_check(["--pairs"], {})
        self.assertIsNone(refused)
        self.assertTrue(err.startswith("warning:"))

    def test_input_row_stops_only_input_runs(self):
        env = {"DISPLAY": ":0"}
        for argv in (["--input"], ["--cast-only"]):
            refused, err = self.run_check(argv, env)
            self.assertIsNotNone(refused, argv)
            self.assertIn("Input is not implemented on Linux", err)
        for argv in ([], ["--list"], ["--pairs"]):
            self.assertEqual(self.run_check(argv, env), (None, ""), argv)

    def test_x11_session_prints_nothing(self):
        self.assertEqual(self.run_check([], {"DISPLAY": ":0"}), (None, ""))

    def test_missing_x11grab_stops_capture_but_not_listing(self):
        import agent
        rows = pure.permission_rows({"DISPLAY": ":0"}, False)
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                agent.check_permissions(parsed(), rows)
            agent.check_permissions(parsed("--list"), rows)


class ReaperTests(unittest.TestCase):
    def test_linux_main_is_wired_in(self):
        self.assertTrue(callable(_reaper.main_linux))
        self.assertTrue(backend.ChildReaper.spawn)
        self.assertNotIn("preexec_fn", Path(backend.__file__).read_text())
        self.assertNotIn("PDEATHSIG", Path(backend.__file__).read_text() + Path(_reaper.__file__).read_text())

    def test_kill_switch_is_unavailable_not_an_error(self):
        self.assertIs(backend.watch_kill_switch(lambda: None), False)

    def test_unimplemented_parts_are_still_stubs(self):
        for name in ("mouse_move", "key", "open_prompt"):
            with self.assertRaises(NotImplementedError):
                getattr(backend, name)(1)


@unittest.skipUnless(LINUX, "needs Linux")
class RealLinuxTests(unittest.TestCase):
    def test_reaper_kills_a_sleeping_child_when_the_parent_exits(self):
        code = ("import sys, time; sys.path.insert(0, %r)\n"
                "import hostos.linux_x11 as m\n"
                "reaper = m.ChildReaper()\n"
                "child = reaper.spawn(['sleep', '60'])\n"
                "print(child.pid, flush=True)\n"
                "time.sleep(60)\n") % str(Path(__file__).resolve().parent)
        parent = subprocess.Popen([sys.executable, "-c", code], stdout=subprocess.PIPE, text=True)
        child_pid = int(parent.stdout.readline())
        os.kill(child_pid, 0)
        parent.kill()
        parent.wait()
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            try:
                os.kill(child_pid, 0)
            except ProcessLookupError:
                return
            time.sleep(0.1)
        os.kill(child_pid, signal.SIGKILL)
        self.fail("the child outlived its parent")

    def test_secrets_file_is_private_and_checked(self):
        import tempfile
        import identity
        with tempfile.TemporaryDirectory() as home, mock.patch.dict(os.environ, {"SPATIAL_LAUNCHER_HOME": home}):
            path = Path(home) / identity.KEY_FILE
            identity.atomic_write(path, backend.protect(b"k"))
            self.assertEqual(path.stat().st_mode & 0o777, 0o600)
            self.assertEqual(backend.unprotect(path.read_bytes()), b"k")
            backend.check_key_file(path)
            path.chmod(0o644)
            with self.assertRaises(OSError):
                backend.check_key_file(path)

    def test_group_readable_key_in_a_custom_home_is_refused_by_identity(self):
        import tempfile
        import identity
        with tempfile.TemporaryDirectory() as home:  # not the default home, and no SPATIAL_LAUNCHER_HOME
            import hostos
            with mock.patch.object(hostos, "check_key_file", backend.check_key_file),                     mock.patch.object(hostos, "protect", backend.protect), mock.patch.object(hostos, "unprotect", backend.unprotect):
                identity.load_or_create(Path(home))
                (Path(home) / identity.KEY_FILE).chmod(0o640)
                with self.assertRaises(identity.IdentityError):
                    identity.load_or_create(Path(home))

    @unittest.skipUnless(os.environ.get("DISPLAY"), "needs an X server")
    def test_real_x_server_lists(self):
        self.assertTrue(backend.list_monitors())
        backend.list_windows()


if __name__ == "__main__":
    unittest.main()
