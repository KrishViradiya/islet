using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using Islet.Core;
using Microsoft.Win32;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Forms = System.Windows.Forms;

namespace Islet.Modules;

public sealed record DeviceEvent(string Title, string Subtitle, string Glyph, Brush Accent, double? Level = null);

/// <summary>
/// AirPods-style moments for Windows: Bluetooth devices connecting / disconnecting (with battery level
/// when the device reports it), charger plugged / unplugged, and low-battery warnings at 20% and 10%.
/// </summary>
public sealed class DeviceModule : IDisposable
{
    // DEVPKEY_Bluetooth_Battery — reported by most headphones and earbuds (Windows 10 1809+).
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";

    private readonly Dispatcher _ui;
    private readonly Dictionary<string, string> _names = new();
    private DeviceWatcher? _watcher;
    private bool _enumerated;
    private Forms.PowerLineStatus _lastPower = Forms.PowerLineStatus.Unknown;
    private int _lastWarnedAt = 101;

    public event Action<DeviceEvent>? Happened;

    public DeviceModule(Dispatcher ui) => _ui = ui;

    public void Start()
    {
        _lastPower = Forms.SystemInformation.PowerStatus.PowerLineStatus;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        try
        {
            var selector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
            _watcher = DeviceInformation.CreateWatcher(selector);
            _watcher.Added += (sender, info) => { var pending = OnConnectedAsync(info); };
            _watcher.Removed += (_, update) => OnDisconnected(update.Id);
            _watcher.EnumerationCompleted += (_, _) => _enumerated = true;
            _watcher.Start();
        }
        catch (Exception ex) { Log.Error("bluetooth watch", ex); }
    }

    // ------------------------------------------------------------------ Bluetooth

    private async Task OnConnectedAsync(DeviceInformation info)
    {
        var name = string.IsNullOrWhiteSpace(info.Name) ? "Bluetooth device" : info.Name;
        lock (_names) _names[info.Id] = name;
        if (!_enumerated) return; // already connected when Islet started: no pop-up

        string glyph = "";
        try
        {
            var device = await BluetoothDevice.FromIdAsync(info.Id);
            if (device?.ClassOfDevice.MajorClass == BluetoothMajorClass.AudioVideo) glyph = ""; // headphones
            else if (device?.ClassOfDevice.MajorClass == BluetoothMajorClass.Phone) glyph = "";
            else if (device?.ClassOfDevice.MajorClass == BluetoothMajorClass.Peripheral) glyph = "";
        }
        catch { /* keep the generic glyph */ }

        var battery = await BatteryAsync(info.Id);
        Raise(new DeviceEvent(name, battery is int b ? $"Connected · {b}%" : "Connected", glyph, Palette.Blue, battery / 100.0));
    }

    private void OnDisconnected(string id)
    {
        string? name;
        lock (_names) { _names.TryGetValue(id, out name); _names.Remove(id); }
        if (name != null && _enumerated) Raise(new DeviceEvent(name, "Disconnected", "", Palette.Gray));
    }

    /// <summary>Battery % of a Bluetooth device, if it reports one (best effort; null when unknown).</summary>
    private static async Task<int?> BatteryAsync(string deviceId)
    {
        try
        {
            // The level lives on the device's PnP node; find nodes in the same container that carry the key.
            var info = await DeviceInformation.CreateFromIdAsync(deviceId, new[] { "System.Devices.ContainerId" });
            if (info.Properties.TryGetValue("System.Devices.ContainerId", out var container) && container is Guid id)
            {
                var nodes = await DeviceInformation.FindAllAsync(
                    $"System.Devices.ContainerId:=\"{{{id}}}\"", new[] { BatteryKey }, DeviceInformationKind.Device);
                foreach (var node in nodes)
                    if (node.Properties.TryGetValue(BatteryKey, out var v) && v is byte level) return level;
            }
        }
        catch { /* not reported */ }
        return null;
    }

    // ------------------------------------------------------------------ power

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;
        var status = Forms.SystemInformation.PowerStatus;
        if (status.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery)) return;

        int percent = (int)Math.Round(status.BatteryLifePercent * 100);
        var line = status.PowerLineStatus;
        if (line != _lastPower && line != Forms.PowerLineStatus.Unknown)
        {
            _lastPower = line;
            if (line == Forms.PowerLineStatus.Online)
            {
                _lastWarnedAt = 101;
                Raise(new DeviceEvent("Charging", $"{percent}%", "", Palette.Green, percent / 100.0));
            }
            else
            {
                var left = status.BatteryLifeRemaining > 0 ? $" · about {TimeSpan.FromSeconds(status.BatteryLifeRemaining):h\\h\\ mm\\m} left" : "";
                Raise(new DeviceEvent("On battery", $"{percent}%{left}", "", Palette.Gray, percent / 100.0));
            }
        }
    }

    /// <summary>Called every 30 s: low-battery warnings (once at 20%, once at 10%).</summary>
    public void Tick()
    {
        var status = Forms.SystemInformation.PowerStatus;
        if (status.BatteryChargeStatus.HasFlag(Forms.BatteryChargeStatus.NoSystemBattery)) return;
        if (status.PowerLineStatus == Forms.PowerLineStatus.Online) return;

        int percent = (int)Math.Round(status.BatteryLifePercent * 100);
        foreach (var threshold in new[] { 10, 20 })
        {
            if (percent <= threshold && _lastWarnedAt > threshold)
            {
                _lastWarnedAt = threshold;
                Raise(new DeviceEvent(threshold == 10 ? "Battery very low" : "Battery low", $"{percent}% · plug in soon",
                    "", threshold == 10 ? Palette.Red : Palette.Orange, percent / 100.0));
                break;
            }
        }
    }

    private void Raise(DeviceEvent e) => _ui.InvokeAsync(() => Happened?.Invoke(e));

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        try { _watcher?.Stop(); } catch { }
    }
}
