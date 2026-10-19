using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Wired mode (Settings > Laptop > Link: Wired (USB)): the headset iPhone's Personal Hotspot over a USB cable. The
    /// phone is the gateway, 172.20.10.1, and the laptop gets one of 172.20.10.2 to .14 (the agent prints which, with
    /// --wired). <see cref="FindAgentAsync"/> tries those addresses on the agent's control port and returns the first that
    /// answers. Nothing here changes the phone's networking; iOS doesn't let apps switch the hotspot on (Settings does).
    /// See Doc/wired.md.
    /// </summary>
    public static class UsbHotspot
    {
        public const string Prefix = "172.20.10.";
        public const string PhoneAddress = "172.20.10.1";

        /// <summary>An address a laptop can have on the hotspot (172.20.10.2 to .14).</summary>
        public static bool IsLaptopAddress(string host)
        {
            if (string.IsNullOrEmpty(host) || !host.StartsWith(Prefix) || !int.TryParse(host.Substring(Prefix.Length), out int last)) return false;
            return last >= 2 && last <= 14;
        }

        /// <summary>The addresses a laptop on the hotspot can have, in the order DHCP usually hands them out.</summary>
        public static IEnumerable<string> CandidateHosts()
        {
            for (int i = 2; i <= 14; i++) yield return Prefix + i;
        }

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

        /// <summary>
        /// The first of <paramref name="hosts"/> that accepts a TCP connection on <paramref name="port"/> within
        /// <paramref name="timeoutMs"/> (all tried at once), or null. Only opens and closes a connection: nothing is sent.
        /// </summary>
        public static async Task<string> FindAgentAsync(IEnumerable<string> hosts, int port, int timeoutMs = 800)
        {
            var attempts = new List<Task<string>>();
            foreach (var host in hosts) attempts.Add(TryConnect(host, port, timeoutMs));
            while (attempts.Count > 0)
            {
                var done = await Task.WhenAny(attempts);
                attempts.Remove(done);
                if (done.Result != null) return done.Result;
            }
            return null;
        }

        static async Task<string> TryConnect(string host, int port, int timeoutMs)
        {
            if (!IPAddress.TryParse(host, out var address)) return null;
            using (var client = new TcpClient(address.AddressFamily))
            {
                try
                {
                    var connect = client.ConnectAsync(address, port);
                    // A connect still pending at the timeout fails later, when the client is closed: observe that.
                    _ = connect.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    if (await Task.WhenAny(connect, Task.Delay(timeoutMs)) != connect || connect.IsFaulted) return null;
                    return client.Connected ? host : null;
                }
                catch (SocketException) { return null; }
                catch (ObjectDisposedException) { return null; }
            }
        }

        /// <summary>The short how-to Settings shows for Wired.</summary>
        public static string Help(string phoneAddress) =>
            (phoneAddress != null ? "Hotspot on (this phone is 172.20.10.1). " : "Turn on Settings > Personal Hotspot, ") +
            "plug into the laptop, run agent.py --wired, then Find laptop. Doc/wired.md";
    }
}
