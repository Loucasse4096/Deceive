// Deceive macOS port — original project by molenzwiebel (github.com/molenzwiebel/Deceive), GPL-3.0.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Deceive;

/// <summary>
///     The Deceive engine. Holds the masking state (enabled / status / lobby-chat) and proxies chat
///     connections. This class is intentionally free of any UI-framework dependency: it talks to the
///     user only through <see cref="IUserInterface" /> and notifies the UI of state changes via
///     <see cref="StateChanged" /> so the menu-bar layer can refresh its check marks.
///
///     This is the half of the original <c>MainController</c> that was already fully portable; the
///     Windows Forms tray/menu half was rewritten in <see cref="AvaloniaUserInterface" />.
/// </summary>
internal class MainController
{
    private readonly IUserInterface _ui;

    internal MainController(IUserInterface ui)
    {
        _ui = ui;
        LoadStatus();
        _ui.Notify("Deceive is currently masking your status. Use the menu-bar icon for more options.");
    }

    /// <summary>Raised whenever Enabled/Status/ConnectToMuc change, so the UI can refresh.</summary>
    public event EventHandler? StateChanged;

    public bool Enabled { get; private set; } = true;
    public string Status { get; private set; } = null!;
    public bool ConnectToMuc { get; private set; } = true;

    private string StatusFile { get; } = Path.Combine(Persistence.DataDir, "status");
    private bool SentIntroductionText { get; set; }
    private CancellationTokenSource? ShutdownToken { get; set; }

    private List<ProxiedConnection> Connections { get; } = new();

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public void StartServingClients(TcpListener server, X509Certificate2 serverCert, string chatHost, int chatPort)
    {
        Task.Run(() => ServeClientsAsync(server, serverCert, chatHost, chatPort));
    }

    private async Task ServeClientsAsync(TcpListener server, X509Certificate2 serverCert, string chatHost, int chatPort)
    {
        while (true)
        {
            try
            {
                // no need to shutdown, we received a new request
                ShutdownToken?.Cancel();
                ShutdownToken = null;

                var incoming = await server.AcceptTcpClientAsync();
                var sslIncoming = new SslStream(incoming.GetStream());
                await sslIncoming.AuthenticateAsServerAsync(serverCert);

                TcpClient outgoing;
                while (true)
                {
                    try
                    {
                        outgoing = new TcpClient(chatHost, chatPort);
                        break;
                    }
                    catch (SocketException e)
                    {
                        Trace.WriteLine(e);
                        var retry = await _ui.ShowRetryCancelAsync(
                            "Unable to connect to the chat server. Please check your internet connection. " +
                            "If this issue persists and you can connect to chat normally without Deceive, " +
                            "please file a bug report through GitHub (https://github.com/molenzwiebel/Deceive) or Discord.");
                        if (!retry)
                            Environment.Exit(0);
                    }
                }

                var sslOutgoing = new SslStream(outgoing.GetStream());
                await sslOutgoing.AuthenticateAsClientAsync(chatHost);

                var proxiedConnection = new ProxiedConnection(this, sslIncoming, sslOutgoing);
                proxiedConnection.Start();
                proxiedConnection.ConnectionErrored += (_, _) =>
                {
                    Trace.WriteLine("Disconnected incoming connection.");
                    Connections.Remove(proxiedConnection);

                    if (Connections.Count == 0)
                        Task.Run(ShutdownIfNoReconnect);
                };
                Connections.Add(proxiedConnection);

                if (!SentIntroductionText)
                {
                    SentIntroductionText = true;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(10_000);
                        await SendIntroductionTextAsync();
                    });
                }
            }
            catch (Exception e)
            {
                Trace.WriteLine("Failed to handle incoming connection.");
                Trace.WriteLine(e);
            }
        }
    }

    // === Public state-mutating API used by the menu-bar UI and by chat commands ===

    public async Task ToggleEnabledAsync()
    {
        Enabled = !Enabled;
        await UpdateStatusAsync(Enabled ? Status : "chat");
        await SendMessageFromFakePlayerAsync(Enabled ? "Deceive is now enabled." : "Deceive is now disabled.");
        RaiseStateChanged();
    }

    public async Task SetStatusAsync(string newStatus)
    {
        Status = newStatus;
        await UpdateStatusAsync(newStatus);
        Enabled = true;
        RaiseStateChanged();
    }

    public void SetConnectToMuc(bool value)
    {
        ConnectToMuc = value;
        RaiseStateChanged();
    }

    public async Task HandleChatMessage(string content)
    {
        var lower = content.ToLower();
        if (lower.Contains("offline"))
        {
            if (!Enabled)
                await SendMessageFromFakePlayerAsync("Deceive is now enabled.");
            await SetStatusAsync("offline");
        }
        else if (lower.Contains("mobile"))
        {
            if (!Enabled)
                await SendMessageFromFakePlayerAsync("Deceive is now enabled.");
            await SetStatusAsync("mobile");
        }
        else if (lower.Contains("online"))
        {
            if (!Enabled)
                await SendMessageFromFakePlayerAsync("Deceive is now enabled.");
            await SetStatusAsync("chat");
        }
        else if (lower.Contains("enable"))
        {
            if (Enabled)
                await SendMessageFromFakePlayerAsync("Deceive is already enabled.");
            else
                await ToggleEnabledAsync();
        }
        else if (lower.Contains("disable"))
        {
            if (!Enabled)
                await SendMessageFromFakePlayerAsync("Deceive is already disabled.");
            else
                await ToggleEnabledAsync();
        }
        else if (lower.Contains("status"))
        {
            if (Status == "chat")
                await SendMessageFromFakePlayerAsync("You are appearing online.");
            else
                await SendMessageFromFakePlayerAsync("You are appearing " + Status + ".");
        }
        else if (lower.Contains("help"))
        {
            await SendMessageFromFakePlayerAsync("You can send the following messages to quickly change Deceive settings: online/offline/mobile/enable/disable/status");
        }
    }

    private async Task SendIntroductionTextAsync()
    {
        SentIntroductionText = true;
        await SendMessageFromFakePlayerAsync("Welcome! Deceive is running and you are currently appearing " + Status +
                                             ". Despite what the game client may indicate, you are appearing offline to your friends unless you manually disable Deceive.");
        await Task.Delay(200);
        await SendMessageFromFakePlayerAsync(
            "If you want to invite others while being offline, you may need to disable Deceive for them to accept. You can enable Deceive again as soon as they are in your lobby.");
        await Task.Delay(200);
        await SendMessageFromFakePlayerAsync("To enable or disable Deceive, or to configure other settings, find Deceive in your menu-bar icons.");
        await Task.Delay(200);
        await SendMessageFromFakePlayerAsync("Have fun!");
    }

    private async Task SendMessageFromFakePlayerAsync(string message)
    {
        foreach (var connection in Connections)
            await connection.SendMessageFromFakePlayerAsync(message);
    }

    private async Task UpdateStatusAsync(string newStatus)
    {
        foreach (var connection in Connections)
            await connection.UpdateStatusAsync(newStatus);

        if (newStatus == "chat")
            await SendMessageFromFakePlayerAsync("You are now appearing online.");
        else
            await SendMessageFromFakePlayerAsync("You are now appearing " + newStatus + ".");
    }

    private void LoadStatus()
    {
        var startupStatus = Persistence.GetStartupStatus();

        if (startupStatus is "chat" or "offline" or "mobile")
        {
            Status = startupStatus;
            return;
        }

        if (!File.Exists(StatusFile))
        {
            Status = "offline";
            return;
        }

        // "last" or unrecognized: use the saved session status.
        var saved = File.ReadAllText(StatusFile);
        Status = saved switch
        {
            "chat" => "chat",
            "mobile" => "mobile",
            _ => "offline"
        };
    }

    private async Task ShutdownIfNoReconnect()
    {
        ShutdownToken ??= new CancellationTokenSource();
        try
        {
            await Task.Delay(60_000, ShutdownToken.Token);
        }
        catch (TaskCanceledException)
        {
            // A new connection arrived; don't shut down.
            return;
        }

        Trace.WriteLine("Received no new connections after 60s, shutting down.");
        Environment.Exit(0);
    }

    public void SaveStatus() => File.WriteAllText(StatusFile, Status);
}
