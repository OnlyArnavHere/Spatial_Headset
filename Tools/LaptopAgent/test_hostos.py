"""Tests for the hostos platform layer: the Windows ffmpeg command line must not change, and every backend must
expose the same names (the macOS and Linux backends are stubs for now but must import everywhere)."""
import importlib
import subprocess
import sys
import threading
import types
import unittest
from unittest import mock

import agent
import hostos
from hostos.types import CONSTANTS, FUNCTIONS

# The command line the agent built for a 2560x1440 target before the platform layer existed (30 fps, 6M, 1920x1080 cap).
GOLDEN = {
    'nvenc':
        'C:/x/ffmpeg.exe -hide_banner -loglevel warning -filter_complex gfxcapture=hwnd=1234:max_framerate=30:capture_cursor=1,hwdownload,format=bgra,scale=1920:1080:flags=bilinear,format=yuv420p[v] -map [v] -fps_mode vfr -c:v h264_nvenc -preset p1 -tune ull -rc cbr -zerolatency 1 -profile:v baseline -forced-idr 1 -b:v 6000000 -maxrate 6000000 -bufsize 3000000 -g 30 -bf 0 -force_key_frames expr:gte(t,n_forced*1) -f rtsp -rtsp_transport tcp rtsp://127.0.0.1:8554/w1',
    'qsv':
        'C:/x/ffmpeg.exe -hide_banner -loglevel warning -filter_complex gfxcapture=hwnd=1234:max_framerate=30:capture_cursor=1,hwdownload,format=bgra,scale=1920:1080:flags=bilinear,format=nv12[v] -map [v] -fps_mode vfr -c:v h264_qsv -preset veryfast -profile:v baseline -async_depth 1 -look_ahead 0 -b:v 6000000 -maxrate 6000000 -bufsize 3000000 -g 30 -bf 0 -force_key_frames expr:gte(t,n_forced*1) -f rtsp -rtsp_transport tcp rtsp://127.0.0.1:8554/w1',
    'amf':
        'C:/x/ffmpeg.exe -hide_banner -loglevel warning -filter_complex gfxcapture=hwnd=1234:max_framerate=30:capture_cursor=1,hwdownload,format=bgra,scale=1920:1080:flags=bilinear,format=nv12[v] -map [v] -fps_mode vfr -c:v h264_amf -usage ultralowlatency -quality speed -rc cbr -profile:v constrained_baseline -b:v 6000000 -maxrate 6000000 -bufsize 3000000 -g 30 -bf 0 -force_key_frames expr:gte(t,n_forced*1) -f rtsp -rtsp_transport tcp rtsp://127.0.0.1:8554/w1',
    'x264':
        'C:/x/ffmpeg.exe -hide_banner -loglevel warning -filter_complex gfxcapture=hwnd=1234:max_framerate=30:capture_cursor=1,hwdownload,format=bgra,scale=1920:1080:flags=bilinear,format=yuv420p[v] -map [v] -fps_mode vfr -c:v libx264 -preset ultrafast -tune zerolatency -profile:v baseline -b:v 6000000 -maxrate 6000000 -bufsize 3000000 -g 30 -bf 0 -force_key_frames expr:gte(t,n_forced*1) -f rtsp -rtsp_transport tcp rtsp://127.0.0.1:8554/w1',
}


def build_command(encoder) -> str:
    built = []

    class FakePopen:
        pid = 1

        def __init__(self, command, **kwargs):
            built.append(command)

    job = types.SimpleNamespace(spawn=lambda command, **kwargs: FakePopen(command, **kwargs))
    args = types.SimpleNamespace(fps=30, max_width=1920, max_height=1080, bitrate="6M", verbose=False)
    target = types.SimpleNamespace(selector="hwnd=1234", rect=lambda: types.SimpleNamespace(width=2560, height=1440))
    with mock.patch.object(agent, "tool", lambda name: "C:/x/ffmpeg.exe"), \
            mock.patch.object(agent.subprocess, "Popen", FakePopen):
        agent.capture_launcher(args, encoder, job)(target, "w1")
    return subprocess.list2cmdline(built[0])


@unittest.skipUnless(sys.platform == "win32", "the golden strings are the Windows command lines")
class WindowsGoldenTests(unittest.TestCase):
    def test_ffmpeg_command_lines_are_unchanged(self):
        for encoder in hostos.ENCODERS:
            with self.subTest(encoder=encoder[0]):
                self.assertEqual(build_command(encoder), GOLDEN[encoder[0]])

    def test_encoder_order_is_unchanged(self):
        self.assertEqual(hostos.ENCODERS, [("nvenc", "h264_nvenc", "yuv420p"), ("qsv", "h264_qsv", "nv12"),
                                           ("amf", "h264_amf", "nv12"), ("x264", "libx264", "yuv420p")])

    def test_selectors_and_prompt_handle(self):
        self.assertEqual(hostos.window_selector(1234), "hwnd=1234")
        self.assertEqual(hostos.monitor_selector(99), "hmonitor=99")
        prompt = hostos.open_prompt("text", "caption", 1000)
        self.assertFalse(prompt.dismiss())  # nothing is showing yet

    def test_wired_report_text_per_os(self):
        import network
        iface = network.Interface("Apple Mobile Device Ethernet", "172.20.10.4", "", "Public")
        head = "Wired (USB):       host 172.20.10.4   on 'Apple Mobile Device Ethernet' (USB)"
        windows = network.WiredReport(iface, False, [], []).lines()
        self.assertEqual(windows, [head + ", firewall profile Public",
                                   "                   Public firewall is off: nothing to allow"])
        other = network.WiredReport(iface, None, [], [], firewall_checked=False).lines()
        self.assertEqual(other, [head, "                   No inbound firewall check on this OS (the agent dials out)."])
        with mock.patch.object(network, "list_interfaces", lambda platform=None: [iface]):
            with mock.patch.object(network, "read_firewall", lambda profile: (False, [], True)):
                report = network.wired_report("darwin")
                self.assertFalse(report.firewall_checked)
                self.assertEqual(report.lines(), other)
                report = network.wired_report("win32")
                self.assertTrue(report.firewall_checked)
                self.assertEqual(report.lines(), windows)

    def test_dismiss_during_wait_ends_the_prompt(self):
        import hostos.windows as backend
        opened, closed = threading.Event(), threading.Event()

        def ask(text, caption, timeout_ms):
            opened.set()
            closed.wait(5)
            return "no"

        def dismiss(thread_id):
            closed.set()
            return True

        with mock.patch.object(backend._w, "ask_yes_no", ask), mock.patch.object(backend._w, "dismiss_prompt", dismiss):
            prompt = hostos.open_prompt("text", "caption", 1000)
            result = []
            thread = threading.Thread(target=lambda: result.append(prompt.wait()))
            thread.start()
            self.assertTrue(opened.wait(5))
            self.assertTrue(prompt.dismiss())
            thread.join(5)
        self.assertEqual(result, ["no"])

    def test_windows_constants(self):
        import winapi
        import identity
        self.assertEqual(hostos.exe_suffix, ".exe")
        self.assertEqual(hostos.KILL_SWITCH, winapi.KILL_SWITCH)
        self.assertEqual(identity.ENTROPY, winapi.DPAPI_ENTROPY)
        self.assertIs(winapi.Rect, hostos.Rect)
        self.assertEqual(hostos.preflight(), [])


class InterfaceTests(unittest.TestCase):
    def test_new_names_are_part_of_the_interface(self):
        for name in ("open_prompt", "window_selector", "monitor_selector", "ChildReaper"):
            self.assertIn(name, FUNCTIONS + CONSTANTS)
            self.assertTrue(hasattr(hostos, name))
        self.assertTrue(hasattr(hostos.ChildReaper, "spawn"))
        for module in ("macos", "linux_x11"):
            self.assertTrue(hasattr(importlib.import_module(f"hostos.{module}").ChildReaper, "spawn"))

    def test_hostos_exposes_every_name(self):
        for name in FUNCTIONS + CONSTANTS:
            self.assertTrue(hasattr(hostos, name), name)

    def test_every_backend_module_imports_and_has_the_same_names(self):
        for module in ("macos", "linux_x11") + (("windows",) if sys.platform == "win32" else ()):
            backend = importlib.import_module(f"hostos.{module}")
            for name in FUNCTIONS + CONSTANTS:
                self.assertTrue(hasattr(backend, name), f"hostos.{module}.{name}")

    def test_linux_backend_has_no_stubs_left(self):
        backend = importlib.import_module("hostos.linux_x11")
        for name in FUNCTIONS:
            self.assertNotEqual(getattr(backend, name).__name__, "unsupported", name)
        self.assertEqual(backend.KILL_SWITCH, "Ctrl+Alt+Shift+Q")

    def test_macos_backend_has_no_stubs_left(self):
        backend = importlib.import_module("hostos.macos")
        for name in FUNCTIONS:
            self.assertNotEqual(getattr(backend, name).__name__, "unsupported", name)
        self.assertEqual(backend.KILL_SWITCH, "Ctrl+Option+Shift+Q")


if __name__ == "__main__":
    unittest.main()
