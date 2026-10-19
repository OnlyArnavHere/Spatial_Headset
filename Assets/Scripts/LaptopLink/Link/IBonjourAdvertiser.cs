namespace SpatialLauncher.LaptopLink
{
    /// <summary>
    /// Advertises the headset's link port as a Bonjour service (_spatiallauncher._tcp) so a laptop can find it. One
    /// registration at a time: Register replaces the previous one. Called on the main thread only.
    /// </summary>
    public interface IBonjourAdvertiser
    {
        /// <param name="instance">The service instance name ("SL-" plus 16 lowercase hex), new for every registration.</param>
        /// <param name="pairing">True adds the TXT entry m=pair (the headset shows a pairing code).</param>
        void Register(string instance, int port, bool pairing);

        void Unregister();
    }

    /// <summary>Advertises nothing (the editor, and iOS until its native advertiser lands).</summary>
    public sealed class NullBonjour : IBonjourAdvertiser
    {
        public string Instance { get; private set; }
        public bool Pairing { get; private set; }
        public bool Registered { get { return Instance != null; } }

        public void Register(string instance, int port, bool pairing)
        {
            Instance = instance;
            Pairing = pairing;
        }

        public void Unregister()
        {
            Instance = null;
            Pairing = false;
        }
    }
}
