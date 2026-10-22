import unittest

from hostos import _macos_perms as perms
from hostos._macos_pure import ACCESSIBILITY, LISTEN, SCREEN


def probe_of(values):
    return lambda name: values.get(name)


class RowsTests(unittest.TestCase):
    def test_off_macos_has_no_rows(self):
        self.assertEqual(perms.rows(platform="win32"), [])
        self.assertEqual(perms.rows(platform="linux"), [])

    def test_four_rows_in_order_with_urls(self):
        rows = perms.rows(probe_of({SCREEN: True, ACCESSIBILITY: False, LISTEN: True}), platform="darwin")
        self.assertEqual([r.name for r in rows], [SCREEN, ACCESSIBILITY, LISTEN, perms.LOCAL_NETWORK])
        base = "x-apple.systempreferences:com.apple.preference.security?Privacy_"
        self.assertEqual([r.url for r in rows], [base + p for p in
                                                 ("ScreenCapture", "Accessibility", "ListenEvent", "LocalNetwork")])
        self.assertTrue(all(r.why for r in rows))

    def test_statuses(self):
        rows = perms.rows(probe_of({SCREEN: True, ACCESSIBILITY: False}), platform="darwin")
        self.assertEqual([r.status for r in rows], ["granted", "missing", "unknown", "unknown"])

    def test_only_input_monitoring_is_optional(self):
        rows = perms.rows(probe_of({}), platform="darwin")
        self.assertEqual([r.name for r in rows if r.optional], [LISTEN])

    def test_local_network_unknown_unless_likely_denied(self):
        ln = lambda **kw: perms.rows(probe_of({}), platform="darwin", **kw)[-1]
        self.assertEqual(ln().status, "unknown")
        self.assertEqual(ln(local_network_denied=True).status, "missing")

    def test_probe_error_is_unknown(self):
        def boom(name):
            raise RuntimeError("x")
        self.assertEqual(perms.rows(boom, platform="darwin")[0].status, "unknown")

    def test_no_microphone_row(self):
        self.assertFalse(any("icrophone" in n for n in perms.NAMES))


class RequestTests(unittest.TestCase):
    def test_request_calls_requester_once(self):
        calls = []
        self.assertTrue(perms.request(SCREEN, calls.append))
        self.assertEqual(calls, [SCREEN])

    def test_nothing_to_request_for_local_network_or_microphone(self):
        calls = []
        self.assertFalse(perms.request(perms.LOCAL_NETWORK, calls.append))
        self.assertFalse(perms.request("Microphone", calls.append))
        self.assertEqual(calls, [])

    def test_gate_requests_each_once(self):
        calls = []
        gate = perms.PermissionGate(calls.append)
        self.assertEqual(gate.requested, set())   # nothing at start
        self.assertEqual(calls, [])
        self.assertTrue(gate.ensure(SCREEN))
        self.assertFalse(gate.ensure(SCREEN))
        self.assertTrue(gate.ensure(ACCESSIBILITY))
        self.assertFalse(gate.ensure(perms.LOCAL_NETWORK))
        self.assertEqual(calls, [SCREEN, ACCESSIBILITY])

    def test_open_settings_uses_open(self):
        seen = []
        perms.open_settings("x-apple://u", lambda argv, **kw: seen.append(argv))
        self.assertEqual(seen, [["open", "x-apple://u"]])


if __name__ == "__main__":
    unittest.main()
