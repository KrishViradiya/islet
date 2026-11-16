using System;
using System.Diagnostics;
using Islet.Core;
using Islet.Modules;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Islet;

/// <summary>Tray icon: Settings, updates, and the only way to quit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _restartToUpdate;

    public TrayIcon(Action openSettings, Action checkUpdates, Action quit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Settings…", null, (_, _) => openSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem($"Live Activities API · localhost:{LiveActivityServer.Port}") { Enabled = false });
        menu.Items.Add("Open data folder", null, (_, _) => Process.Start("explorer.exe", Paths.DataDir));
        menu.Items.Add("Check for updates", null, (_, _) => checkUpdates());
        _restartToUpdate = new Forms.ToolStripMenuItem("Restart to update", null, (_, _) => Updater.RestartToUpdate()) { Visible = false };
        menu.Items.Add(_restartToUpdate);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Islet", null, (_, _) => quit());

        _icon = new Forms.NotifyIcon
        {
            Icon = AppIcon(),
            Text = "Islet",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => openSettings();
        _icon.BalloonTipClicked += (_, _) => { if (Updater.UpdateReady) Updater.RestartToUpdate(); };
    }

    public void Notify(string title, string text, Forms.ToolTipIcon kind = Forms.ToolTipIcon.Info) =>
        _icon.ShowBalloonTip(5000, title, text, kind);

    public void ShowUpdateReady()
    {
        _restartToUpdate.Text = $"Restart to update ({Updater.ReadyVersion})";
        _restartToUpdate.Visible = true;
        Notify("Islet update ready", $"Version {Updater.ReadyVersion} is downloaded. Click to restart now, or it installs when you quit.");
    }

    /// <summary>The icon embedded in Islet.exe (assets/islet.ico); a drawn fallback for odd setups.</summary>
    private static Drawing.Icon AppIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && Drawing.Icon.ExtractAssociatedIcon(exe) is { } icon) return icon;
        }
        catch { /* fall through */ }
        return Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
