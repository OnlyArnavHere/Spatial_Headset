#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;

namespace SpatialLauncher.LaptopLink
{
    /// <summary>Advertises _spatiallauncher._tcp through dns_sd (SpatialLink.mm).</summary>
    public sealed class IosBonjour : IBonjourAdvertiser
    {
        [DllImport("__Internal")] static extern int SpatialLink_BonjourRegister(string instance, int port, int pairing);
        [DllImport("__Internal")] static extern void SpatialLink_BonjourUnregister();

        readonly NativeBonjourCore core = new NativeBonjourCore(SpatialLink_BonjourRegister, SpatialLink_BonjourUnregister);

        public void Register(string instance, int port, bool pairing) { core.Register(instance, port, pairing); }
        public void Unregister() { core.Unregister(); }
    }
}
#endif
