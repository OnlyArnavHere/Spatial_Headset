"""Tests for the macOS backend. The pure ones run everywhere (no pyobjc); the ones that touch the real system run on
a Mac only."""
import os
import signal
import subprocess
import sys
import time
import unittest
from pathlib import Path
from unittest import mock

import network
from hostos import _macos_pure as pure
from hostos import _reaper
from hostos.types import Monitor, Rect, Window

PID = 4242


def cg(number, owner="Safari", name="Home", layer=0, pid=100, bounds=None):
    return {"kCGWindowNumber": number, "kCGWindowOwnerName": owner, "kCGWindowName": name, "kCGWindowLayer": layer,
            "kCGWindowOwnerPID": pid, "kCGWindowBounds": bounds or {"X": 10.0, "Y": 20.0, "Width": 800.0, "Height": 600.0}}


MAIN = pure.Display(1, Rect(0, 0, 1920, 1080), 1920)
RETINA = pure.Display(2, Rect(0, 0, 1440, 900), 2880)
LEFT = pure.Display(3, Rect(-1280, 0, 0, 720), 1280)
RIGHT = pure.Display(4, Rect(1920, 0, 3840, 1080), 1920)


class WindowListTests(unittest.TestCase):
    def test_layer_pid_and_empty_filters(self):
        infos = [cg(1), cg(2, layer=25), cg(3, pid=PID), cg(4, owner="", name=""), cg(5, owner="Finder", name=""),
                 cg(6, owner="", name="Untitled")]
        self.assertEqual(pure.windows_from_cg(infos, PID),
                         [Window(1, "Safari - Home"), Window(5, "Finder"), Window(6, "Untitled")])

    def test_bounds_become_a_rect(self):
        self.assertEqual(pure.rect_from_bounds({"X": 10.0, "Y": -20.0, "Width": 800.0, "Height": 600.4}),
                         Rect(10, -20, 810, 580))
        self.assertEqual(pure.bounds_of(7, [cg(6), cg(7)]), Rect(10, 20, 810, 620))
        self.assertIsNone(pure.bounds_of(8, [cg(6)]))

    def test_minimized_means_known_but_not_on_screen(self):
        everything, onscreen = [cg(1), cg(2)], [cg(1)]
        self.assertFalse(pure.is_minimized(1, everything, onscreen))
        self.assertTrue(pure.is_minimized(2, everything, onscreen))
        self.assertFalse(pure.is_minimized(3, everything, onscreen))  # gone, not minimized
        self.assertEqual(pure.window_ids(everything), {1, 2})

    def test_monitors(self):
        self.assertEqual(pure.monitors_from_displays([MAIN, LEFT], 1),
                         [Monitor(0, 1, MAIN.bounds, True), Monitor(1, 3, LEFT.bounds, False)])


class CropTests(unittest.TestCase):
    def test_retina_crop_is_full_size_at_the_right_offset(self):
        self.assertEqual(pure.crop_window(Rect(0, 0, 1440, 900), [RETINA])[1], (0, 0, 2880, 1800))
        self.assertEqual(pure.crop_window(Rect(100, 50, 500, 350), [RETINA])[1], (200, 100, 800, 600))

    def test_output_follows_the_clamped_window(self):
        hanging = Rect(1700, 100, 2100, 500)  # 400x400 points, but only 220x400 are on the display
        self.assertEqual(pure.visible_rect(hanging, [MAIN]), Rect(1700, 100, 1920, 500))
        self.assertEqual(pure.visible_rect(Rect(100, 100, 300, 300), [MAIN]), Rect(100, 100, 300, 300))
        with self.assertRaises(LookupError):
            pure.visible_rect(Rect(5000, 5000, 5200, 5200), [MAIN])

    def test_one_x(self):
        display, crop = pure.crop_window(Rect(100, 101, 500, 401), [MAIN])
        self.assertEqual((display, crop), (MAIN, (100, 100, 400, 300)))

    def test_two_x(self):
        self.assertEqual(pure.crop_window(Rect(10, 20, 410, 320), [RETINA])[1], (20, 40, 800, 600))

    def test_second_display_at_a_negative_origin(self):
        display, crop = pure.crop_window(Rect(-1000, 100, -600, 400), [MAIN, LEFT])
        self.assertEqual((display, crop), (LEFT, (280, 100, 400, 300)))

    def test_odd_sizes_floor_to_even(self):
        self.assertEqual(pure.crop_window(Rect(1, 1, 402, 303), [MAIN])[1], (0, 0, 400, 302))

    def test_off_screen_and_tiny_windows_count_as_minimized(self):
        for rect in (Rect(5000, 5000, 5200, 5200), Rect(0, 0, 10, 10), Rect(0, 0, 400, 12)):
            with self.subTest(rect=rect), self.assertRaisesRegex(LookupError, "minimized"):
                pure.crop_window(rect, [MAIN])

    def test_window_spanning_two_displays_uses_the_one_with_its_centre_and_is_clamped(self):
        display, crop = pure.crop_window(Rect(1800, 100, 2200, 500), [MAIN, RIGHT])
        self.assertEqual((display, crop), (RIGHT, (0, 100, 280, 400)))
        display, crop = pure.crop_window(Rect(1700, 100, 2000, 500), [MAIN, RIGHT])
        self.assertEqual((display, crop), (MAIN, (1700, 100, 220, 400)))


DEVICES = """\
[AVFoundation indev @ 0x7f8] AVFoundation video devices:
[AVFoundation indev @ 0x7f8] [0] FaceTime HD Camera
[AVFoundation indev @ 0x7f8] [1] Studio Display Camera
[AVFoundation indev @ 0x7f8] [2] Capture screen 0
[AVFoundation indev @ 0x7f8] [3] Capture screen 1
[AVFoundation indev @ 0x7f8] AVFoundation audio devices:
[AVFoundation indev @ 0x7f8] [0] MacBook Pro Microphone
"""


class FfmpegTests(unittest.TestCase):
    def test_screen_devices(self):
        self.assertEqual(pure.parse_screen_devices(DEVICES), {0: 2, 1: 3})
        self.assertEqual(pure.parse_screen_devices("nothing useful"), {})

    def test_capture_args_with_and_without_a_crop(self):
        self.assertEqual(pure.capture_args(2, 30, 1280, 720, "nv12", (20, 40, 800, 600)),
                         ["-f", "avfoundation", "-capture_cursor", "1", "-framerate", "30", "-pixel_format", "bgr0",
                          "-i", "2:none", "-filter_complex",
                          "[0:v]crop=800:600:20:40,scale=1280:720:flags=bilinear,format=nv12[v]",
                          "-map", "[v]", "-fps_mode", "vfr"])
        self.assertEqual(pure.capture_args(3, 60, 1920, 1080, "yuv420p", None)[10:12],
                         ["-filter_complex", "[0:v]scale=1920:1080:flags=bilinear,format=yuv420p[v]"])

    COMMON = ["-b:v", "6000000", "-maxrate", "6000000", "-bufsize", "3000000", "-g", "30", "-bf", "0",
              "-force_key_frames", "expr:gte(t,n_forced*1)"]

    def test_encoder_args(self):
        self.assertEqual(pure.encoder_args("videotoolbox", "h264_videotoolbox", 30, 6_000_000),
                         ["-c:v", "h264_videotoolbox", "-realtime", "1", "-prio_speed", "1", "-profile:v", "baseline"]
                         + self.COMMON)
        self.assertEqual(pure.encoder_args("x264", "libx264", 30, 6_000_000),
                         ["-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-profile:v", "baseline"]
                         + self.COMMON)

    def test_backend_tables(self):
        import hostos.macos as backend
        self.assertEqual(backend.ENCODERS, [("videotoolbox", "h264_videotoolbox", "nv12"),
                                            ("x264", "libx264", "yuv420p")])
        self.assertEqual(backend.exe_suffix, "")
        self.assertEqual((backend.window_selector(7), backend.monitor_selector(9)), ("window=7", "display=9"))


class SmallPieceTests(unittest.TestCase):
    def test_hardware_ports(self):
        text = """Hardware Port: Wi-Fi
Device: en0
Ethernet Address: aa:bb

Hardware Port: iPhone USB
Device: en5
Ethernet Address: cc:dd

Hardware Port: Thunderbolt Bridge
Device: bridge0
"""
        self.assertEqual(network.iphone_usb_devices(text), {"en5"})
        self.assertEqual(network.iphone_usb_devices(""), set())

    def test_reaper_protocol(self):
        self.assertEqual(_reaper.parse_lines(b"123\n456\n"), [123, 456])
        self.assertEqual(_reaper.parse_lines(b"12\nabc\n\n-5\n1\n0\n 77 \n"), [12, 77])

    def test_reaper_lines_split_across_reads(self):
        groups = _reaper.Groups()
        for chunk in (b"12", b"3\n45", b"\n"):
            groups.feed(chunk)
        self.assertEqual(groups.ids, [123, 45])
        groups.feed(b"67")  # the agent died mid-write: never guessed
        self.assertEqual(groups.ids, [123, 45])

    def test_reaper_handles_reads_before_the_exit_and_drains_without_blocking(self):
        events = ["proc", "read", "read"]
        self.assertEqual(_reaper.reads_first(events, lambda e: e == "read"), ["read", "read", "proc"])
        groups, pipe = _reaper.Groups(), [b"7\n", b"8", b"9\n", b""]
        _reaper.drain(groups, lambda: pipe.pop(0), lambda: bool(pipe))
        self.assertEqual(groups.ids, [7, 89])
        groups = _reaper.Groups()
        _reaper.drain(groups, lambda: b"1\n", lambda: False)  # nothing readable: returns at once
        self.assertEqual(groups.ids, [])
        ticks = iter(range(100))
        _reaper.drain(groups, lambda: b"5\n", lambda: True, budget=3, clock=lambda: next(ticks))  # bounded
        self.assertLessEqual(len(groups.ids), 3)

    def test_children_get_their_own_group(self):
        self.assertEqual(pure.group_kwargs((3, 11, 4)), {"process_group": 0})
        self.assertEqual(pure.group_kwargs((3, 13)), {"process_group": 0})
        self.assertEqual(pure.group_kwargs((3, 10, 12)), {"start_new_session": True})


class FakeProcess:
    pid = 77

    def __init__(self, dead=None, write_error=None):
        self.dead, self.write_error = dead, write_error
        self.killed = self.waited = False
        self.stdin = self
        self.written = []

    def wait(self, timeout=None):
        if self.dead is None and timeout is not None:
            raise subprocess.TimeoutExpired("watcher", timeout)
        self.waited = True
        return self.dead

    def poll(self):
        return self.dead

    def write(self, data):
        if self.write_error:
            raise self.write_error
        self.written.append(data)

    def flush(self):
        pass

    def kill(self):
        self.killed = True


class ReaperFailureTests(unittest.TestCase):
    """ChildReaper must refuse to run children it can't clean up (like a failed job object on Windows)."""

    def reaper(self, watcher, child):
        import hostos.macos as backend
        processes = iter([watcher, child])
        with mock.patch.object(backend.subprocess, "Popen", lambda *a, **k: next(processes)):
            return backend, backend.ChildReaper()

    def test_a_watcher_that_dies_at_once_is_an_error(self):
        with self.assertRaisesRegex(OSError, "child cleanup watcher is not running"):
            self.reaper(FakeProcess(dead=1), FakeProcess())

    def test_a_healthy_watcher_gets_the_group_id(self):
        import hostos.macos as backend
        watcher, child = FakeProcess(), FakeProcess()
        processes = iter([watcher, child])
        with mock.patch.object(backend.subprocess, "Popen", lambda *a, **k: next(processes)):
            self.assertIs(backend.ChildReaper().spawn(["sleep"]), child)
        self.assertEqual(watcher.written, [b"77" + bytes([10])])
        self.assertFalse(child.killed)

    def test_a_watcher_that_dies_later_kills_the_child_and_raises(self):
        import hostos.macos as backend
        for how in ("exited", "broken pipe"):
            watcher = FakeProcess(write_error=BrokenPipeError("pipe closed") if how == "broken pipe" else None)
            child = FakeProcess()
            processes = iter([watcher, child])
            with mock.patch.object(backend.subprocess, "Popen", lambda *a, **k: next(processes)):
                reaper = backend.ChildReaper()
                if how == "exited":
                    watcher.dead = 3
                with self.subTest(how=how), self.assertRaisesRegex(OSError, "child cleanup watcher is not running"):
                    reaper.spawn(["sleep"])
            self.assertTrue(child.killed and child.waited, how)


@unittest.skipUnless(sys.platform == "darwin", "needs macOS")
class RealMacTests(unittest.TestCase):
    def test_real_lists(self):
        import hostos.macos as backend
        self.assertTrue(backend.list_monitors())
        self.assertTrue(backend.list_windows())

    def test_reaper_kills_a_sleeping_child_when_the_parent_exits(self):
        code = ("import sys, time; sys.path.insert(0, %r)\n"
                "import hostos.macos as m\n"
                "reaper = m.ChildReaper()\n"
                "child = reaper.spawn(['sleep', '60'])\n"
                "print(child.pid, flush=True)\n"
                "time.sleep(60)\n") % str(Path(__file__).resolve().parent)
        parent = subprocess.Popen([sys.executable, "-c", code], stdout=subprocess.PIPE, text=True)
        child_pid = int(parent.stdout.readline())
        os.kill(child_pid, 0)  # alive
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


if __name__ == "__main__":
    unittest.main()
