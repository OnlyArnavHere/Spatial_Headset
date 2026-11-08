"""QR pairing: the laptop shows a QR code with a 100-bit code and its fingerprint, the headset scans it, and the laptop
pairs with exactly that headset, over a known address, the USB cable or Bonjour. No strike, no rate-limit cost and no
proof ever reaches a headset that didn't scan the QR."""
import asyncio
import functools
import shutil
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import agent
import discovery
import fake_headset
import handshake
import identity
import pair_protocol
import pairing
import usbmux
from handshake import HandshakeError
from test_discovery import multicast_unavailable
from test_session_e2e import new_identity, until
from test_stream_link import FakeUsbmuxd, device

FAST = {"iterations": 1000}


def instance_browse(host, port, every=0.02):
    """Bonjour stand-in: the headset that scanned the QR (its own instance) keeps showing up; nothing else."""
    async def browse(pairing=False, instance=None, **_kw):
        if instance is None:
            await asyncio.sleep(3600)
        while True:
            yield host, port
            await asyncio.sleep(every)
    return browse


async def never_browse(**_kw):
    await asyncio.sleep(3600)
    yield None


class QrTextTests(unittest.TestCase):
    def test_invite_round_trips_and_stays_alphanumeric(self):
        ident = new_identity()
        invite = pairing.new_qr_invite(ident.spki_der)
        self.assertEqual(len(invite.code), pairing.QR_CODE_LEN)
        self.assertEqual(pairing.parse_qr(invite.payload), (invite.code, ident.fingerprint))
        self.assertTrue(set(invite.payload) <= set("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:"))
        self.assertEqual(pairing.parse_code(invite.code), invite.code)
        self.assertNotEqual(pairing.new_qr_invite(ident.spki_der).code, invite.code)

    def test_qr_id_and_instance(self):
        self.assertEqual(pairing.qr_id("A" * 20), pairing.qr_id("A" * 20))
        self.assertRegex(pairing.qr_id("A" * 20), r"^[0-9a-f]{16}$")
        self.assertEqual(pairing.qr_instance("A" * 20), "SL-" + pairing.qr_id("A" * 20))
        # Known answer, shared with the C# side (LaptopLinkCoreTest).
        self.assertEqual(pairing.qr_id("0123456789ABCDEFGHJK"), "8d64db921aec5743")

    def test_rejects_other_text(self):
        for text in ("", "hello", "https://example.com", "SLPAIR:ABC:ABCDEFGHIJKLMNOP",
                     "SLPAIR:0123456789ABCDEFGHJU:ABCDEFGHIJKLMNOP", "SLPAIR:0123456789ABCDEFGHJK:abcdefghijklmnop",
                     "SLPAIR:0123456789ABCDEFGHJK:ABCDEFGHIJKLMNO1", "XLPAIR:0123456789ABCDEFGHJK:ABCDEFGHIJKLMNOP",
                     "SLPAIR:0123456789ABCDEFGHJK:ABCDEFGHIJKLMNOP:X"):
            with self.assertRaises(ValueError, msg=text):
                pairing.parse_qr(text)

    def test_issuer_arms_and_forgets(self):
        now = [0.0]
        issuer = pairing.CodeIssuer(clock=lambda: now[0])
        issuer.issue()
        self.assertIsNone(issuer.qr_id)
        invite = pairing.new_qr_invite(b"laptop")
        self.assertEqual(issuer.arm_qr(invite.payload), invite.code)
        self.assertEqual((issuer.qr_id, issuer.qr_fingerprint), (invite.qr_id, invite.fingerprint))
        issuer.issue()  # a new shown code replaces the scanned one
        self.assertIsNone(issuer.qr_fingerprint)
        issuer.arm_qr(invite.payload)
        now[0] = pairing.CODE_TTL
        self.assertIsNone(issuer.qr_id)
        self.assertFalse(issuer.active)


class QrExchangeTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        self.ident = new_identity()
        self.store = pairing.PairStore(self.tmp / "agent")
        self.gate = handshake.HeadsetGate(attempts=1)  # a QR check must never use up the rate
        self.headset = fake_headset.FakeHeadset(self.tmp / "headset", name="QR Headset", pairing_mode=True,
                                                confirm=False, gate=self.gate, pair_iterations=1000,
                                                echo=lambda *a: None)
        self.port = await self.headset.start()
        self.addAsyncCleanup(self.headset.stop)
        self.invite = pairing.new_qr_invite(self.ident.spki_der)

    async def pair(self, ident=None, code=None, qr=True):
        return await pair_protocol.agent_pair_connect("127.0.0.1", self.port, ident or self.ident, self.store,
                                                      code or self.invite.code, "Laptop", qr=qr, **FAST)

    async def test_scanned_qr_pairs_without_asking_on_the_headset(self):
        await self.headset.scan_qr(self.invite.payload)
        result = await self.pair()
        self.assertEqual(result.name, "QR Headset")
        self.assertEqual(self.headset.paired, ["Laptop"])
        self.assertEqual(self.headset.prompts, [])  # confirm=False would have declined: nothing was asked
        self.assertEqual([p.name for p in self.store.list()], ["QR Headset"])
        self.assertFalse(self.headset.issuer.active)  # spent

    async def test_unscanned_headset_says_not_pairing_for_free(self):
        for _ in range(4):  # the gate allows 1 attempt a minute: refunds keep the checks free
            with self.assertRaises(HandshakeError) as caught:
                await self.pair()
            self.assertEqual(caught.exception.reason, "peer refused: not pairing")
        self.assertEqual(self.headset.issuer.strikes, 0)
        self.assertTrue(self.headset.issuer.active)  # its shown code still works
        self.assertEqual(self.store.list(), [])

    async def test_another_laptop_cannot_use_a_scanned_qr(self):
        await self.headset.scan_qr(self.invite.payload)
        with self.assertRaises(HandshakeError) as caught:
            await self.pair(ident=new_identity())  # knows the code (saw the screen) but not the key
        self.assertEqual(caught.exception.reason, "peer refused: not pairing")
        self.assertEqual(self.headset.issuer.strikes, 0)
        await self.pair()  # the right laptop still can

    async def test_typed_code_is_refused_by_a_scanned_headset(self):
        await self.headset.scan_qr(self.invite.payload)
        with self.assertRaises(HandshakeError) as caught:
            await self.pair(code=self.invite.code, qr=False)
        self.assertEqual(caught.exception.reason, "peer refused: not pairing")
        self.assertEqual(self.headset.issuer.strikes, 0)

    async def test_old_headset_gets_no_proof(self):
        """A headset from before QR pairing ignores "qr" and answers P2; the agent must stop before P3."""
        frames = []

        async def old_headset(reader, writer):
            ch = handshake.Channel(reader, writer, 5.0, 8, pair_protocol.VERSION)
            frames.append(await ch.read("pair"))
            other = new_identity()
            await ch.write("pair-hello", nonce=handshake._b64s(b"\x01" * 32), spki=handshake._b64s(other.spki_der),
                           name="Old")
            rest = await reader.read()  # whatever the agent sent before closing
            frames.append(__import__("json").loads(rest[4:]) if rest else None)
            writer.close()

        server = await asyncio.start_server(old_headset, "127.0.0.1", 0)
        self.addAsyncCleanup(server.wait_closed)
        self.addCleanup(server.close)
        with self.assertRaises(HandshakeError) as caught:
            await pair_protocol.agent_pair_connect("127.0.0.1", server.sockets[0].getsockname()[1], self.ident,
                                                   self.store, self.invite.code, "Laptop", qr=True, **FAST)
        self.assertEqual(caught.exception.reason, "no qr")
        await until(lambda: len(frames) == 2, 5, "the old headset to see the close")
        self.assertEqual(frames[0]["qr"], self.invite.qr_id)
        self.assertTrue(frames[1] is None or frames[1]["type"] == "error", frames[1])  # never a "pair-proof"


class QrPairCommandTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        self.ident = new_identity()
        self.store = pairing.PairStore(self.tmp / "agent")
        self.headset = fake_headset.FakeHeadset(self.tmp / "headset", name="QR Headset", pairing_mode=True,
                                                gate=handshake.HeadsetGate(attempts=1), pair_iterations=1000,
                                                echo=lambda *a: None)
        self.port = await self.headset.start()
        self.addAsyncCleanup(self.headset.stop)

    async def test_pair_command_finishes_when_the_headset_scans(self):
        shown, said = [], []
        def read_line(prompt):  # the user never types: parks this daemon thread
            _Block.wait()

        task = asyncio.ensure_future(agent.pair_command(
            self.ident, self.store, "Laptop", read_line=read_line, say=said.append,
            browse=instance_browse("127.0.0.1", self.port), qr_shown=shown.append, iterations=1000))
        await until(lambda: shown, 5, "the QR to be shown")
        await asyncio.sleep(0.3)  # a few checks find nothing
        self.assertEqual(self.headset.issuer.strikes, 0)
        await self.headset.scan_qr(shown[0].payload)
        self.assertEqual(await asyncio.wait_for(task, 10), 0, said)
        self.assertEqual(self.headset.paired, ["Laptop"])
        self.assertIn("Paired with QR Headset", said[-1])
        self.assertNotIn("Pairing cancelled.", said)

    async def test_usb_cable_is_checked_too(self):
        mux = FakeUsbmuxd([device(5, "PHONE")], {(5, handshake.LINK_PORT): self.port})
        address = await mux.start()
        self.addAsyncCleanup(mux.stop)
        invite = pairing.new_qr_invite(self.ident.spki_der)
        with mock.patch.object(usbmux, "connect", functools.partial(usbmux.connect, address=address)):
            task = asyncio.ensure_future(agent.qr_pair(
                invite, self.ident, self.store, "Laptop", browse=never_browse,
                usb=functools.partial(usbmux.list_devices, address), pair_kw=FAST, poll_s=0.05))
            await asyncio.sleep(0.3)
            await self.headset.scan_qr(invite.payload)
            result = await asyncio.wait_for(task, 10)
        self.assertEqual(result.name, "QR Headset")

    async def test_old_headset_is_asked_once(self):
        tries = []

        async def fake_pair_once(host, port, ident, store, code, name, announce, kw, opener=None):
            tries.append(host)
            raise HandshakeError("no qr")

        invite = pairing.new_qr_invite(self.ident.spki_der)
        with mock.patch.object(agent, "_pair_once", fake_pair_once):
            task = asyncio.ensure_future(agent.qr_pair(invite, self.ident, self.store, "Laptop",
                                                       browse=instance_browse("10.0.0.9", 47810), poll_s=0.02))
            await asyncio.sleep(0.3)
            task.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await task
        self.assertEqual(tries, ["10.0.0.9"])

    async def test_name_clash_gives_up(self):
        async def fake_pair_once(*a, **kw):
            raise HandshakeError("name taken")

        said = []
        with mock.patch.object(agent, "_pair_once", fake_pair_once):
            result = await asyncio.wait_for(agent.qr_pair(
                pairing.new_qr_invite(self.ident.spki_der), self.ident, self.store, "Laptop",
                browse=instance_browse("10.0.0.9", 47810), say=said.append, poll_s=0.02), 5)
        self.assertIsNone(result)
        self.assertIn("already exists", said[0])


class _Block:
    """Parks the fake stdin thread (a daemon) for good."""
    event = __import__("threading").Event()

    @classmethod
    def wait(cls):
        cls.event.wait()
        raise EOFError


@unittest.skipIf(multicast_unavailable(), multicast_unavailable() or "")
class QrBonjourTests(unittest.IsolatedAsyncioTestCase):
    async def test_laptop_finds_the_scanned_headset_by_its_instance_name(self):
        from zeroconf.asyncio import AsyncZeroconf
        tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, tmp, ignore_errors=True)
        ident = new_identity()
        store = pairing.PairStore(tmp / "agent")
        other = fake_headset.FakeHeadset(tmp / "other", name="Other", pairing_mode=True, advertise=True,
                                         gate=handshake.HeadsetGate(attempts=1), echo=lambda *a: None)
        mine = fake_headset.FakeHeadset(tmp / "mine", name="Mine", pairing_mode=True, advertise=True,
                                        gate=handshake.HeadsetGate(attempts=1), pair_iterations=1000,
                                        echo=lambda *a: None)
        for h in (other, mine):
            await h.start()
            self.addAsyncCleanup(h.stop)
        browser = AsyncZeroconf(interfaces=["127.0.0.1"])
        self.addAsyncCleanup(browser.async_close)
        invite = pairing.new_qr_invite(ident.spki_der)
        task = asyncio.ensure_future(agent.qr_pair(invite, ident, store, "Laptop",
                                                   browse=functools.partial(discovery.browse, browser),
                                                   pair_kw=FAST, browse_s=3.0))
        await asyncio.sleep(1.0)
        await mine.scan_qr(invite.payload)
        self.assertEqual(mine.instance, pairing.qr_instance(invite.code))
        result = await asyncio.wait_for(task, 20)
        self.assertEqual(result.name, "Mine")
        self.assertEqual((other.refusals, other.issuer.strikes), ([], 0))  # never dialled


if __name__ == "__main__":
    unittest.main()
