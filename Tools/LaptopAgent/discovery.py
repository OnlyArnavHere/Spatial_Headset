"""Finding the headset: Bonjour browsing, a typed address, and the wired-hotspot address. Nothing scans the network.

The headset advertises `_spatiallauncher._tcp.local.` (handshake.SERVICE_TYPE) under a random instance name with the
TXT record `v=1`, plus `m=pair` while it shows a pairing code. browse() yields each (address, port) once: normally
only services without `m=pair`, with pairing=True only those with it. Everything found is only a place to connect to: the
handshake decides whether the peer is trusted.

Questions are QM (multicast reply), not QU: on Windows many sockets share UDP 5353, so a unicast reply reaches only one
of them and an already-advertising headset would never be found.
"""
import asyncio
import ipaddress
import secrets

from zeroconf import IPVersion, ServiceStateChange
from zeroconf.asyncio import AsyncServiceBrowser, AsyncServiceInfo, AsyncZeroconf

import network
from handshake import LINK_PORT, SERVICE_TYPE

RESOLVE_TIMEOUT_MS = 3000
MAX_RESOLVES = 32  # concurrent resolve tasks
MAX_PENDING = 256  # queued results, and remembered (address, port) pairs


def random_instance_name() -> str:
    """A Bonjour instance name from a CSPRNG with 64 bits: 'SL-' + 16 lowercase hex characters."""
    return "SL-" + secrets.token_hex(8)


def parse_headset(text: str) -> tuple[str, int]:
    """'192.168.1.5', '192.168.1.5:47810', '[fe80::1]:47810' or '::1' into (address, port). ValueError otherwise."""
    text = text.strip()
    port_text = None
    if text.startswith("["):
        host, sep, rest = text[1:].partition("]")
        if not sep or (rest and not rest.startswith(":")):
            raise ValueError("Expected [address]:port for an IPv6 address.")
        port_text = rest[1:] if rest else None
        address = ipaddress.IPv6Address(host)
    elif text.count(":") > 1:
        address = ipaddress.IPv6Address(text)
    else:
        host, sep, tail = text.partition(":")
        port_text = tail if sep else None
        address = ipaddress.IPv4Address(host)
    port = LINK_PORT
    if port_text is not None:
        if not port_text.isascii() or not port_text.isdigit() or not 1 <= int(port_text) <= 65535:
            raise ValueError("Port must be 1-65535.")
        port = int(port_text)
    return str(address), port


def wired_candidates() -> list[tuple[str, int]]:
    """The phone's hotspot address when the USB hotspot adapter is present, else nothing."""
    try:
        interfaces = network.list_interfaces()
    except Exception:
        return []
    return [(network.PHONE_ADDRESS, LINK_PORT)] if network.pick_hotspot(interfaces) is not None else []


async def browse(zeroconf: AsyncZeroconf | None = None, *, interfaces=None, pairing: bool = False):
    """Async generator of deduplicated (address, port) for every advertised headset. Runs until closed or cancelled;
    wrap it in asyncio.timeout / wait_for to stop. pairing=False yields headsets that are not showing a pairing code
    (no `m=pair`), pairing=True only those that are. Pass an AsyncZeroconf to reuse one, else one is created (and closed).

    Bounded against a flooding network: a name already being resolved is skipped, at most MAX_RESOLVES resolves run
    at once (further announcements are ignored), the result queue holds MAX_PENDING (further results are dropped) and
    the dedupe set remembers the newest MAX_PENDING entries."""
    own = zeroconf is None
    azc = zeroconf or (AsyncZeroconf(interfaces=interfaces) if interfaces is not None else AsyncZeroconf())
    found: asyncio.Queue = asyncio.Queue(MAX_PENDING)
    seen: dict[tuple[str, int], None] = {}  # insertion-ordered
    tasks: set[asyncio.Task] = set()
    resolving: set[str] = set()
    closed = False

    async def resolve(name: str) -> None:
        info = AsyncServiceInfo(SERVICE_TYPE, name)
        if not await info.async_request(azc.zeroconf, RESOLVE_TIMEOUT_MS):
            return
        if info.properties.get(b"v") != b"1" or not info.port:
            return
        if (info.properties.get(b"m") == b"pair") != pairing:
            return
        for address in info.parsed_addresses(IPVersion.All):
            ip = ipaddress.ip_address(address)
            if ip.is_link_local and ip.version == 6:
                continue  # no scope to connect with
            try:
                found.put_nowait((address, info.port))
            except asyncio.QueueFull:
                return

    def finished(name: str, task: asyncio.Task) -> None:
        tasks.discard(task)
        resolving.discard(name)

    def on_change(zeroconf, service_type, name, state_change):
        if closed or state_change is ServiceStateChange.Removed:
            return
        if name in resolving or len(tasks) >= MAX_RESOLVES:
            return
        resolving.add(name)
        task = asyncio.ensure_future(resolve(name))
        tasks.add(task)
        task.add_done_callback(lambda t, n=name: finished(n, t))

    browser = AsyncServiceBrowser(azc.zeroconf, SERVICE_TYPE, handlers=[on_change])
    try:
        while True:
            item = await found.get()
            if item not in seen:
                seen[item] = None
                if len(seen) > MAX_PENDING:
                    del seen[next(iter(seen))]
                yield item
    finally:
        closed = True
        try:
            await browser.async_cancel()
            pending = list(tasks)
            for task in pending:
                task.cancel()
            await asyncio.gather(*pending, return_exceptions=True)
        finally:
            if own:
                await azc.async_close()
