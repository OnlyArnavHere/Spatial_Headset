#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif
using UnityEngine;

namespace SpatialLauncher.SpatialWindows
{
    /// <summary>
    /// Read-only device state for the Settings window's About and Laptop sections. Nothing here changes the phone: iOS
    /// doesn't let apps switch Wi-Fi or Bluetooth, and the app doesn't try. The thermal state comes from iOS
    /// (NSProcessInfo.thermalState) through Assets/Plugins/iOS/SpatialDeviceStatus.mm; elsewhere it reads "n/a".
    /// Bluetooth isn't read at all: asking iOS would show a Bluetooth permission prompt for no feature that needs it.
    /// </summary>
    public static class DeviceStatus
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern int SpatialLauncher_ThermalState();
        [DllImport("__Internal")] static extern bool SpatialLauncher_LowPowerMode();
#endif

        /// <summary>"Nominal", "Fair", "Serious" or "Critical" on iOS; "n/a" elsewhere.</summary>
        public static string ThermalState
        {
            get
            {
#if UNITY_IOS && !UNITY_EDITOR
                return ThermalName(SpatialLauncher_ThermalState()) + (SpatialLauncher_LowPowerMode() ? " (Low Power Mode)" : "");
#else
                return "n/a (editor)";
#endif
            }
        }

        /// <summary>NSProcessInfoThermalState's values.</summary>
        public static string ThermalName(int state) => state switch
        {
            0 => "Nominal",
            1 => "Fair",
            2 => "Serious",
            3 => "Critical",
            _ => "Unknown",
        };

        public static string Battery
        {
            get
            {
                float level = SystemInfo.batteryLevel;
                if (level < 0f) return "n/a";
                string status = SystemInfo.batteryStatus switch
                {
                    BatteryStatus.Charging => ", charging",
                    BatteryStatus.Full => ", full",
                    BatteryStatus.NotCharging => ", not charging",
                    _ => "",
                };
                return $"{Mathf.RoundToInt(level * 100f)}%{status}";
            }
        }

        /// <summary>What the network looks like to the app (iOS gives no more than this without special entitlements).</summary>
        public static string Network => Application.internetReachability switch
        {
            NetworkReachability.ReachableViaLocalAreaNetwork => "Wi-Fi or wired network connected",
            NetworkReachability.ReachableViaCarrierDataNetwork => "Cellular only: the laptop must be on the same Wi-Fi",
            _ => "No network",
        };

        public const string BluetoothNote = "Not read (would need Bluetooth permission); change it in iOS Settings";

        public static string Versions =>
            $"App {Application.version}  ·  Unity {Application.unityVersion}  ·  Agent protocol 3  ·  Settings v{SpatialSettings.CurrentVersion}";

        public static string Device => $"{SystemInfo.deviceModel}  ·  {SystemInfo.operatingSystem}";
    }
}
