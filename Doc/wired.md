# Wired mode (USB Personal Hotspot)

The headset iPhone shares its Personal Hotspot with the laptop over the USB cable. The phone is the gateway
(172.20.10.1) and the laptop gets one of 172.20.10.2 to .14. The headset then reaches the agent over the cable, with no
Wi-Fi involved.

## Steps

1. On the iPhone: Settings > Personal Hotspot > Allow Others to Join. The option only appears if the carrier plan
   allows a hotspot.
2. Plug the iPhone into the laptop and tap Trust on the phone. Windows needs Apple's driver, which comes with the Apple
   Devices app or iTunes. An "Apple Mobile Device Ethernet" adapter then appears.
3. On the laptop: `python agent.py --wired` (add `--cast-only` or `--input` as usual). The banner shows the line
   `Wired (USB): host 172.20.10.x` and checks the firewall for that adapter.
4. On the headset: Settings > Laptop > Link > Wired (USB), then Find laptop. It tries 172.20.10.2 to .14 on the control
   port and fills in Address. Type the pair code the agent printed.

## Firewall

Windows usually files the hotspot network under the Public profile, where inbound connections are blocked unless a
rule allows them. The agent needs no inbound rule: the laptop dials the headset, and the video and input travel back
over that same connection (WebRTC, replies allowed by the firewall's normal stateful handling). The only port the
agent opens is MediaMTX on 127.0.0.1:8554, which no other machine can reach. The banner under `--wired` says so.

## Checked so far, and what only the phone can show

Checked on the laptop (`python selftest_wired.py`): the hotspot adapter is picked and the firewall logic and fix
commands are right, all with made-up adapters, plus a read-only live check. The headset's Find laptop was checked in
the editor against this machine.

Only the phone can show:
- that the adapter appears and gets a 172.20.10.x address;
- that iOS lets the app reach the laptop over the cable (Local Network permission);
- that WebRTC video uses the cable;
- the latency.
