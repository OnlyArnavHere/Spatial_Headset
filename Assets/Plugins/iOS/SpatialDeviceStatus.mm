// Read-only device state for the Settings window (DeviceStatus.cs). iOS only.
#import <Foundation/Foundation.h>

extern "C" {

// NSProcessInfoThermalState: 0 nominal, 1 fair, 2 serious, 3 critical.
int SpatialLauncher_ThermalState(void)
{
    return (int)[[NSProcessInfo processInfo] thermalState];
}

bool SpatialLauncher_LowPowerMode(void)
{
    return [[NSProcessInfo processInfo] isLowPowerModeEnabled];
}

}
