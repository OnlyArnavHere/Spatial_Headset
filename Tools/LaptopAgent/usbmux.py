"""A client for Apple's USB multiplexer (usbmuxd): TCP connections to an app on an iPhone over the USB cable, with no
Wi-Fi and no IP network between the two.

The multiplexer is part of the OS on macOS (/var/run/usbmuxd) and comes with Apple's driver on Windows (the Apple Mobile
Device Service of the Apple Devices app or iTunes, on 127.0.0.1:27015). Linux has the open-source usbmuxd daemon on the
same Unix socket path as macOS. The phone has to trust the computer (the "Trust This Computer?" prompt).

Protocol (plist variant, the one current Apple software speaks): every message is a 16-byte little-endian header
(total length including the header, version 1, message type 8 = plist, tag) and an XML property list. A client asks
"ListDevices" and gets "DeviceList"; or it sends "Connect" with a DeviceID and a PortNumber (in network byte order) and
gets a "Result" whose Number is 0 on success, after which the same socket is a raw byte stream to that port on the
phone. On the phone the app sees the connection arrive from its own loopback address.

Any wired USB link counts the same: Lightning or USB-C on the phone, USB-A or USB-C on the laptop, directly or
through an adapter or hub. usbmuxd reports them all as ConnectionType "USB"; the connector is never looked at.
Only devices attached by USB are used. usbmuxd also lists phones it reaches over Wi-Fi ("Network"); those are skipped,
because this module exists to keep traffic on the cable.
"""
import asyncio
import plistlib
import struct
import sys
from dataclasses import dataclass

WINDOWS_ADDRESS = ("127.0.0.1", 27015)
UNIX_PATH = "/var/run/usbmuxd"
HEADER = struct.Struct("<IIII")
VERSION_PLIST = 1
TYPE_PLIST = 8
MAX_REPLY = 1 << 20
TIMEOUT_S = 5.0
PROG = "SpatialLauncherAgent"

RESULTS = {1: "bad command", 2: "no such device", 3: "connection refused", 6: "bad version"}


class UsbmuxError(Exception):
    """usbmuxd is missing, refused, or answered something unexpected. str() is a sentence for the user."""


@dataclass(frozen=True)
class Device:
    device_id: int
    serial: str
    product_id: int = 0

    @property
    def key(self) -> str:
        """How the runner names this phone: 'usb:<serial>' (stable across replugging, unlike the DeviceID)."""
        return f"usb:{self.serial or self.device_id}"


async def _open(address=None):
    if address is not None:
        if isinstance(address, str):
            return await asyncio.open_unix_connection(address)
        return await asyncio.open_connection(*address)
    if sys.platform == "win32":
        return await asyncio.open_connection(*WINDOWS_ADDRESS)
    return await asyncio.open_unix_connection(UNIX_PATH)


def _message(payload: dict, tag: int) -> bytes:
    body = plistlib.dumps({**payload, "ClientVersionString": PROG, "ProgName": PROG})
    return HEADER.pack(HEADER.size + len(body), VERSION_PLIST, TYPE_PLIST, tag) + body


async def _read_reply(reader) -> dict:
    head = await reader.readexactly(HEADER.size)
    length, version, kind, _tag = HEADER.unpack(head)
    if version != VERSION_PLIST or kind != TYPE_PLIST or not HEADER.size <= length <= MAX_REPLY:
        raise UsbmuxError("The Apple USB service sent something unexpected.")
    body = await reader.readexactly(length - HEADER.size)
    try:
        reply = plistlib.loads(body)
    except Exception:
        raise UsbmuxError("The Apple USB service sent something unexpected.") from None
    if not isinstance(reply, dict):
        raise UsbmuxError("The Apple USB service sent something unexpected.")
    return reply


def missing_hint() -> str:
    if sys.platform == "win32":
        return ("The Apple USB service isn't running. Install Apple's \"Apple Devices\" app (or iTunes) from the "
                "Microsoft Store, then plug the iPhone in and tap Trust on it.")
    if sys.platform == "darwin":
        return "The Apple USB service (usbmuxd) isn't answering. Unplug the iPhone and plug it in again."
    return "usbmuxd isn't running. Install the usbmuxd package (libimobiledevice) and plug the iPhone in again."


async def list_devices(address=None, timeout: float = TIMEOUT_S) -> list[Device]:
    """The iPhones attached by USB cable. Raises UsbmuxError when the service can't be reached."""
    try:
        reader, writer = await asyncio.wait_for(_open(address), timeout)
    except (OSError, asyncio.TimeoutError):
        raise UsbmuxError(missing_hint()) from None
    try:
        writer.write(_message({"MessageType": "ListDevices"}, 1))
        await writer.drain()
        reply = await asyncio.wait_for(_read_reply(reader), timeout)
    except (OSError, asyncio.IncompleteReadError, asyncio.TimeoutError):
        raise UsbmuxError(missing_hint()) from None
    finally:
        writer.close()
    devices = []
    for entry in reply.get("DeviceList") or []:
        props = entry.get("Properties") if isinstance(entry, dict) else None
        kind = props.get("ConnectionType") if isinstance(props, dict) else None
        if not isinstance(kind, str) or kind.strip().upper() != "USB":
            continue  # a phone usbmuxd reaches over Wi-Fi ("Network"): not the cable
        device_id = props.get("DeviceID", entry.get("DeviceID"))
        if not isinstance(device_id, int):
            continue
        serial = props.get("SerialNumber")
        devices.append(Device(device_id, serial if isinstance(serial, str) else "",
                              props.get("ProductID") if isinstance(props.get("ProductID"), int) else 0))
    return devices


async def connect(device_id: int, port: int, address=None, timeout: float = TIMEOUT_S):
    """A (reader, writer) pair to `port` on the phone, over the cable. Raises ConnectionRefusedError when nothing
    listens there (the headset app isn't waiting for a laptop), UsbmuxError for anything else."""
    try:
        reader, writer = await asyncio.wait_for(_open(address), timeout)
    except (OSError, asyncio.TimeoutError):
        raise UsbmuxError(missing_hint()) from None
    try:
        writer.write(_message({"MessageType": "Connect", "DeviceID": device_id,
                               "PortNumber": struct.unpack("<H", struct.pack(">H", port))[0]}, 2))
        await writer.drain()
        reply = await asyncio.wait_for(_read_reply(reader), timeout)
    except (OSError, asyncio.IncompleteReadError, asyncio.TimeoutError):
        writer.close()
        raise UsbmuxError("The Apple USB service closed the connection.") from None
    except UsbmuxError:
        writer.close()
        raise
    number = reply.get("Number")
    if reply.get("MessageType") != "Result" or number != 0:
        writer.close()
        if number == 3:
            raise ConnectionRefusedError(f"nothing listens on port {port} on the iPhone")
        raise UsbmuxError(f"The Apple USB service refused the connection ({RESULTS.get(number, number)}).")
    return reader, writer


def describe(address=None) -> str:
    """One line for the app's Wired status: is an iPhone on the cable? (Blocking: call it off the GUI thread.)"""
    try:
        devices = asyncio.run(list_devices(address))
    except UsbmuxError as e:
        return str(e)
    if not devices:
        return "No iPhone on the USB cable yet. Plug it in and tap Trust on the phone."
    if len(devices) == 1:
        return "iPhone on the USB cable. No Wi-Fi needed."
    return f"{len(devices)} iPhones on the USB cable. The first headset app that answers is used."
