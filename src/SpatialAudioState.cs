// Read-only Windows Spatial Audio state. Windows 10 version 1809 or later.
// Public API: https://learn.microsoft.com/windows/uwp/api/windows.media.audio.spatialaudiodeviceconfiguration
// Setting a format requires its owning provider app; this utility opens Windows
// settings for changes instead of writing undocumented endpoint properties.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Win10Audio
{
    public sealed class SpatialState
    {
        public string Label { get; set; }
        public bool Supported { get; set; }
    }

    public static class SpatialAudioState
    {
        private static readonly Type ConfigurationType = LoadType(
            "Windows.Media.Audio.SpatialAudioDeviceConfiguration, Windows.Media.Audio, ContentType=WindowsRuntime");
        private static readonly Dictionary<Guid, string> FormatNames = LoadFormatNames();

        public static SpatialState Read(string deviceId)
        {
            SpatialState unknown = new SpatialState { Label = Localization.Text("SpatialConfigure"), Supported = false };
            if (ConfigurationType == null || String.IsNullOrEmpty(deviceId)) return unknown;

            try
            {
                // This WinRT factory requires a device-interface ID. A bare
                // IMMDevice endpoint ID is accepted but misleadingly reports
                // unsupported; convert render endpoints to their interface ID.
                string interfaceId = deviceId;
                if (deviceId.StartsWith("{0.0.0.", StringComparison.OrdinalIgnoreCase))
                    interfaceId = @"\\?\SWD#MMDEVAPI#" + deviceId + "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

                MethodInfo factory = ConfigurationType.GetMethod("GetForDeviceId", BindingFlags.Public | BindingFlags.Static);
                if (factory == null) return unknown;
                object configuration = factory.Invoke(null, new object[] { interfaceId });
                if (configuration == null) return unknown;

                object supportedValue = ReadProperty(configuration, "IsSpatialAudioSupported");
                if (!(supportedValue is bool)) return unknown;
                bool supported = (bool)supportedValue;
                if (!supported)
                    return new SpatialState { Label = Localization.Text("SpatialUnavailable"), Supported = false };

                // Default is the user's selected format. Active may differ due
                // to a temporary system change, and is only a read fallback.
                string selected = ReadProperty(configuration, "DefaultSpatialAudioFormat") as string;
                string active = ReadProperty(configuration, "ActiveSpatialAudioFormat") as string;
                Guid format;
                if (!Guid.TryParse(selected, out format) && !Guid.TryParse(active, out format))
                    return new SpatialState { Label = unknown.Label, Supported = true };

                if (format == Guid.Empty)
                    return new SpatialState { Label = Localization.Text("SpatialOff"), Supported = true };

                string label;
                if (!FormatNames.TryGetValue(format, out label)) label = Localization.Text("SpatialActive");
                else if (label == "WindowsSonicHeadphones") label = Localization.Text("WindowsSonicHeadphones");
                return new SpatialState { Label = label, Supported = true };
            }
            catch (Exception)
            {
                // Missing metadata, an unplugged endpoint, or a driver failure
                // must never be presented as a successful Off/Sonic selection.
                return unknown;
            }
        }

        private static object ReadProperty(object configuration, string propertyName)
        {
            PropertyInfo property = ConfigurationType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            return property == null ? null : property.GetValue(configuration, null);
        }

        private static Type LoadType(string name)
        {
            try { return Type.GetType(name, false); }
            catch (Exception) { return null; }
        }

        private static Dictionary<Guid, string> LoadFormatNames()
        {
            Dictionary<Guid, string> names = new Dictionary<Guid, string>();
            Type subtypeType = LoadType("Windows.Media.Audio.SpatialAudioFormatSubtype, Windows.Media.Audio, ContentType=WindowsRuntime");
            if (subtypeType == null) return names;
            // Store a stable key, then resolve it in Read so live language
            // changes do not leave a translated value in this static cache.
            AddFormat(names, subtypeType, "WindowsSonic", "WindowsSonicHeadphones");
            AddFormat(names, subtypeType, "DolbyAtmosForHeadphones", "Dolby Atmos for Headphones");
            AddFormat(names, subtypeType, "DolbyAtmosForHomeTheater", "Dolby Atmos for Home Theater");
            AddFormat(names, subtypeType, "DolbyAtmosForSpeakers", "Dolby Atmos for Speakers");
            AddFormat(names, subtypeType, "DTSHeadphoneX", "DTS Headphone:X");
            AddFormat(names, subtypeType, "DTSXForHomeTheater", "DTS:X for Home Theater");
            AddFormat(names, subtypeType, "DTSXUltra", "DTS:X Ultra");
            return names;
        }

        private static void AddFormat(Dictionary<Guid, string> names, Type subtypeType, string propertyName, string label)
        {
            try
            {
                PropertyInfo property = subtypeType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
                if (property == null) return;
                Guid format;
                if (Guid.TryParse(property.GetValue(null, null) as string, out format) && format != Guid.Empty)
                    names[format] = label;
            }
            catch (Exception) { /* A newer provider may not exist on this OS. */ }
        }
    }
}
