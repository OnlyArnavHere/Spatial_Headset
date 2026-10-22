"""Tray icon: struct layout and command mapping. No icon is ever shown here."""
import ctypes
import sys
import unittest

from gui import tray_win as t


class TrayTests(unittest.TestCase):
    def test_notifyicondata_size_flags_and_callback(self):
        data = t.notify_data(123, 456, "tip")
        self.assertEqual(data.cbSize, ctypes.sizeof(t.NOTIFYICONDATAW))
        if ctypes.sizeof(ctypes.c_void_p) == 8:
            self.assertEqual(data.cbSize, 976)
        self.assertEqual(data.uFlags, t.NIF_MESSAGE | t.NIF_ICON | t.NIF_TIP)
        self.assertEqual(data.uCallbackMessage, t.CALLBACK_MESSAGE)
        self.assertEqual((data.hWnd, data.hIcon, data.szTip), (123, 456, "tip"))

    def test_long_tip_is_truncated(self):
        self.assertEqual(len(t.notify_data(1, 1, "x" * 500).szTip), 127)

    def test_menu_ids_map_to_commands(self):
        self.assertEqual([t.command_name(i) for i in (t.ID_OPEN, t.ID_STOP_ALL, t.ID_QUIT)],
                         ["open", "stop_all", "quit"])
        self.assertIsNone(t.command_name(0))  # menu dismissed
        self.assertIsNone(t.command_name(99))

    def test_dispatch_and_callbacks(self):
        got = []
        tray = t.TrayIcon(got.append)
        tray.dispatch(t.ID_STOP_ALL)
        tray.dispatch(0)
        tray.handle_callback(t.WM_LBUTTONDBLCLK, lambda: self.fail("no menu on double click"))
        tray.handle_callback(t.WM_RBUTTONUP, lambda: t.ID_QUIT)
        tray.handle_callback(0x0200, lambda: self.fail("mouse move is ignored"))
        self.assertEqual(got, ["stop_all", "open", "quit"])

    def test_window_procedure_type_is_a_cfunctype(self):
        self.assertTrue(issubclass(t.WNDPROC, ctypes._CFuncPtr))

    def test_stop_is_safe_when_never_started(self):
        tray = t.TrayIcon(lambda name: None)
        tray.stop()
        if sys.platform != "win32":
            self.assertFalse(tray.start())


if __name__ == "__main__":
    unittest.main()
