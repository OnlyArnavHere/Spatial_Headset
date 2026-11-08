namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Wired mode (Settings > Laptop > Link: Wired (USB)). The laptop reaches the app over the USB cable through Apple's
    /// USB multiplexer, with no network at all (the stream transport, LaptopLink/Link/StreamLinkTransport.cs). Any cable
    /// works the same way: Lightning or USB-C on the phone, USB-A or USB-C on the laptop, with or without an adapter.
    /// Nothing here changes the phone's networking. See Doc/wired.md.
    /// </summary>
    public static class UsbCable
    {
        /// <summary>The short how-to Settings shows for Wired.</summary>
        public const string Help =
            "Plug the phone into the laptop with any USB cable (USB-C or Lightning) and tap Trust. On the laptop app "
            + "turn on Wired (USB cable). No Wi-Fi needed.";
    }
}
