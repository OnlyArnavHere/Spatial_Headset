"""Wired mode: the headset iPhone's USB Personal Hotspot.

With Personal Hotspot on and the phone plugged into this laptop by USB, Windows gets an "Apple Mobile Device Ethernet"
adapter on the phone's hotspot subnet, 172.20.10.0/28: the phone is the gateway (172.20.10.1) and this laptop gets one
of 172.20.10.2 to .14. The headset then reaches the agent at that address over the cable, without Wi-Fi.

Windows usually files that network under the Public firewall profile, where inbound connections are blocked unless a
rule allows them. This module finds the hotspot adapter and its address, reads the firewall profile and rules for it
(read-only, no admin rights needed) and says which of the agent's ports would be blocked, with the exact command that
allows them on that adapter only. The pure functions (pick_hotspot, blocked, fix_commands) take plain data, so
selftest_wired.py checks them without a phone.
"""
import ipaddress
import json
import subprocess
from dataclasses import dataclass

HOTSPOT_NETWORK = ipaddress.ip_network("172.20.10.0/28")
PHONE_ADDRESS = "172.20.10.1"
USB_ADAPTER_HINTS = ("apple mobile device ethernet", "iphone usb")


@dataclass(frozen=True)
class Interface:
    alias: str
    address: str
    description: str
    category: str  # "Public", "Private", "DomainAuthenticated" or "" (no profile yet)

    @property
    def on_hotspot(self) -> bool:
        try:
            return ipaddress.ip_address(self.address) in HOTSPOT_NETWORK and self.address != PHONE_ADDRESS
        except ValueError:
            return False

    @property
    def usb(self) -> bool:
        text = f"{self.alias} {self.description}".lower()
        return any(hint in text for hint in USB_ADAPTER_HINTS)

    @property
    def profile(self) -> str:
        """The firewall profile that applies: Domain, Private or Public (no or unknown category counts as Public)."""
        return {"DomainAuthenticated": "Domain", "Private": "Private"}.get(self.category, "Public")


@dataclass(frozen=True)
class Rule:
    """An enabled inbound Allow rule, as far as it matters here."""
    profile: str    # "Any" or a list like "Domain, Private, Public"
    protocol: str   # "TCP", "UDP" or "Any"
    ports: str      # "Any" or "8765,8766" or "8000-9000"
    program: str    # "Any" or a full path
    scope: str = ""  # "service" or "package" when the rule only covers a Windows service or a store app


@dataclass(frozen=True)
class Need:
    """A port the headset must reach and the program that listens on it."""
    protocol: str
    port: int
    program: str  # file name, e.g. "python.exe" or "mediamtx.exe"
    what: str


def pick_hotspot(interfaces: list[Interface]) -> Interface | None:
    """The USB hotspot adapter's address on this laptop, else any address on the hotspot subnet (Wi-Fi hotspot)."""
    on_hotspot = [i for i in interfaces if i.on_hotspot]
    usb = [i for i in on_hotspot if i.usb]
    return (usb or on_hotspot or [None])[0]


def _port_matches(ports: str, port: int) -> bool:
    for part in (p.strip() for p in ports.split(",")):
        if part.lower() == "any":
            return True
        if "-" in part:
            low, _, high = part.partition("-")
            if low.isdigit() and high.isdigit() and int(low) <= port <= int(high):
                return True
        elif part.isdigit() and int(part) == port:
            return True
    return False


def allows(rule: Rule, profile: str, need: Need) -> bool:
    if rule.scope or rule.program == "?":
        return False  # only for a Windows service or a store app, or unreadable without admin: never counted
    profile_ok = rule.profile.lower() == "any" or profile.lower() in rule.profile.lower()
    named = rule.program.lower() not in ("any", "")
    program_ok = not named or rule.program.lower().replace("/", "\\").endswith("\\" + need.program.lower())
    if "?" in (rule.protocol, rule.ports):
        # Port filter hidden without admin rights. A rule for the program itself is what Windows' "allow access"
        # prompt creates (every port of that program), so it counts; an unreadable rule for any program doesn't.
        return profile_ok and named and program_ok
    protocol_ok = rule.protocol.lower() == "any" or rule.protocol.lower() == need.protocol.lower()
    ports_ok = rule.protocol.lower() == "any" or _port_matches(rule.ports or "Any", need.port)
    return profile_ok and protocol_ok and program_ok and ports_ok


def blocked(profile: str, firewall_on: bool, rules: list[Rule], needs: list[Need]) -> list[Need]:
    """The needed ports no rule allows on that profile (none when the profile's firewall is off)."""
    if not firewall_on:
        return []
    return [n for n in needs if not any(allows(r, profile, n) for r in rules)]


def needs(input_port: int) -> list[Need]:
    return [Need("TCP", input_port, "python.exe", "control and input")]


def fix_commands(alias: str, profile: str, needed: list[Need]) -> list[str]:
    """Commands for an administrator PowerShell that allow exactly these ports, on this adapter only."""
    commands = []
    for protocol in ("TCP", "UDP"):
        ports = ",".join(str(n.port) for n in needed if n.protocol == protocol)
        if ports:
            commands.append(f'New-NetFirewallRule -DisplayName "Spatial Launcher agent (USB {protocol})" -Direction Inbound '
                            f'-Action Allow -Profile {profile} -InterfaceAlias "{alias}" -Protocol {protocol} -LocalPort {ports}')
    return commands


# --- Reading Windows (read-only PowerShell; no admin rights needed) -----------------------------------------------

_INTERFACES = r"""
Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | ForEach-Object {
  $a = Get-NetAdapter -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue
  $p = Get-NetConnectionProfile -InterfaceIndex $_.InterfaceIndex -ErrorAction SilentlyContinue
  [pscustomobject]@{ Alias = $_.InterfaceAlias; Address = $_.IPAddress; Description = [string]$a.InterfaceDescription;
                     Category = [string]$p.NetworkCategory }
} | ConvertTo-Json -Compress
"""

_FIREWALL = r"""
$profile = '%PROFILE%'
$rules = @{}
$scoped = @{}
Get-NetFirewallRule -PolicyStore ActiveStore -Direction Inbound -Enabled True -Action Allow -ErrorAction SilentlyContinue |
  ForEach-Object {
    $rules[$_.InstanceID] = [string]$_.Profile
    # Store apps' rules belong to a user (Owner) or a package resource group: they never cover the agent.
    if ($_.Owner -or ([string]$_.Group).StartsWith('@{')) { $scoped[$_.InstanceID] = 'package' } }
$ports = @{}
Get-NetFirewallPortFilter -PolicyStore ActiveStore -ErrorAction SilentlyContinue | ForEach-Object {
  if ($rules.ContainsKey($_.InstanceID)) { $ports[$_.InstanceID] = @([string]$_.Protocol, (($_.LocalPort | ForEach-Object { [string]$_ }) -join ',')) } }
$apps = @{}
Get-NetFirewallApplicationFilter -PolicyStore ActiveStore -ErrorAction SilentlyContinue | ForEach-Object {
  if ($rules.ContainsKey($_.InstanceID)) {
    $apps[$_.InstanceID] = [string]$_.Program
    if ($_.Package) { $scoped[$_.InstanceID] = 'package' } } }
Get-NetFirewallServiceFilter -PolicyStore ActiveStore -ErrorAction SilentlyContinue | ForEach-Object {
  if ($rules.ContainsKey($_.InstanceID) -and [string]$_.Service -ne 'Any') { $scoped[$_.InstanceID] = 'service' } }
$list = foreach ($id in $rules.Keys) {
  $pf = $ports[$id]
  [pscustomobject]@{ Profile = $rules[$id]; Protocol = $(if ($pf) { $pf[0] } else { '?' });
                     Ports = $(if ($pf) { $pf[1] } else { '?' }); Program = $(if ($apps.ContainsKey($id)) { $apps[$id] } else { '?' });
                     Scope = [string]$scoped[$id] } }
$fw = Get-NetFirewallProfile -PolicyStore ActiveStore -Name $profile
[pscustomobject]@{ Enabled = [string]$fw.Enabled; Complete = ($ports.Count -eq $rules.Count -and $apps.Count -eq $rules.Count);
                   Rules = @($list) } | ConvertTo-Json -Depth 4 -Compress
"""


def _powershell(script: str, timeout: float = 60.0) -> object:
    result = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
                            capture_output=True, text=True, timeout=timeout)
    if result.returncode != 0 or not result.stdout.strip():
        raise OSError((result.stderr.strip().splitlines() or ["no output"])[-1])
    return json.loads(result.stdout)


def list_interfaces() -> list[Interface]:
    data = _powershell(_INTERFACES)
    if isinstance(data, dict):
        data = [data]
    return [Interface(str(d.get("Alias") or ""), str(d.get("Address") or ""), str(d.get("Description") or ""),
                      str(d.get("Category") or "")) for d in data or []]


def read_firewall(profile: str) -> tuple[bool, list[Rule], bool]:
    """Whether the profile's firewall is on, the enabled inbound Allow rules, and whether every rule's filters could be
    read (without admin rights Windows hides many of them; those rules are never counted as allowing anything)."""
    data = _powershell(_FIREWALL.replace("%PROFILE%", profile))
    enabled = str(data.get("Enabled", "True")).lower() != "false"
    rules = [Rule(str(r.get("Profile") or "Any"), str(r.get("Protocol") or "?"), str(r.get("Ports") or "?"),
                  str(r.get("Program") or "?"), str(r.get("Scope") or "")) for r in data.get("Rules") or []]
    return enabled, rules, bool(data.get("Complete", False))


@dataclass
class WiredReport:
    interface: Interface | None
    firewall_on: bool | None = None
    blocked: list = None
    commands: list = None
    error: str = ""
    complete: bool = True

    def lines(self) -> list[str]:
        if self.interface is None:
            return ["Wired (USB):       no iPhone hotspot adapter (172.20.10.x) found. Turn on Personal Hotspot on the",
                    "                   headset iPhone, plug it into this laptop by USB and trust the computer. See Doc/wired.md."]
        i = self.interface
        kind = "USB" if i.usb else "hotspot (not USB: Wi-Fi?)"
        out = [f"Wired (USB):       host {i.address}   on {i.alias!r} ({kind}), firewall profile {i.profile}"]
        if self.error:
            out.append(f"                   couldn't read the firewall ({self.error}); if the headset can't connect, run:")
            out += [f"                     {c}" for c in fix_commands(i.alias, i.profile, self.blocked or [])]
        elif self.firewall_on is False:
            out.append(f"                   {i.profile} firewall is off: nothing to allow")
        elif self.blocked:
            ports = ", ".join(f"{n.protocol} {n.port} ({n.what})" for n in self.blocked)
            likely = "" if self.complete else " probably"
            out.append(f"                   WARNING: the {i.profile} firewall{likely} blocks {ports} on this adapter.")
            if not self.complete:
                out.append("                   (some rules can only be read as administrator; run the agent elevated for an exact check)")
            out.append("                   Allow them on this adapter only (PowerShell as administrator):")
            out += [f"                     {c}" for c in self.commands]
        else:
            out.append(f"                   {i.profile} firewall: the agent's ports are allowed")
        return out


def wired_report(input_port: int) -> WiredReport:
    try:
        interface = pick_hotspot(list_interfaces())
    except (OSError, ValueError, subprocess.TimeoutExpired) as error:
        return WiredReport(None, error=str(error))
    if interface is None:
        return WiredReport(None)
    wanted = needs(input_port)
    try:
        firewall_on, rules, complete = read_firewall(interface.profile)
    except (OSError, ValueError, subprocess.TimeoutExpired) as error:
        return WiredReport(interface, None, wanted, fix_commands(interface.alias, interface.profile, wanted), str(error))
    still_blocked = blocked(interface.profile, firewall_on, rules, wanted)
    return WiredReport(interface, firewall_on, still_blocked, fix_commands(interface.alias, interface.profile, still_blocked),
                       complete=complete)
