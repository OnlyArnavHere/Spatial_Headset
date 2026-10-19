using System;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Wired mode (Settings > Laptop > Link: Wired (USB)): the headset iPhone's Personal Hotspot over a USB cable. The
    /// phone is the gateway, 172.20.10.1; the laptop's agent.py dials it. Nothing here changes the phone's networking; iOS doesn't let apps switch the hotspot on (Settings does).
    /// See Doc/wired.md.
    /// </summary>
    public static class UsbHotspot
    {
        public const string Prefix = "172.20.10.";
        public const string PhoneAddress = "172.20.10.1";

        /// <summary>The phone's own hotspot address, if Personal Hotspot is on (null otherwise or if it can't tell).</summary>
        public static string PhoneHotspotAddress()
        {
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var address in nic.GetIPProperties().UnicastAddresses)
                        if (address.Address.AddressFamily == AddressFamily.InterNetwork && address.Address.ToString() == PhoneAddress)
                            return PhoneAddress;
                }
            }
            catch (Exception) { /* not supported on this platform: unknown */ }
            return null;
        }

        /// <summary>The short how-to Settings shows for Wired.</summary>
        public const string Help =
            "Turn on Personal Hotspot and plug the phone into the laptop by USB. On the laptop, agent.py dials 172.20.10.1.";
    }
}
