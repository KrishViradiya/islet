using System;
using Islet.Core;
using Islet.Modules;
using Velopack;

namespace Islet;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack must run first: during install / update / uninstall it handles the event and exits.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ =>
            {
                // Leave nothing behind: no autostart entry, no Claude Code hooks pointing at a removed app.
                try { Startup.Set(false); } catch { }
                try { if (AgentInstaller.IsInstalled()) AgentInstaller.Uninstall(); } catch { }
            })
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
