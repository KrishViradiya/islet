using System;
using System.Runtime.InteropServices;

namespace Islet.Interop;

/// <summary>
/// Reads/sets the default speaker volume through Windows Core Audio (the same API the volume
/// flyout uses), so the island can show its own volume HUD instead of the Windows one.
/// </summary>
internal static class AudioVolume
{
    public static (float Level, bool Muted)? Get()
    {
        try
        {
            var endpoint = Endpoint();
            Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out float level));
            Marshal.ThrowExceptionForHR(endpoint.GetMute(out bool muted));
            return (level, muted);
        }
        catch { return null; }
    }

    public static void Set(double level)
    {
        try
        {
            var ctx = Guid.Empty;
            Endpoint().SetMasterVolumeLevelScalar((float)Math.Clamp(level, 0, 1), ref ctx);
        }
        catch { /* no audio device */ }
    }

    public static void SetMute(bool muted)
    {
        try
        {
            var ctx = Guid.Empty;
            Endpoint().SetMute(muted, ref ctx);
        }
        catch { /* no audio device */ }
    }

    private static IAudioEndpointVolume Endpoint() => Activate<IAudioEndpointVolume>();

    // ---- microphone (meeting mode): mutes the default capture devices for every app at once

    /// <summary>null when there is no microphone.</summary>
    public static bool? MicMuted()
    {
        try
        {
            Marshal.ThrowExceptionForHR(Activate<IAudioEndpointVolume>(dataFlow: 1, role: 2).GetMute(out bool muted));
            return muted;
        }
        catch { return null; }
    }

    public static void SetMicMuted(bool muted)
    {
        // The "communications" and "default" microphones can differ (headset vs. webcam): mute both.
        foreach (var role in new[] { 2, 0 })
        {
            try
            {
                var ctx = Guid.Empty;
                Activate<IAudioEndpointVolume>(dataFlow: 1, role: role).SetMute(muted, ref ctx);
            }
            catch { /* no such device */ }
        }
    }

    // ---- peak meter: how loud the speakers are right now (drives the liquid music ripple)
    private static IAudioMeterInformation? _meter;
    private static DateTime _meterRefreshAt;

    /// <summary>Current output peak 0..1 (what the volume flyout's bar shows). Never throws.</summary>
    public static float Peak()
    {
        try
        {
            if (_meter == null || DateTime.Now > _meterRefreshAt)
            {
                _meter = Activate<IAudioMeterInformation>();
                _meterRefreshAt = DateTime.Now.AddSeconds(10); // follow default-device changes
            }
            return _meter.GetPeakValue(out float peak) == 0 ? peak : 0;
        }
        catch
        {
            _meter = null;
            _meterRefreshAt = DateTime.Now.AddSeconds(5);
            return 0;
        }
    }

    /// <param name="dataFlow">0 = speakers (eRender), 1 = microphones (eCapture).</param>
    /// <param name="role">0 = eConsole, 1 = eMultimedia, 2 = eCommunications.</param>
    private static T Activate<T>(int dataFlow = 0, int role = 1) where T : class
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(dataFlow, role, out var device));
        var iid = typeof(T).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var obj));
        return (T)obj;
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    // Methods must stay in vtable order, even the ones we don't call.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}
