// Deceive macOS port — original project by molenzwiebel (github.com/molenzwiebel/Deceive), GPL-3.0.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Deceive.Platform;

namespace Deceive;

/// <summary>
///     Avalonia implementation of <see cref="IUserInterface" />. This is the rewrite of the original
///     Windows Forms tray code: <c>NotifyIcon</c> → <see cref="TrayIcon" />, <c>ContextMenuStrip</c>/
///     <c>ToolStripMenuItem</c> → <see cref="NativeMenu" />/<see cref="NativeMenuItem" /> (rendered as
///     a real <c>NSStatusItem</c> menu on macOS), and <c>MessageBox.Show</c> → small Avalonia dialog
///     windows.
///
///     Every public method marshals to the UI thread, so the (background-threaded) network engine can
///     call them freely.
/// </summary>
internal sealed class AvaloniaUserInterface : IUserInterface
{
    private readonly IPlatform _platform;
    private readonly TrayIcon _trayIcon;
    private MainController? _controller;

    internal AvaloniaUserInterface(IPlatform platform)
    {
        _platform = platform;

        using var stream = AssetLoader.Open(new Uri("avares://Deceive/Resources/deceive.png"));
        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(stream),
            ToolTipText = StartupHandler.DeceiveTitle,
            IsVisible = true
        };

        var icons = new TrayIcons { _trayIcon };
        TrayIcon.SetIcons(Application.Current!, icons);

        RebuildMenu();
    }

    /// <summary>Wire the tray menu to the engine once it has been constructed.</summary>
    internal void AttachController(MainController controller)
    {
        _controller = controller;
        _controller.StateChanged += (_, _) => Dispatcher.UIThread.Post(RebuildMenu);
        RebuildMenu();
    }

    // === Tray / NativeMenu (rewrite of the WinForms ContextMenuStrip) ===

    private void RebuildMenu()
    {
        var menu = new NativeMenu();

        // Disabled "about" header showing the version (matches the original tray).
        menu.Add(new NativeMenuItem(StartupHandler.DeceiveTitle) { IsEnabled = false });

        if (_controller is null)
        {
            // Engine not ready yet: only offer Quit.
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(Item("Quit", async (_, _) => await QuitAsync()));
            _trayIcon.Menu = menu;
            return;
        }

        var controller = _controller;

        menu.Add(new NativeMenuItemSeparator());

        // Enabled toggle.
        menu.Add(Check("Enabled", controller.Enabled, async (_, _) => await controller.ToggleEnabledAsync()));

        // Status Type submenu (Online / Offline / Mobile).
        var statusMenu = new NativeMenu();
        statusMenu.Add(Check("Online", controller.Status == "chat", async (_, _) => await controller.SetStatusAsync("chat")));
        statusMenu.Add(Check("Offline", controller.Status == "offline", async (_, _) => await controller.SetStatusAsync("offline")));
        statusMenu.Add(Check("Mobile", controller.Status == "mobile", async (_, _) => await controller.SetStatusAsync("mobile")));
        menu.Add(new NativeMenuItem("Status Type") { Menu = statusMenu });

        // Default Status on Startup submenu.
        var startup = Persistence.GetStartupStatus();
        var startupMenu = new NativeMenu();
        startupMenu.Add(Check("Online", startup == "chat", (_, _) => SetStartup("chat")));
        startupMenu.Add(Check("Offline", startup == "offline", (_, _) => SetStartup("offline")));
        startupMenu.Add(Check("Mobile", startup == "mobile", (_, _) => SetStartup("mobile")));
        startupMenu.Add(Check("Remember Last", startup == "last", (_, _) => SetStartup("last")));
        menu.Add(new NativeMenuItem("Default Status on Startup") { Menu = startupMenu });

        // Lobby chat toggle.
        menu.Add(Check("Enable lobby chat", controller.ConnectToMuc, (_, _) => controller.SetConnectToMuc(!controller.ConnectToMuc)));

        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Restart and launch a different game", async (_, _) => await RestartWithDifferentGameAsync()));
        menu.Add(Item("Quit", async (_, _) => await QuitAsync()));

        _trayIcon.Menu = menu;
    }

    private void SetStartup(string status)
    {
        Persistence.SetStartupStatus(status);
        RebuildMenu();
    }

    private static NativeMenuItem Item(string header, EventHandler onClick)
    {
        var item = new NativeMenuItem(header);
        item.Click += onClick;
        return item;
    }

    private static NativeMenuItem Check(string header, bool isChecked, EventHandler onClick)
    {
        var item = new NativeMenuItem(header)
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = isChecked
        };
        item.Click += onClick;
        return item;
    }

    private async Task RestartWithDifferentGameAsync()
    {
        if (!await ShowYesNoAsync("Restart Deceive to launch a different game? This will also stop related games if they are running."))
            return;

        TryKill();
        await Task.Delay(2000);

        Persistence.SetDefaultLaunchGame(LaunchGame.Prompt);
        try
        {
            Process.Start(new ProcessStartInfo { FileName = Environment.ProcessPath!, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to relaunch Deceive: {ex}");
        }

        Environment.Exit(0);
    }

    private async Task QuitAsync()
    {
        if (!await ShowYesNoAsync("Are you sure you want to stop Deceive? This will also stop related games if they are running."))
            return;

        TryKill();
        _controller?.SaveStatus();
        App.Shutdown();
    }

    private void TryKill()
    {
        try
        {
            _platform.KillProcesses();
        }
        catch (PlatformAccessDeniedException ex)
        {
            Trace.WriteLine(ex);
        }
    }

    // === Dialogs (rewrite of MessageBox.Show) ===

    public Task ShowInfoAsync(string message) => OnUi(() => ShowDialogAsync(message, "OK"));

    public Task ShowErrorAsync(string message) => OnUi(() => ShowDialogAsync(message, "OK"));

    public async Task<bool> ShowYesNoAsync(string message) =>
        await OnUi(() => ShowDialogAsync(message, "Yes", "No")) == 0;

    public async Task<bool> ShowRetryCancelAsync(string message) =>
        await OnUi(() => ShowDialogAsync(message, "Retry", "Cancel")) == 0;

    public async Task<bool> ShowOkCancelAsync(string message) =>
        await OnUi(() => ShowDialogAsync(message, "OK", "Cancel")) == 0;

    public Task<LaunchGame> ShowGamePromptAsync(LaunchGame initialGame, bool rememberDefault) =>
        OnUi(ShowGamePromptCoreAsync);

    public void OpenUrl(string url)
    {
        try
        {
            // macOS uses `open`; this keeps us off UseShellExecute (unsupported the same way on .NET/macOS).
            Process.Start(new ProcessStartInfo { FileName = "open", ArgumentList = { url }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to open URL {url}: {ex}");
        }
    }

    public void Notify(string message)
    {
        // The Windows tray balloon tip has no portable Avalonia equivalent; the in-chat introduction
        // messages cover the same ground. We surface it on the tooltip and the trace log.
        Trace.WriteLine($"[notify] {message}");
        Dispatcher.UIThread.Post(() => _trayIcon.ToolTipText = StartupHandler.DeceiveTitle + " — masking active");
    }

    // === Helpers ===

    private static Task<T> OnUi<T>(Func<Task<T>> f)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return f();

        var tcs = new TaskCompletionSource<T>();
        Dispatcher.UIThread.Post(async () =>
        {
            try { tcs.SetResult(await f()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    private static Task OnUi(Func<Task> f)
    {
        if (Dispatcher.UIThread.CheckAccess())
            return f();

        var tcs = new TaskCompletionSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await f();
                tcs.SetResult();
            }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    /// <summary>
    ///     Build and show a simple modal-style dialog with the given buttons. Returns the index of
    ///     the clicked button, or -1 if the window was closed without a choice. Must run on the UI
    ///     thread.
    /// </summary>
    private static Task<int> ShowDialogAsync(string message, params string[] buttons)
    {
        var tcs = new TaskCompletionSource<int>();

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };

        var window = new Window
        {
            Title = StartupHandler.DeceiveTitle,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
            Topmost = true
        };

        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var button = new Button { Content = buttons[i], MinWidth = 80 };
            button.Click += (_, _) =>
            {
                tcs.TrySetResult(index);
                window.Close();
            };
            buttonRow.Children.Add(button);
        }

        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                buttonRow
            }
        };

        window.Closed += (_, _) => tcs.TrySetResult(-1);
        window.Show();
        window.Activate();

        return tcs.Task;
    }

    private Task<LaunchGame> ShowGamePromptCoreAsync()
    {
        var tcs = new TaskCompletionSource<LaunchGame>();

        // Only offer games that have a native macOS client, plus the bare Riot Client.
        var choices = new List<(LaunchGame Game, string Label)>();
        void Offer(LaunchGame g, string label)
        {
            if (_platform.IsGameSupported(g))
                choices.Add((g, label));
        }

        Offer(LaunchGame.LoL, "League of Legends");
        Offer(LaunchGame.LoR, "Legends of Runeterra");
        Offer(LaunchGame.VALORANT, "VALORANT");
        Offer(LaunchGame.Lion, "2XKO");
        Offer(LaunchGame.RiotClient, "Just the Riot Client");

        var remember = new CheckBox { Content = "Remember my choice", Margin = new Thickness(0, 8, 0, 0) };

        var window = new Window
        {
            Title = StartupHandler.DeceiveTitle,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
            Topmost = true
        };

        var stack = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        stack.Children.Add(new TextBlock
        {
            Text = "Which game would you like to launch?",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeight.SemiBold
        });

        foreach (var (game, label) in choices)
        {
            var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch };
            var captured = game;
            button.Click += (_, _) =>
            {
                if (remember.IsChecked == true)
                    Persistence.SetDefaultLaunchGame(captured);
                tcs.TrySetResult(captured);
                window.Close();
            };
            stack.Children.Add(button);
        }

        stack.Children.Add(remember);

        var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (_, _) =>
        {
            tcs.TrySetResult(LaunchGame.Prompt);
            window.Close();
        };
        stack.Children.Add(cancel);

        window.Content = stack;
        // If the window is closed without a choice, treat it as a cancel.
        window.Closed += (_, _) => tcs.TrySetResult(LaunchGame.Prompt);
        window.Show();
        window.Activate();

        return tcs.Task;
    }
}
