"""AgentController with fakes: start, stop all, restart, option changes, kill switch, shutdown. No tkinter, no ffmpeg."""
import asyncio
import subprocess
import sys
import threading
import time
import unittest
from types import SimpleNamespace

import agent
import controller


class FakeProc:
    returncode = None

    def __init__(self):
        self.terminated = False

    def poll(self):
        return 0 if self.terminated else None

    def terminate(self):
        self.terminated = True

    def kill(self):
        self.terminated = True

    def wait(self, timeout=None):
        return 0


class FakeStreams:
    def __init__(self, launcher):
        self.launcher = launcher
        self.stops = 0
        self.ticks = 0

    def tick(self):
        self.ticks += 1

    def stop_all(self):
        self.stops += 1


class FakeRunner:
    def __init__(self, ident, store, streams, port, **options):
        self.options, self.streams, self.killed = options, streams, False
        self.running = threading.Event()

    async def run(self):
        self.running.set()
        await asyncio.Event().wait()

    def kill_switch(self):
        self.killed = True


class FakeStore:
    def __init__(self):
        self.names = ["Quest"]

    def list(self):
        return list(self.names)

    def revoke(self, name):
        if name in self.names:
            self.names.remove(name)
            return True
        return False


class Rig:
    def __init__(self, **over):
        self.mediamtx = []
        self.runners = []
        self.hotkeys = []
        self.probes = 0
        self.store = FakeStore()

        def start_mediamtx(args, job):
            self.mediamtx.append(FakeProc())
            return self.mediamtx[-1]

        def runner_factory(*a, **kw):
            self.runners.append(FakeRunner(*a, **kw))
            return self.runners[-1]

        def probe(ffmpeg, choice):
            self.probes += 1
            return ("x264", "libx264", "yuv420p")

        def kill_switch(callback):
            self.hotkeys.append(callback)
            return True

        opts = dict(tool=lambda name: name, probe=probe, start_mediamtx=start_mediamtx,
                    capture_launcher=lambda args, enc, job: "launcher", reaper=object,
                    streams_factory=FakeStreams, runner_factory=runner_factory, kill_switch=kill_switch,
                    load_identity=lambda: "ident", store_factory=lambda: self.store,
                    approver_factory=lambda keyboard: ("approver", keyboard), watch_s=0.01)
        opts.update(over)
        self.c = controller.AgentController(**opts)


def wait_for(predicate, timeout=3.0):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if predicate():
            return True
        time.sleep(0.005)
    return False


class ControllerTests(unittest.TestCase):
    def rig(self, **over):
        r = Rig(**over)
        self.addCleanup(r.c.shutdown)
        return r

    def running(self, r):
        r.c.start()
        self.assertTrue(wait_for(lambda: r.c.status()["state"] == "running"), r.c.status())

    def test_start_runs_and_reports_status(self):
        r = self.rig()
        self.assertEqual(r.c.status()["state"], "idle")
        self.running(r)
        self.assertEqual(r.c.status()["encoder"], "x264")
        self.assertTrue(wait_for(lambda: r.runners and r.runners[0].running.is_set()))
        opts = r.runners[0].options
        self.assertFalse(opts["input_enabled"])
        self.assertTrue(opts["discover"])
        self.assertIsNone(opts["approver"])
        states = [e["state"] for e in r.c.events() if e["type"] == "status"]
        self.assertEqual(states, ["starting", "running"])
        self.assertEqual(r.c.events(), [])

    def test_stop_all_keeps_mediamtx_and_restart_reuses_it(self):
        r = self.rig()
        self.running(r)
        first = r.runners[0]
        r.c.stop_all()
        self.assertTrue(wait_for(lambda: r.c.status()["state"] == "idle"))
        self.assertGreaterEqual(first.streams.stops, 1)
        self.assertFalse(r.mediamtx[0].terminated)
        self.running(r)
        self.assertEqual(len(r.mediamtx), 1)
        self.assertEqual(r.probes, 1)
        self.assertEqual(len(r.runners), 2)
        self.assertEqual(len(r.hotkeys), 1)

    def test_apply_rebuilds_runner_only_when_options_change(self):
        r = self.rig()
        self.running(r)
        r.c.apply(SimpleNamespace(allow_control=False))
        r.c.apply(SimpleNamespace(allow_control=True))
        self.assertTrue(wait_for(lambda: len(r.runners) == 2))
        self.assertTrue(r.runners[1].options["input_enabled"])
        self.assertTrue(r.runners[1].options["keyboard_enabled"])
        self.assertEqual(r.runners[1].options["approver"], ("approver", True))
        self.assertEqual(len(r.mediamtx), 1)
        r.c.apply(SimpleNamespace(headset_address="192.168.1.5"))
        self.assertTrue(wait_for(lambda: len(r.runners) == 3))
        self.assertEqual(r.runners[2].options["pinned"], [("192.168.1.5", 47810)])
        self.assertFalse(r.runners[2].options["discover"])

    def test_apply_rejects_bad_settings(self):
        r = self.rig()
        with self.assertRaises(ValueError):
            r.c.apply(SimpleNamespace(allow_control=True, cast_only=True))
        with self.assertRaises(ValueError):
            r.c.apply(SimpleNamespace(headset_address="not a host:::"))

    def test_kill_hotkey_registered_once_and_routed_to_current_runner(self):
        r = self.rig()
        self.running(r)
        r.c.stop_all()
        self.assertTrue(wait_for(lambda: r.c.status()["state"] == "idle"))
        self.running(r)
        self.assertEqual(len(r.hotkeys), 1)
        r.hotkeys[0]()
        self.assertTrue(wait_for(lambda: r.runners[1].killed))
        self.assertFalse(r.runners[0].killed)
        self.assertTrue(wait_for(lambda: r.c.status()["state"] == "idle"))
        self.running(r)  # a new runner after the kill switch
        self.assertEqual(len(r.runners), 3)

    def test_shutdown_stops_media_and_joins_the_thread(self):
        r = self.rig()
        self.running(r)
        thread = r.c._thread
        r.c.shutdown()
        self.assertTrue(r.mediamtx[0].terminated)
        self.assertFalse(thread.is_alive())

    def test_shutdown_before_start_is_a_no_op(self):
        controller.AgentController().shutdown()

    def test_systemexit_from_helper_becomes_error_status(self):
        def probe(ffmpeg, choice):
            raise SystemExit("No working H.264 encoder found")
        r = self.rig(probe=probe)
        r.c.start()
        self.assertTrue(wait_for(lambda: r.c.status()["state"] == "error"))
        self.assertIn("No working H.264 encoder", r.c.status()["message"])
        self.assertEqual(r.runners, [])

    def test_missing_tool_is_an_error_not_a_crash(self):
        def tool(name):
            raise SystemExit(f"{name} not found.")
        r = self.rig(tool=tool)
        r.c.start()
        self.assertTrue(wait_for(lambda: r.c.status()["state"] == "error"))
        self.assertIn("ffmpeg not found", r.c.status()["message"])

    def test_dead_mediamtx_is_reported_and_start_recovers(self):
        r = self.rig()
        self.running(r)
        r.mediamtx[0].terminated = True
        r.mediamtx[0].returncode = 1
        self.assertTrue(wait_for(lambda: r.c.status()["state"] == "error"))
        self.assertIn("MediaMTX", r.c.status()["message"])
        self.running(r)
        self.assertEqual(len(r.mediamtx), 2)

    def test_hotkey_survives_shutdown_and_restart(self):
        r = self.rig()
        self.running(r)
        r.c.shutdown()
        r.hotkeys[0]()  # loop is gone: must not raise
        self.running(r)
        self.assertEqual(len(r.hotkeys), 1)
        r.hotkeys[0]()
        self.assertTrue(wait_for(lambda: r.runners[-1].killed))

    def test_reaper_created_once_across_mediamtx_restarts(self):
        made = []
        r = self.rig(reaper=lambda: made.append(1) or object())
        self.running(r)
        r.mediamtx[0].terminated = True
        self.assertTrue(wait_for(lambda: r.c.status()["state"] == "error"))
        self.running(r)
        self.assertEqual(len(made), 1)

    def test_mediamtx_wait_fits_inside_the_shutdown_timeout(self):
        self.assertLess(controller.MEDIAMTX_WAIT_S, controller.JOIN_S)

    def test_wired_change_rebuilds_runner(self):
        r = self.rig()
        self.running(r)
        r.c.apply(SimpleNamespace(wired=True))
        self.assertTrue(wait_for(lambda: len(r.runners) == 2))

    def test_pairs_and_revoke(self):
        r = self.rig()
        self.assertEqual(r.c.pairs(), ["Quest"])
        self.assertTrue(r.c.revoke("Quest"))
        self.assertEqual(r.c.pairs(), [])
        self.assertFalse(r.c.revoke("Quest"))

    def test_importing_controller_does_not_import_tkinter(self):
        code = "import controller, sys; sys.exit(1 if 'tkinter' in sys.modules else 0)"
        self.assertEqual(subprocess.run([sys.executable, "-c", code]).returncode, 0)

    def test_parse_args_accepts_an_argv(self):
        self.assertFalse(agent.parse_args([]).input)
        self.assertTrue(agent.parse_args(["--input"]).input)


if __name__ == "__main__":
    unittest.main()
