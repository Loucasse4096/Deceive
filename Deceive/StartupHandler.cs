// Deceive macOS port — original project by molenzwiebel (github.com/molenzwiebel/Deceive), GPL-3.0.
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Deceive.Platform;

namespace Deceive;

internal static class StartupHandler
{
    public static string DeceiveTitle => "Deceive " + Utils.DeceiveVersion;

    /// <summary>Parsed command-line arguments, captured before the Avalonia app loop starts.</summary>
    internal static LaunchArguments PendingArgs { get; private set; } = new(LaunchGame.Auto, "live", null, null);

    // Arguments are parsed through System.CommandLine.DragonFruit (unchanged from the original):
    // e.g. `Deceive lol`, `Deceive valorant --game-patchline pbe`.
    /// <param name="args">The game to be launched, or automatically determined if not passed.</param>
    /// <param name="gamePatchline">The patchline to be used for launching the game.</param>
    /// <param name="riotClientParams">Any extra parameters to be passed to the Riot Client.</param>
    /// <param name="gameParams">Any extra parameters to be passed to the launched game.</param>
    public static int Main(LaunchGame args = LaunchGame.Auto, string gamePatchline = "live", string? riotClientParams = null, string? gameParams = null)
    {
        PendingArgs = new LaunchArguments(args, gamePatchline, riotClientParams, gameParams);

        AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;
        Trace.Listeners.Add(new ConsoleTraceListener());

        Trace.WriteLine($"{DeceiveTitle} starting with arguments:\n" +
                        $"LaunchGame: {args}\n" +
                        $"GamePatchline: {gamePatchline}\n" +
                        $"RiotClientParams: {riotClientParams}\n" +
                        $"GameParams: {gameParams}");

        // Avalonia must own the main thread on macOS; the actual Deceive startup logic runs from
        // App.OnFrameworkInitializationCompleted once the UI loop is up.
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>(), ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    ///     The real startup sequence (the old <c>StartDeceiveAsync</c>), driven from the UI thread
    ///     once Avalonia is ready. It wires the config proxy, certificate, Riot Client launch, and
    ///     the chat-proxy engine together.
    /// </summary>
    internal static async Task StartDeceiveAsync(AvaloniaUserInterface ui, IPlatform platform)
    {
        var (game, gamePatchline, riotClientParams, gameParams) = PendingArgs;

        try
        {
            // Refuse to do anything if the client is already running, unless we're specifically
            // allowing that through League/RC's --allow-multiple-clients.
            if (platform.IsClientRunning() && !(riotClientParams?.Contains("allow-multiple-clients") ?? false))
            {
                var kill = await ui.ShowYesNoAsync(
                    "The Riot Client is currently running. In order to mask your online status, the Riot Client needs to be started by Deceive. " +
                    "Do you want Deceive to stop the Riot Client and games launched by it, so that it can restart with the proper configuration?");

                if (!kill)
                {
                    App.Shutdown();
                    return;
                }

                try
                {
                    platform.KillProcesses();
                }
                catch (PlatformAccessDeniedException ex)
                {
                    await ui.ShowErrorAsync(ex.Message + " Please try again.");
                    App.Shutdown();
                    return;
                }

                await Task.Delay(2000); // Riot Client takes a while to die
            }

            try
            {
                File.WriteAllText(Path.Combine(Persistence.DataDir, "debug.log"), string.Empty);
                Trace.Listeners.Add(new TextWriterTraceListener(Path.Combine(Persistence.DataDir, "debug.log")));
                Debug.AutoFlush = true;
                Trace.WriteLine(DeceiveTitle);
            }
            catch
            {
                // ignored; just don't save logs if file is already being accessed
            }

            // if we can't resolve deceive-localhost.molenzwiebel.xyz to 127.0.0.1, offer to fix it.
            if (!await Utils.EnsureLocalhostResolutionAsync(platform, ui))
            {
                App.Shutdown();
                return;
            }

            // Step 0: Check for updates in the background.
            _ = Utils.CheckForUpdatesAsync(ui);

            // Step 1: Open a port for our chat proxy, so we can patch the chat port into clientconfig.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Trace.WriteLine($"Chat proxy listening on port {port}");

            // Step 2: Find the Riot Client.
            var riotClientPath = platform.GetRiotClientPath();
            if (riotClientPath is null)
            {
                await ui.ShowErrorAsync(
                    "Deceive was unable to find the path to the Riot Client. Usually this can be resolved by launching any Riot Games game once, then launching Deceive again. " +
                    "If this does not resolve the issue, please file a bug report through GitHub (https://github.com/molenzwiebel/Deceive) or Discord.");
                App.Shutdown();
                return;
            }

            // If launching "auto", use the persisted launch game (which defaults to prompt).
            if (game is LaunchGame.Auto)
                game = Persistence.GetDefaultLaunchGame();

            // If prompt, display dialog (filtered to games that exist on macOS).
            if (game is LaunchGame.Prompt)
                game = await ui.ShowGamePromptAsync(LaunchGame.Prompt, rememberDefault: false);

            // If we don't have a concrete game by now, the user has cancelled and nothing we can do.
            if (game is LaunchGame.Prompt or LaunchGame.Auto)
            {
                App.Shutdown();
                return;
            }

            // Guard: the requested game may not have a native macOS client.
            if (!platform.IsGameSupported(game))
            {
                await ui.ShowErrorAsync(
                    $"{game} does not have a native macOS client, so Deceive cannot launch it on this Mac. " +
                    "You can still use Deceive with League of Legends or Legends of Runeterra, or launch just the Riot Client.");
                App.Shutdown();
                return;
            }

            var launchProduct = game switch
            {
                LaunchGame.LoL => "league_of_legends",
                LaunchGame.LoR => "bacon",
                LaunchGame.VALORANT => "valorant",
                LaunchGame.Lion => "lion",
                LaunchGame.RiotClient => null,
                var x => throw new Exception("Unexpected LaunchGame: " + x)
            };

            // Step 3: Start proxy web server for clientconfig.
            var proxyServer = new ConfigProxy(port, ui, App.Shutdown);

            // Step 4: fetch certificate for MITM.
            var serverCertificate = await Utils.GetProxyCertificateAsync();
            if (serverCertificate is null)
            {
                await ui.ShowErrorAsync(
                    "Deceive was unable to obtain a necessary security certificate for intercepting and modifying the chat connection. This normally happens when there's " +
                    "a problem with the server that provides the certificate, but it can also be caused by network issues on your end. Please check if there's a new version" +
                    " of Deceive available, check your network connection, or contact the creator through GitHub (https://github.com/molenzwiebel/Deceive) or Discord.");
                App.Shutdown();
                return;
            }

            // Step 5: Launch Riot Client (+game).
            var arguments = $"--client-config-url=\"http://127.0.0.1:{proxyServer.ConfigPort}\"";
            if (launchProduct is not null)
                arguments += $" --launch-product={launchProduct} --launch-patchline={gamePatchline}";
            if (riotClientParams is not null)
                arguments += $" {riotClientParams}";
            if (gameParams is not null)
                arguments += $" -- {gameParams}";

            var startArgs = platform.BuildRiotClientStartInfo(riotClientPath, arguments);
            Trace.WriteLine($"About to launch Riot Client with parameters:\n{startArgs.Arguments}");
            var riotClient = Process.Start(startArgs);
            // Kill Deceive when Riot Client has exited, so no ghost Deceive exists.
            if (riotClient is not null)
                ListenToRiotClientExit(riotClient, platform);

            var mainController = new MainController(ui);
            ui.AttachController(mainController);

            // Step 6: Get chat server and port for this player by listening to event from ConfigProxy.
            var servingClients = false;
            proxyServer.PatchedChatServer += (_, eventArgs) =>
            {
                Trace.WriteLine($"The original chat server details were {eventArgs.ChatHost}:{eventArgs.ChatPort}");

                // Step 7: Start serving incoming connections and proxy them!
                if (servingClients)
                    return;
                servingClients = true;
                if (eventArgs.ChatHost is not null)
                    mainController.StartServingClients(listener, serverCertificate, eventArgs.ChatHost, eventArgs.ChatPort);
            };
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            await ui.ShowErrorAsync(
                "Deceive encountered an error and couldn't properly initialize itself. " +
                "Please contact the creator through GitHub (https://github.com/molenzwiebel/Deceive) or Discord.\n\n" + ex);
            App.Shutdown();
        }
    }

    private static void CurrentDomainOnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Trace.WriteLine(e.ExceptionObject as Exception);
        Trace.WriteLine(Environment.StackTrace);
    }

    private static void ListenToRiotClientExit(Process riotClientProcess, IPlatform platform)
    {
        riotClientProcess.EnableRaisingEvents = true;
        riotClientProcess.Exited += async (_, _) =>
        {
            Trace.WriteLine("Detected Riot Client exit.");
            await Task.Delay(3000); // wait for a bit to ensure this is not a relaunch triggered by the RC

            if (platform.IsRiotClientRunning())
            {
                Trace.WriteLine("A new Riot Client process is running, monitoring that for exits.");
                var newProcess = Process.GetProcessesByName(platform.RiotClientProcessName);
                if (newProcess.Length > 0)
                    ListenToRiotClientExit(newProcess[0], platform);
            }
            else
            {
                Trace.WriteLine("No new clients spawned after waiting, killing ourselves.");
                Environment.Exit(0);
            }
        };
    }
}

/// <summary>Captured CLI arguments.</summary>
internal sealed record LaunchArguments(LaunchGame Game, string GamePatchline, string? RiotClientParams, string? GameParams);
