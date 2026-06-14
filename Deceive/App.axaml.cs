// Deceive macOS port — original project by molenzwiebel (github.com/molenzwiebel/Deceive), GPL-3.0.
using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Deceive.Platform;

namespace Deceive;

public partial class App : Application
{
    private AvaloniaUserInterface? _ui;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // This is a menu-bar app: it must keep running with no main window open.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        // Pick the platform implementation. Only macOS is fully implemented; on other OSes we still
        // construct it so the app at least starts (useful for development/build on Linux/CI).
        IPlatform platform = new MacOsPlatform();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Trace_NonMac();

        _ui = new AvaloniaUserInterface(platform);

        // Run the Deceive startup sequence on the UI thread (it shows dialogs). Fire-and-forget;
        // it manages its own lifetime via App.Shutdown on failure.
        Dispatcher.UIThread.Post(async () => await StartupHandler.StartDeceiveAsync(_ui, platform));

        base.OnFrameworkInitializationCompleted();
    }

    private static void Trace_NonMac() =>
        System.Diagnostics.Trace.WriteLine(
            "Warning: Deceive is running on a non-macOS platform. Process detection and Riot Client " +
            "launching use the macOS implementation and will likely not work here.");

    /// <summary>Cleanly shut down the application from anywhere (UI or background thread).</summary>
    public static void Shutdown()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            else
                Environment.Exit(0);
        });
    }
}
