import tempfile
import unittest
from pathlib import Path
from unittest import mock

import autostart


class FakeKey:
    def __init__(self, values):
        self.values = values

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False


class FakeReg:
    """An in-memory stand-in for winreg: the real HKCU Run key is never touched."""
    HKEY_CURRENT_USER = "HKCU"
    REG_SZ = 1
    KEY_SET_VALUE = 2

    def __init__(self):
        self.keys = {}

    def OpenKey(self, root, path, reserved=0, access=0):
        if path not in self.keys:
            raise FileNotFoundError(path)
        return FakeKey(self.keys[path])

    def CreateKey(self, root, path):
        return FakeKey(self.keys.setdefault(path, {}))

    def QueryValueEx(self, key, name):
        if name not in key.values:
            raise FileNotFoundError(name)
        return key.values[name], 1

    def SetValueEx(self, key, name, _r, kind, data):
        key.values[name] = data

    def DeleteValue(self, key, name):
        if name not in key.values:
            raise FileNotFoundError(name)
        del key.values[name]


class AutostartBase(unittest.TestCase):
    def patch(self, **attrs):
        for k, v in attrs.items():
            p = mock.patch.object(autostart, k, v)
            p.start()
            self.addCleanup(p.stop)

    def frozen(self, value=True):
        p = mock.patch.object(autostart.paths, "frozen", lambda: value)
        p.start()
        self.addCleanup(p.stop)


class WindowsTests(AutostartBase):
    def setUp(self):
        self.reg = FakeReg()
        self.patch(_winreg=self.reg, _platform="win32", _exe=r"C:\Program Files\Spatial Launcher\Agent.exe")
        self.frozen()

    def run_values(self):
        return self.reg.keys.get(autostart.RUN_KEY, {})

    def test_quoted_command_with_spaces(self):
        self.assertEqual(autostart.command_line(), r'"C:\Program Files\Spatial Launcher\Agent.exe" --background')
        self.assertEqual(autostart.enable(), (True, ""))
        self.assertEqual(self.run_values()[autostart.VALUE_NAME], autostart.command_line())
        self.assertTrue(autostart.is_enabled())

    def test_disabled_by_default(self):
        self.assertEqual(autostart.status(), ("disabled", ""))

    def test_even_first_byte_is_enabled(self):
        autostart.enable()
        self.reg.keys[autostart.APPROVED_KEY] = {autostart.VALUE_NAME: bytes([0x02, 0, 0, 0])}
        self.assertEqual(autostart.status(), ("enabled", ""))

    def test_0x03_is_disabled_by_user_and_not_overwritten(self):
        autostart.enable()
        approved = bytes([0x03] + [0] * 11)
        self.reg.keys[autostart.APPROVED_KEY] = {autostart.VALUE_NAME: approved}
        self.assertEqual(autostart.status(), ("disabled", "turned off in Task Manager"))
        ok, msg = autostart.enable()
        self.assertFalse(ok)
        self.assertIn("Task Manager", msg)
        self.assertEqual(self.reg.keys[autostart.APPROVED_KEY][autostart.VALUE_NAME], approved)

    def test_disable_removes_value(self):
        autostart.enable()
        self.assertEqual(autostart.disable(), (True, ""))
        self.assertNotIn(autostart.VALUE_NAME, self.run_values())
        self.assertEqual(autostart.disable(), (True, ""))  # already gone is fine
        self.assertFalse(autostart.is_enabled())


class MacTests(AutostartBase):
    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.dir = Path(tmp.name) / "LaunchAgents"
        self.patch(_platform="darwin", _launch_agents_dir=self.dir, _exe="/Applications/Spatial Launcher.app/Contents/MacOS/Agent")
        self.frozen()
        self.plist = self.dir / "com.spatiallauncher.agent.plist"

    def test_enable_writes_plist(self):
        import plistlib
        self.assertEqual(autostart.enable(), (True, ""))
        data = plistlib.loads(self.plist.read_bytes())
        self.assertEqual(data["Label"], "com.spatiallauncher.agent")
        self.assertEqual(data["ProgramArguments"], ["/Applications/Spatial Launcher.app/Contents/MacOS/Agent", "--background"])
        self.assertIs(data["RunAtLoad"], True)
        self.assertEqual(autostart.status(), ("enabled", ""))

    def test_translocated_path_refused(self):
        self.patch(_exe="/private/var/folders/x/AppTranslocation/ABC/d/Agent.app/Contents/MacOS/Agent")
        ok, msg = autostart.enable()
        self.assertFalse(ok)
        self.assertTrue(msg)
        self.assertFalse(self.plist.exists())

    def test_outside_applications_refused(self):
        self.patch(_exe="/Users/x/Downloads/Agent.app/Contents/MacOS/Agent")
        self.assertFalse(autostart.enable()[0])
        self.assertFalse(self.plist.exists())

    def test_user_applications_allowed(self):
        self.patch(_exe=str(Path.home()) + "/Applications/Agent.app/Contents/MacOS/Agent")
        self.assertTrue(autostart.enable()[0])

    def test_disable_removes_plist(self):
        autostart.enable()
        self.assertEqual(autostart.disable(), (True, ""))
        self.assertFalse(self.plist.exists())
        self.assertEqual(autostart.status(), ("disabled", ""))


class MacPlistTests(MacTests):
    def test_plist_is_written_atomically(self):
        autostart.enable()
        self.assertEqual([p.name for p in self.dir.iterdir()], ["com.spatiallauncher.agent.plist"])

    def test_old_location_is_reported_and_enable_repairs_it(self):
        autostart.enable()
        self.patch(_exe="/Applications/Moved.app/Contents/MacOS/Agent")
        state, message = autostart.status()
        self.assertEqual(state, "enabled")
        self.assertIn("old location", message)
        self.assertTrue(autostart.enable()[0])
        self.assertEqual(autostart.status(), ("enabled", ""))

    def test_damaged_plist_is_reported(self):
        autostart.enable()
        self.plist.write_bytes(b"not a plist")
        state, message = autostart.status()
        self.assertEqual(state, "enabled")
        self.assertIn("damaged", message)


class NotFrozenTests(AutostartBase):
    def test_unavailable_and_nothing_written(self):
        reg = FakeReg()
        self.patch(_winreg=reg, _platform="win32")
        self.frozen(False)
        self.assertEqual(autostart.status()[0], "unavailable")
        self.assertFalse(autostart.is_enabled())
        self.assertFalse(autostart.enable()[0])
        self.assertFalse(autostart.disable()[0])
        self.assertEqual(reg.keys, {})


if __name__ == "__main__":
    unittest.main()
