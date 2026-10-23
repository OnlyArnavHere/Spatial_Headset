"""The laptop shell over the USB hotspot link: the link runner dials the phone at 172.20.10.1 when the hotspot adapter is
there, and the session it builds for that address is the same as over Wi-Fi: the same shell backend, the same gate
(view only until control is granted, cast-only and no-input refused), the same replies. No network: the handshake is
replaced, and the session is driven through its control-message handler.

  python -m unittest test_wired_shell -v        (from Tools/LaptopAgent)
"""
import asyncio
import json
import unittest
from unittest import mock

import _test_keychain  # noqa: F401  (a throwaway Keychain entry on macOS)
import discovery
import dispatch
import handshake
import network
import session
from streams import StreamManager
from test_shell import FakeBackend

WIRED = (network.PHONE_ADDRESS, discovery.LINK_PORT)


class Recorder(session.LinkSession):
    """A LinkSession that remembers itself and skips building a real WebRTC offer."""
    made: list = []

    async def build_offer(self) -> str:
        Recorder.made.append(self)
        return "v=0"


async def never_browse():
    await asyncio.Event().wait()
    yield  # pragma: no cover


class WiredShell(unittest.IsolatedAsyncioTestCase):
    async def dial(self, **options):
        Recorder.made = []
        dialled = []
        backend = FakeBackend()

        async def fake_connect(host, port, ident, store, make_offer, **kw):
            dialled.append((host, port))
            await make_offer()
            await asyncio.Event().wait()  # the handshake is still running: the session stays open for the test

        runner = session.LinkRunner(None, None, StreamManager(lambda t, p: (None, (0, 0))), 8554, discover=True,
                                    browse=never_browse, wired=lambda: [WIRED], shell_backend=backend, **options)
        patches = [mock.patch.object(session, "LinkSession", Recorder), mock.patch.object(handshake, "agent_connect", fake_connect)]
        for p in patches:
            p.start()
            self.addCleanup(p.stop)
        task = asyncio.ensure_future(runner.run())
        self.addAsyncCleanup(self.stop, task)
        for _ in range(50):
            await asyncio.sleep(0.02)
            if Recorder.made:
                break
        return dialled, list(Recorder.made), backend

    @staticmethod
    async def stop(task):
        task.cancel()
        await asyncio.gather(task, return_exceptions=True)

    async def ask(self, link, message):
        sent = []
        link._send = sent.append
        link.shell.send = sent.append
        link._on_control_message(json.dumps(message))
        await asyncio.gather(*link._shell_tasks)
        return sent

    async def test_the_hotspot_is_dialled_and_its_session_carries_the_shell(self):
        dialled, made, backend = await self.dial(input_enabled=True, keyboard_enabled=True)
        self.assertIn(WIRED, dialled, "the phone's hotspot address is dialled without --headset")
        self.assertTrue(made, "a session was built for it")
        link = made[0]
        self.assertIs(link.shell.backend, backend, "the wired session uses the same shell backend")
        self.assertEqual(link.shell.info, {"os": "windows", "available": True})
        sent = await self.ask(link, {"type": "apps.running", "rid": 1})
        self.assertEqual((sent[0]["type"], sent[0]["reason"]), ("shell.error", "view only"), "view only until control is granted")
        link.dispatcher.state = dispatch.ControlState.GRANTED
        sent = await self.ask(link, {"type": "apps.running", "rid": 2})
        self.assertEqual((sent[0]["type"], sent[0]["rid"]), ("apps.running", 2), "with control, the shell answers over the cable")
        link.dispatcher.revoke("kill switch")
        sent = await self.ask(link, {"type": "apps.pinned"})
        self.assertEqual(sent[-1]["reason"], "stopped", "the kill switch stops it over the cable too")

    async def test_cast_only_and_no_input_over_the_cable(self):
        for options, reason in (({"input_enabled": True, "cast_only": True}, "cast only"), ({}, "input off")):
            _, made, backend = await self.dial(**options)
            link = made[0]
            link.dispatcher.state = dispatch.ControlState.GRANTED
            sent = await self.ask(link, {"type": "apps.running"})
            self.assertEqual(sent[0]["reason"], reason)
            self.assertEqual(backend.calls, [])

    def test_no_hotspot_no_wired_target(self):
        with mock.patch.object(network, "list_interfaces", return_value=[]):
            self.assertEqual(discovery.wired_candidates(), [])


if __name__ == "__main__":
    unittest.main()
