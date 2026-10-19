"""Checks wired-mode detection (network.py) without a phone, then shows what this laptop sees right now.

  python selftest_wired.py            the checks, then a live read of this laptop's adapters and firewall
  python selftest_wired.py --no-live  the checks only

The checks feed network.py made-up adapters and firewall rules: the USB hotspot adapter is picked over a Wi-Fi one on
the same subnet and over other adapters; the phone's own address and other subnets are never picked; a Public profile
with no matching rule blocks the agent's three ports; program rules (as Windows' "allow access" prompt makes them),
port rules, Any rules and port ranges allow them only on the right profile; service and store-app rules never count;
and the fix commands allow exactly the blocked ports on that adapter only. The live part only reads (no admin rights,
nothing changed) and can't prove the phone reaches the laptop: that needs the phone (see Doc/wired.md).
"""
import sys

import network
from network import Interface, Need, Rule

results: list[tuple[bool, str]] = []


def check(ok: bool, what: str) -> None:
    results.append((ok, what))
    print(f"{'PASS' if ok else 'FAIL'}: {what}", flush=True)


def checks() -> None:
    wifi = Interface("Wi-Fi", "192.168.1.20", "Intel(R) Wi-Fi 6 AX203", "Private")
    usb = Interface("Ethernet 3", "172.20.10.4", "Apple Mobile Device Ethernet", "Public")
    wifi_hotspot = Interface("Wi-Fi 2", "172.20.10.6", "Intel(R) Wi-Fi 6 AX203", "Public")
    phone = Interface("Ethernet 4", "172.20.10.1", "Apple Mobile Device Ethernet", "Public")
    near = Interface("Ethernet 5", "172.20.10.20", "Apple Mobile Device Ethernet", "Public")

    check(network.pick_hotspot([wifi, wifi_hotspot, usb]) == usb, "the USB adapter is picked over Wi-Fi on the same subnet")
    check(network.pick_hotspot([wifi, wifi_hotspot]) == wifi_hotspot and not wifi_hotspot.usb,
          "without USB, a hotspot address over Wi-Fi is still found (and called not USB)")
    check(network.pick_hotspot([wifi, phone, near]) is None,
          "the phone's own 172.20.10.1 and addresses outside 172.20.10.0/28 are never picked")
    check(usb.profile == "Public" and Interface("a", "172.20.10.3", "", "").profile == "Public" and
          Interface("a", "172.20.10.3", "", "Private").profile == "Private" and
          Interface("a", "172.20.10.3", "", "DomainAuthenticated").profile == "Domain",
          "the firewall profile follows the network category (none yet counts as Public)")

    wanted = network.needs(8765)
    check([(n.protocol, n.port) for n in wanted] == [("TCP", 8765)], "the agent needs one inbound port: TCP 8765")
    check(network.blocked("Public", True, [], wanted) == wanted, "with no rules, Public blocks the control port")
    check(network.blocked("Public", False, [], wanted) == [], "with the Public firewall off nothing is blocked")

    python_rule = Rule("Private, Public", "?", "?", r"C:\Python313\python.exe")
    mediamtx_private = Rule("Private", "?", "?", r"D:\agent\bin\mediamtx.exe")
    check(network.blocked("Public", True, [python_rule], wanted) == [],
          "a program rule from Windows' prompt allows its program's ports, on its profiles only")
    check(network.blocked("Public", True, [mediamtx_private], wanted) == wanted, "another program's rule doesn't count")
    check(network.blocked("Private", True, [Rule("Public", "TCP", "8765", "Any")], wanted) == wanted,
          "a rule for another profile doesn't count")
    port_rules = [Rule("Public", "TCP", "8100,8765", "Any"), Rule("Any", "TCP", "8700-8800", "Any")]
    check(all(network.blocked("Public", True, [r], wanted) == [] for r in port_rules), "port lists, ranges and Any profiles allow")
    check(network.blocked("Public", True, [Rule("Public", "UDP", "Any", "Any")], wanted) == wanted,
          "a UDP-only rule leaves the TCP control port blocked")
    scoped = [Rule("Any", "Any", "Any", "Any", scope="service"), Rule("Any", "Any", "Any", "Any", scope="package"),
              Rule("Any", "?", "?", "?")]
    check(network.blocked("Public", True, scoped, wanted) == wanted,
          "service and store-app rules, and rules unreadable without admin rights, never count")
    check(network.blocked("Public", True, [Rule("Public", "TCP", "8765", r"C:\Other\other.exe")], wanted) == wanted,
          "a port rule for another program doesn't allow ours")

    commands = network.fix_commands("Ethernet 3", "Public", wanted)
    check(len(commands) == 1 and '-InterfaceAlias "Ethernet 3"' in commands[0] and "-Profile Public" in commands[0] and
          "-Direction Inbound" in commands[0] and "-Action Allow" in commands[0] and
          "-Protocol TCP -LocalPort 8765" in commands[0],
          f"the fix allows exactly that port, inbound, on that adapter and profile only: {commands[0]}")

    report = network.WiredReport(usb, True, wanted, commands, complete=False)
    text = "\n".join(report.lines())
    check("host 172.20.10.4" in text and "WARNING" in text and "probably blocks" in text and commands[0] in text,
          "the agent's banner names the address, warns, and prints the commands")
    none = "\n".join(network.WiredReport(None).lines())
    check("no iPhone hotspot adapter" in none and "Doc/wired.md" in none, "with no phone it says what to do")


def live() -> None:
    print("\nThis laptop now (read-only):", flush=True)
    try:
        for interface in network.list_interfaces():
            mark = "  <- hotspot" if interface.on_hotspot else ""
            print(f"  {interface.alias!r}: {interface.address} ({interface.description or '?'}, "
                  f"{interface.category or 'no profile'}){mark}")
    except OSError as error:
        print(f"  couldn't list adapters: {error}")
    for line in network.wired_report(8765).lines():
        print(" " + line)


def main() -> int:
    checks()
    if "--no-live" not in sys.argv:
        live()
    failed = [what for ok, what in results if not ok]
    print(f"\n{len(results) - len(failed)} passed, {len(failed)} failed", flush=True)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
