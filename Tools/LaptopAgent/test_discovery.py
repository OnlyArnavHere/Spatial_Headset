"""Unit tests for headset discovery: address parsing, the wired candidate and Bonjour browsing on loopback.

  python -m unittest test_discovery -v        (from Tools/LaptopAgent)

The mDNS tests are skipped when this machine can't do multicast on 127.0.0.1.
"""
import asyncio
import re
import socket
import types
import unittest
from unittest import mock

from zeroconf import ServiceInfo
from zeroconf.asyncio import AsyncZeroconf

import discovery
import handshake
import network


def multicast_unavailable() -> str | None:
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            s.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_IF, socket.inet_aton("127.0.0.1"))
            s.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, 1)
        finally:
            s.close()
    except OSError as e:
        return f"multicast is not available on 127.0.0.1 ({e})"
    return None


class ParseTests(unittest.TestCase):
    def test_accepts(self):
        for text, expected in {
            "192.168.1.5": ("192.168.1.5", handshake.LINK_PORT),
            " 192.168.1.5:5000 ": ("192.168.1.5", 5000),
            "10.0.0.1:65535": ("10.0.0.1", 65535),
            "[fe80::1]:5000": ("fe80::1", 5000),
            "[::1]": ("::1", handshake.LINK_PORT),
            "::1": ("::1", handshake.LINK_PORT),
        }.items():
            self.assertEqual(discovery.parse_headset(text), expected, text)

    def test_rejects(self):
        for text in ["", "laptop.local", "300.1.1.1", "1.2.3", "1.2.3.4:", "1.2.3.4:0", "1.2.3.4:65536",
                     "1.2.3.4:-1", "1.2.3.4:abc", "1.2.3.4:5:6", "[::1", "[::1]x", "[::1]:99999", "[1.2.3.4]:5"]:
            with self.subTest(text=text), self.assertRaises(ValueError):
                discovery.parse_headset(text)


class WiredTests(unittest.TestCase):
    def test_hotspot_present(self):
        phone_side = network.Interface("Ethernet 3", "172.20.10.2", "Apple Mobile Device Ethernet", "Public")
        with mock.patch.object(network, "list_interfaces", return_value=[phone_side]):
            self.assertEqual(discovery.wired_candidates(), [("172.20.10.1", handshake.LINK_PORT)])

    def test_no_hotspot(self):
        wifi = network.Interface("Wi-Fi", "192.168.1.20", "Intel Wi-Fi", "Private")
        with mock.patch.object(network, "list_interfaces", return_value=[wifi]):
            self.assertEqual(discovery.wired_candidates(), [])

    def test_interface_listing_fails(self):
        with mock.patch.object(network, "list_interfaces", side_effect=OSError("no powershell")):
            self.assertEqual(discovery.wired_candidates(), [])


class InstanceNameTests(unittest.TestCase):
    def test_format_and_entropy(self):
        names = {discovery.random_instance_name() for _ in range(200)}
        self.assertEqual(len(names), 200)
        for name in names:
            self.assertRegex(name, r"^SL-[0-9a-f]{16}$")


class FakeBrowser:
    """Stands in for AsyncServiceBrowser: remembers the handler so a test can fire announcements."""
    handlers = None
    cancelled = False

    def __init__(self, zc, service_type, handlers=None, **kw):
        FakeBrowser.handlers = handlers
        FakeBrowser.cancelled = False

    async def async_cancel(self):
        FakeBrowser.cancelled = True


class FakeInfo:
    """Stands in for AsyncServiceInfo. Subclasses set what a request does."""
    created: list = []
    cancelled = 0
    addresses = ["10.0.0.1"]
    hang = True

    def __init__(self, service_type, name):
        FakeInfo.created.append(name)
        self.properties = {b"v": b"1"}
        self.port = 47810

    async def async_request(self, zc, timeout, question_type=None):
        if FakeInfo.hang:
            try:
                await asyncio.sleep(60)
            except asyncio.CancelledError:
                FakeInfo.cancelled += 1
                raise
        return True

    def parsed_addresses(self, version=None):
        return list(FakeInfo.addresses)


class BoundsTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        FakeInfo.created, FakeInfo.cancelled, FakeInfo.hang, FakeInfo.addresses = [], 0, True, ["10.0.0.1"]
        for target, fake in (("AsyncServiceBrowser", FakeBrowser), ("AsyncServiceInfo", FakeInfo)):
            p = mock.patch.object(discovery, target, fake)
            p.start()
            self.addCleanup(p.stop)
        self.azc = types.SimpleNamespace(zeroconf=object())

    async def start(self):
        gen = discovery.browse(self.azc)
        first = asyncio.ensure_future(gen.__anext__())
        await asyncio.sleep(0.05)  # the generator has built its browser
        return gen, first

    async def stop(self, gen, first):
        first.cancel()
        await asyncio.gather(first, return_exceptions=True)
        await gen.aclose()

    def announce(self, name):
        FakeBrowser.handlers[0](None, handshake.SERVICE_TYPE, name, discovery.ServiceStateChange.Added)

    async def test_names_being_resolved_are_skipped(self):
        gen, first = await self.start()
        for _ in range(10):
            self.announce("SL-aaaa." + handshake.SERVICE_TYPE)
        await asyncio.sleep(0.05)
        self.assertEqual(len(FakeInfo.created), 1)
        await self.stop(gen, first)

    async def test_concurrent_resolves_are_capped(self):
        gen, first = await self.start()
        for n in range(100):
            self.announce(f"SL-{n:016x}." + handshake.SERVICE_TYPE)
        await asyncio.sleep(0.05)
        self.assertEqual(len(FakeInfo.created), discovery.MAX_RESOLVES)
        await self.stop(gen, first)

    async def test_close_cancels_every_resolve_task(self):
        gen, first = await self.start()
        for n in range(10):
            self.announce(f"SL-{n:016x}." + handshake.SERVICE_TYPE)
        await asyncio.sleep(0.05)
        await self.stop(gen, first)
        self.assertEqual(FakeInfo.cancelled, 10)
        self.assertTrue(FakeBrowser.cancelled)
        leftovers = [t for t in asyncio.all_tasks() if t is not asyncio.current_task() and not t.done()]
        self.assertEqual(leftovers, [])

    async def test_slot_frees_when_a_resolve_ends(self):
        FakeInfo.hang = False
        gen, first = await self.start()
        for n in range(discovery.MAX_RESOLVES * 3):
            self.announce(f"SL-{n:016x}." + handshake.SERVICE_TYPE)
            await asyncio.sleep(0)
            await asyncio.sleep(0)
        await asyncio.sleep(0.05)
        self.assertEqual(len(FakeInfo.created), discovery.MAX_RESOLVES * 3)
        await self.stop(gen, first)

    async def test_queue_and_seen_are_bounded(self):
        FakeInfo.hang = False
        FakeInfo.addresses = [f"10.1.{n // 256}.{n % 256}" for n in range(discovery.MAX_PENDING + 44)]
        gen = discovery.browse(self.azc)
        first = asyncio.ensure_future(gen.__anext__())
        await asyncio.sleep(0.05)
        self.announce("SL-bbbb." + handshake.SERVICE_TYPE)
        await asyncio.sleep(0.05)  # all addresses were offered at once; only MAX_PENDING fit
        got = [await first]
        while True:
            try:
                got.append(await asyncio.wait_for(gen.__anext__(), 0.2))
            except asyncio.TimeoutError:
                break
        self.assertLessEqual(len(got), discovery.MAX_PENDING + 1)
        self.assertGreaterEqual(len(got), discovery.MAX_PENDING)
        await gen.aclose()


@unittest.skipIf(multicast_unavailable(), multicast_unavailable() or "")
class BonjourTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.registrar = AsyncZeroconf(interfaces=["127.0.0.1"])
        self.browser = AsyncZeroconf(interfaces=["127.0.0.1"])

    async def asyncTearDown(self):
        await self.registrar.async_close()
        await self.browser.async_close()

    async def advertise(self, port: int, txt=None) -> str:
        name = discovery.random_instance_name() + "." + handshake.SERVICE_TYPE
        info = ServiceInfo(handshake.SERVICE_TYPE, name, port=port, properties=txt or {"v": "1"},
                           addresses=[socket.inet_aton("127.0.0.1")])
        await self.registrar.async_register_service(info)
        return name

    async def collect(self, count: int, timeout: float = 15.0) -> list[tuple[str, int]]:
        found = []

        async def run():
            async for item in discovery.browse(self.browser):
                found.append(item)
                if len(found) >= count:
                    return
        try:
            await asyncio.wait_for(run(), timeout)
        except asyncio.TimeoutError:
            pass
        return found

    async def test_finds_a_service(self):
        await self.advertise(47811)
        self.assertEqual(await self.collect(1), [("127.0.0.1", 47811)])

    async def test_two_random_instance_names_are_both_found(self):
        a = await self.advertise(47812)
        b = await self.advertise(47813)
        self.assertNotEqual(a, b)
        self.assertEqual(sorted(await self.collect(2)), [("127.0.0.1", 47812), ("127.0.0.1", 47813)])

    async def test_ignores_other_protocol_versions(self):
        await self.advertise(47814, {"v": "2"})
        await self.advertise(47815)
        self.assertEqual(await self.collect(2, timeout=4.0), [("127.0.0.1", 47815)])

    async def test_pair_mode_services_are_hidden_in_normal_mode(self):
        await self.advertise(47817)
        await self.advertise(47818, {"v": "1", "m": "pair"})
        self.assertEqual(await self.collect(2, timeout=4.0), [("127.0.0.1", 47817)])

    async def test_pairing_mode_yields_only_pair_services(self):
        await self.advertise(47819)
        await self.advertise(47820, {"v": "1", "m": "pair"})
        found = []

        async def run():
            async for item in discovery.browse(self.browser, pairing=True):
                found.append(item)
        try:
            await asyncio.wait_for(run(), 4.0)
        except asyncio.TimeoutError:
            pass
        self.assertEqual(found, [("127.0.0.1", 47820)])

    async def test_duplicates_are_reported_once(self):
        await self.advertise(47816)
        found = await self.collect(2, timeout=3.0)
        self.assertEqual(found, [("127.0.0.1", 47816)])


if __name__ == "__main__":
    unittest.main()
