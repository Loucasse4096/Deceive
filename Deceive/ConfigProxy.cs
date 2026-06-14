// Deceive macOS port — original project by molenzwiebel (github.com/molenzwiebel/Deceive), GPL-3.0.
using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Deceive;

/// <summary>
///     Local HTTP proxy for the Riot client configuration service. The Riot Client is launched with
///     <c>--client-config-url</c> pointing here; we relay each request to the real clientconfig
///     service and rewrite the chat host/port/affinities in the JSON response to point at our local
///     chat proxy.
///
///     macOS note: the original used the EmbedIO web server. We replaced it with the built-in
///     <see cref="HttpListener" />, which is fully cross-platform, has no third-party dependency, and
///     avoids EmbedIO's quirk of prepending stray bytes to responses. The old Windows-version &lt; 10
///     TLS workaround was dropped (irrelevant on macOS).
/// </summary>
internal class ConfigProxy
{
    private const string ConfigUrl = "https://clientconfig.rpg.riotgames.com";
    private const string GeoPasUrl = "https://riot-geo.pas.si.riotgames.com/pas/v1/service/chat";
    public const string LocalhostDomain = "deceive-localhost.molenzwiebel.xyz";

    private readonly IUserInterface _ui;
    private readonly Action _onFatalError;
    private readonly HttpListener _listener;

    /// <summary>
    ///     Starts a new client configuration proxy on a random local port. The proxy modifies any
    ///     responses to point the chat servers to our local setup.
    /// </summary>
    internal ConfigProxy(int chatPort, IUserInterface ui, Action onFatalError)
    {
        ChatPort = chatPort;
        _ui = ui;
        _onFatalError = onFatalError;

        // Find a free port.
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        ConfigPort = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{ConfigPort}/");
        _listener.Start();
        Trace.WriteLine($"Config proxy listening on http://127.0.0.1:{ConfigPort}");

        Task.Run(AcceptLoopAsync);
    }

    private HttpClient Client { get; } = new();
    internal int ConfigPort { get; }
    private int ChatPort { get; }

    internal event EventHandler<ChatServerEventArgs>? PatchedChatServer;

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception e)
            {
                Trace.WriteLine(e);
                break;
            }

            // Handle each request concurrently so a slow upstream doesn't block others.
            _ = Task.Run(() => ProxyAndRewriteResponseAsync(ctx));
        }
    }

    /// <summary>
    ///     Proxies a request to the clientconfig service and rewrites the response so that any chat
    ///     servers point to localhost at the configured chat-proxy port.
    /// </summary>
    private async Task ProxyAndRewriteResponseAsync(HttpListenerContext ctx)
    {
        var url = ConfigUrl + ctx.Request.RawUrl;
        Trace.WriteLine("Received client proxy request to URL: " + url);

        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        // Cloudflare bitches at us without a user agent.
        message.Headers.TryAddWithoutValidation("User-Agent", ctx.Request.Headers["user-agent"]);

        // Add authorization headers for player config.
        if (ctx.Request.Headers["x-riot-entitlements-jwt"] is not null)
            message.Headers.TryAddWithoutValidation("X-Riot-Entitlements-JWT", ctx.Request.Headers["x-riot-entitlements-jwt"]);

        if (ctx.Request.Headers["authorization"] is not null)
            message.Headers.TryAddWithoutValidation("Authorization", ctx.Request.Headers["authorization"]);

        var result = await Client.SendAsync(message);
        Trace.WriteLine("Received response from clientconfig service with status code: " + result.StatusCode);
        var content = await result.Content.ReadAsStringAsync();
        var modifiedContent = content;
        Trace.WriteLine("ORIGINAL CLIENTCONFIG: " + content);

        // sometimes riot yields an internal error with content that is definitely
        // not json. we can just forward it to the riot client, which will retry
        // the request until it succeeds
        if (result.IsSuccessStatusCode)
        {
            try
            {
                var configObject = JsonSerializer.Deserialize<JsonNode>(content);

                string? riotChatHost = null;
                var riotChatPort = 0;

                // Set fallback host to localhost.
                if (configObject?["chat.host"] is not null)
                {
                    // Save fallback host
                    riotChatHost = configObject["chat.host"]!.GetValue<string>();
                    configObject["chat.host"] = LocalhostDomain;
                }

                // Set chat port.
                if (configObject?["chat.port"] is not null)
                {
                    riotChatPort = configObject["chat.port"]!.GetValue<int>();
                    configObject["chat.port"] = ChatPort;
                }

                // Set chat.affinities (a dictionary) to all localhost.
                if (configObject?["chat.affinities"] is not null)
                {
                    var affinities = configObject["chat.affinities"];
                    if (configObject["chat.affinity.enabled"]?.GetValue<bool>() ?? false)
                    {
                        var pasRequest = new HttpRequestMessage(HttpMethod.Get, GeoPasUrl);
                        pasRequest.Headers.TryAddWithoutValidation("Authorization", ctx.Request.Headers["authorization"]);

                        try
                        {
                            var pasJwt = await (await Client.SendAsync(pasRequest)).Content.ReadAsStringAsync();
                            var pasJwtContent = pasJwt.Split('.')[1];
                            var validBase64 = pasJwtContent.PadRight((pasJwtContent.Length / 4 * 4) + (pasJwtContent.Length % 4 == 0 ? 0 : 4), '=');
                            var pasJwtString = Encoding.UTF8.GetString(Convert.FromBase64String(validBase64));
                            var pasJwtJson = JsonSerializer.Deserialize<JsonNode>(pasJwtString);
                            var affinity = pasJwtJson?["affinity"]?.GetValue<string>();

                            // replace fallback host with host by player affinity
                            if (affinity is not null)
                            {
                                riotChatHost = affinities?[affinity]?.GetValue<string>();
                                Trace.WriteLine($"AFFINITY: {affinity} -> {riotChatHost}");
                            }
                        }
                        catch (Exception e)
                        {
                            Trace.WriteLine("Error getting player affinity token, using default chat server.");
                            Trace.WriteLine(e);
                        }
                    }

                    affinities?.AsObject().Select(pair => pair.Key).ToList().ForEach(s => affinities[s] = LocalhostDomain);
                }

                modifiedContent = JsonSerializer.Serialize(configObject);
                Trace.WriteLine("MODIFIED CLIENTCONFIG: " + modifiedContent);

                if (riotChatHost is not null && riotChatPort != 0)
                    PatchedChatServer?.Invoke(this, new ChatServerEventArgs { ChatHost = riotChatHost, ChatPort = riotChatPort });
            }
            catch (Exception ex)
            {
                Trace.WriteLine(ex);

                // Show a message instead of failing silently. This is THE alert that fires when Riot
                // changes its config format — see the maintenance notes in the README.
                await _ui.ShowErrorAsync(
                    "Deceive was unable to rewrite a League of Legends configuration file. This normally happens because Riot changed something on their end. " +
                    "Please check if there's a new version of Deceive available, or contact the creator through GitHub (https://github.com/molenzwiebel/Deceive) or Discord if there's not.\n\n" +
                    ex);

                _onFatalError();
                return;
            }
        }

        var responseBytes = Encoding.UTF8.GetBytes(modifiedContent);
        ctx.Response.StatusCode = (int)result.StatusCode;
        ctx.Response.SendChunked = false;
        ctx.Response.ContentLength64 = responseBytes.Length;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length);
        ctx.Response.OutputStream.Close();
    }

    internal class ChatServerEventArgs : EventArgs
    {
        internal string? ChatHost { get; set; }
        internal int ChatPort { get; set; }
    }
}
