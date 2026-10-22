"""App lifecycle without a display (fake root), and a real launch that quits by itself."""
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import app

HERE = Path(__file__).resolve().parent


class FakeRoot:
    def __init__(self):
        self.calls = []

    def __getattr__(self, name):
        return lambda *a, **k: self.calls.append(name)


class FakeController:
    def __init__(self):
        self.calls = []

    def stop_all(self):
        self.calls.append("stop_all")

    def shutdown(self):
        self.calls.append("shutdown")


class FakeThing:
    def __init__(self, log, name):
        self.log, self.name = log, name

    def close(self):
        self.log.append(self.name)

    stop = close


def make(hide_on_close=True):
    root, controller = FakeRoot(), FakeController()
    return app.App(root, controller, hide_on_close=hide_on_close), root, controller


class AppTests(unittest.TestCase):
    def test_close_hides_instead_of_quitting(self):
        a, root, controller = make()
        a.on_close()
        self.assertEqual(root.calls, ["withdraw"])
        self.assertEqual(controller.calls, [])
        self.assertFalse(a.quit_requested)

    def test_close_quits_where_nothing_can_bring_the_window_back(self):
        a, root, controller = make(hide_on_close=False)
        a.on_close()
        self.assertIn("destroy", root.calls)
        self.assertEqual(controller.calls, ["shutdown"])

    def test_open_command_shows_the_window(self):
        a, root, _ = make()
        a.post("open")
        a.poll()
        self.assertEqual(root.calls[:2], ["deiconify", "lift"])
        self.assertIn("after", root.calls)  # keeps polling

    def test_stop_all_command_goes_to_the_controller(self):
        a, _, controller = make()
        a.post("stop_all")
        a.poll()
        self.assertEqual(controller.calls, ["stop_all"])

    def test_quit_shuts_down_in_order_once(self):
        a, root, controller = make()
        log = []
        a.window, a.tray = FakeThing(log, "window"), FakeThing(log, "tray")
        controller.shutdown = lambda: log.append("shutdown")
        a.post("quit")
        a.post("quit")
        a.poll()
        a.quit()
        self.assertEqual(log, ["window", "shutdown", "tray"])
        self.assertEqual(root.calls.count("destroy"), 1)
        self.assertNotIn("after", root.calls)  # no more polling after quit

    def test_unknown_command_is_ignored(self):
        a, _, controller = make()
        a.handle("nope")
        self.assertEqual(controller.calls, [])

    def test_version_flag(self):
        with mock.patch("builtins.print") as out:
            self.assertEqual(app.main(["--version"]), 0)
        out.assert_called_once_with(app.VERSION)

    def test_background_flag_reaches_run(self):
        with mock.patch.object(app, "run", return_value=0) as run:
            app.main(["--background"])
            app.main([])
        self.assertEqual([c.kwargs["background"] for c in run.call_args_list], [True, False])


class FinishTests(unittest.TestCase):
    def test_finish_runs_atexit_handlers_before_exiting(self):
        order = []
        with mock.patch("atexit._run_exitfuncs", side_effect=lambda: order.append("atexit")), \
                mock.patch("logging.shutdown", side_effect=lambda: order.append("logging")), \
                mock.patch("os._exit", side_effect=lambda code: order.append(("exit", code))):
            app.finish(0)
        self.assertEqual(order, ["atexit", "logging", ("exit", 0)])

    def test_finish_still_exits_if_a_handler_fails(self):
        with mock.patch("atexit._run_exitfuncs", side_effect=RuntimeError("x")), \
                mock.patch("os._exit") as hard_exit:
            app.finish(3)
        hard_exit.assert_called_once_with(3)

    def test_real_process_runs_atexit_and_exits_1_on_error(self):
        code = ("import atexit, app\n"
                "atexit.register(lambda: print('cleanup ran'))\n"
                "app.main = lambda: 1 / 0\n"
                "app.entry()\n")
        result = subprocess.run([sys.executable, "-c", code], capture_output=True, text=True, timeout=60, cwd=HERE)
        self.assertEqual(result.returncode, 1, result.stderr)
        self.assertIn("cleanup ran", result.stdout)

    def test_systemexit_code_is_kept(self):
        def boom():
            raise SystemExit(5)
        with mock.patch.object(app, "main", boom), mock.patch.object(app, "finish") as finish:
            app.entry()
        finish.assert_called_once_with(5)


def _tk_works() -> bool:
    try:
        import tkinter
        tkinter.Tk().destroy()
        return True
    except Exception:
        return False


def _running_with(text: str) -> list[str]:
    if sys.platform != "win32":
        return []
    out = subprocess.run(["powershell", "-NoProfile", "-Command",
                          "Get-CimInstance Win32_Process | ForEach-Object { $_.CommandLine }"],
                         capture_output=True, text=True, timeout=60).stdout
    return [line for line in out.splitlines() if text in line]


class LaunchTests(unittest.TestCase):
    def test_app_launches_and_quits_by_itself(self):
        if not _tk_works():
            self.skipTest("Tk is unavailable")
        with tempfile.TemporaryDirectory() as home:
            env = dict(os.environ, SPATIAL_LAUNCHER_HOME=home, SPATIAL_AGENT_SMOKE_QUIT="2")
            result = subprocess.run([sys.executable, str(HERE / "app.py")], env=env, capture_output=True, text=True,
                                    timeout=60, cwd=HERE)
            self.assertEqual(result.returncode, 0, result.stderr)
            log_text = (Path(home) / "logs" / "agent.log").read_text(encoding="utf-8")
            self.assertIn("Spatial Launcher Agent", log_text)  # startup line is written and flushed
            self.assertIn("Quit requested", log_text)
            self.assertIn("Exiting with code 0", log_text)
            # nothing of ours may be left behind (MediaMTX runs from this folder's .runtime)
            self.assertEqual(_running_with(str(HERE / ".runtime")), [])


if __name__ == "__main__":
    unittest.main()
