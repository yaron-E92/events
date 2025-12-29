using Microsoft.Maui.Storage;

namespace EventMessenger.Platforms.Android;

internal static class DeviceIdentity
{
    private const string PreferenceKey = "EventMessenger.DeviceId";

    internal static string GetDeviceId()
    {
        var deviceId = Preferences.Get(PreferenceKey, string.Empty);
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            deviceId = Guid.NewGuid().ToString("N");
            Preferences.Set(PreferenceKey, deviceId);
        }

        return deviceId;
    }
}
