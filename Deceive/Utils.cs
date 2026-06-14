// Deceive macOS port — original project by molenzwiebel (github.com/molenzwiebel/Deceive), GPL-3.0.
using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Deceive.Platform;

namespace Deceive;

/// <summary>
///     Portable helpers. Everything here uses cross-platform .NET APIs (HttpClient, Dns,
///     X509Certificate2). OS-specific behaviour is delegated to <see cref="IPlatform" /> and all
///     user interaction goes through <see cref="IUserInterface" />.
/// </summary>
internal static class Utils
{
    internal static string DeceiveVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version;
            if (version is null)
                return "v0.0.0";
            return "v" + version.Major + "." + version.Minor + "." + version.Build;
        }
    }

    /// <summary>
    ///     Asynchronously checks if the current version of Deceive is the latest version.
    ///     If not, and the user has not dismissed the message before, an alert is shown.
    /// </summary>
    public static async Task CheckForUpdatesAsync(IUserInterface ui)
    {
        try
        {
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Deceive", DeceiveVersion));

            var response =
                await httpClient.GetAsync("https://api.github.com/repos/molenzwiebel/Deceive/releases/latest");
            var content = await response.Content.ReadAsStringAsync();
            var release = JsonSerializer.Deserialize<JsonNode>(content);
            var latestVersion = release?["tag_name"]?.ToString();

            // If failed to fetch or already latest or newer, return.
            if (latestVersion is null)
                return;
            var githubVersion = new Version(latestVersion.Replace("v", ""));
            var assemblyVersion = new Version(DeceiveVersion.Replace("v", ""));
            // Earlier = -1, Same = 0, Later = 1
            if (assemblyVersion.CompareTo(githubVersion) != -1)
                return;

            // Check if we have shown this before.
            var latestShownVersion = Persistence.GetPromptedUpdateVersion();

            // If we have, return.
            if (!string.IsNullOrEmpty(latestShownVersion) && latestShownVersion == latestVersion)
                return;

            // Show a message and record the latest shown.
            Persistence.SetPromptedUpdateVersion(latestVersion);

            var openDownload = await ui.ShowOkCancelAsync(
                $"There is a new version of Deceive available: {latestVersion}. You are currently using Deceive {DeceiveVersion}. " +
                "Deceive updates usually fix critical bugs or adapt to changes by Riot, so it is recommended that you install the latest version.\n\n" +
                "Press OK to visit the download page, or press Cancel to continue. Don't worry, we won't bother you with this message again if you press cancel.");

            if (openDownload)
                ui.OpenUrl(release?["html_url"]?.ToString() ?? "https://github.com/molenzwiebel/Deceive/releases/latest");
        }
        catch
        {
            // Ignored.
        }
    }

    /// <summary>
    ///     Returns a certificate for deceive-localhost.molenzwiebel.xyz, either from cache or by
    ///     downloading the current one from the server. The returned certificate is valid for at
    ///     least 20 days.
    ///
    ///     This mechanism is unchanged from the Windows version: Deceive downloads a pre-signed PFX
    ///     whose chain the Riot Client already trusts, which works identically on macOS. (Generating
    ///     a self-signed cert on the fly via CertificateRequest is possible but would require the
    ///     Riot Client to trust it, so we keep the original approach.)
    /// </summary>
    public static async Task<X509Certificate2?> GetProxyCertificateAsync()
    {
        var cachedCert = Persistence.GetCachedCertificate();
        if (cachedCert is not null && cachedCert.NotAfter > DateTime.Now.AddDays(20))
        {
            Trace.WriteLine($"Cached certificate is valid until {cachedCert.NotAfter}, using cached certificate.");
            return cachedCert;
        }

        try
        {
            Trace.WriteLine("Cached certificate is missing or expiring soon, downloading new certificate.");
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Deceive", DeceiveVersion));

            var response = await httpClient.GetAsync("https://mln.cx/deceive/localhost.pfx");
            response.EnsureSuccessStatusCode();
            var certBytes = await response.Content.ReadAsByteArrayAsync();
            var cert = new X509Certificate2(certBytes);
            Persistence.SetCachedCertificate(certBytes);
            return cert;
        }
        catch (Exception ex)
        {
            // something went wrong, let's just return null and inform the user
            Trace.WriteLine($"Failed to download certificate: {ex}");
            return null;
        }
    }

    private static bool DeceiveLocalhostResolves()
    {
        try
        {
            var addresses = System.Net.Dns.GetHostAddresses(ConfigProxy.LocalhostDomain);
            if (addresses.Any(addr => addr.ToString() == "127.0.0.1"))
                return true;
        }
        catch
        {
            // intentionally empty
        }
        return false;
    }

    /// <summary>
    ///     Checks if deceive-localhost.molenzwiebel.xyz resolves to 127.0.0.1. If not, offers to add
    ///     the entry to /etc/hosts (with macOS privilege elevation) or to open the FAQ. Returns
    ///     <c>true</c> if resolution works (or was fixed), <c>false</c> if Deceive should abort.
    /// </summary>
    public static async Task<bool> EnsureLocalhostResolutionAsync(IPlatform platform, IUserInterface ui)
    {
        if (DeceiveLocalhostResolves())
            return true;

        // On macOS we can offer to fix this automatically by editing /etc/hosts (requires the admin
        // password prompt). This replaces the Windows version which only pointed users at the FAQ.
        var fixNow = await ui.ShowYesNoAsync(
            "Your machine is failing to resolve a required domain (" + ConfigProxy.LocalhostDomain + " must point to 127.0.0.1). " +
            "Deceive can add this entry to your /etc/hosts file for you — this requires your administrator password. " +
            "\n\nWould you like Deceive to add the entry now? (Choose No to instead see the FAQ, which also explains the alternative of switching your DNS to 1.1.1.1 or 8.8.8.8.)");

        if (fixNow)
        {
            var added = await platform.TryAddHostsEntryAsync(ConfigProxy.LocalhostDomain);
            if (added && DeceiveLocalhostResolves())
                return true;

            await ui.ShowErrorAsync(
                "Deceive was unable to update your hosts file (the change was cancelled or did not take effect). " +
                "Please add the entry manually, or switch your DNS to 1.1.1.1 / 8.8.8.8. See the FAQ for details.");
            ui.OpenUrl("https://github.com/molenzwiebel/Deceive#FAQ");
            return false;
        }

        ui.OpenUrl("https://github.com/molenzwiebel/Deceive#FAQ");
        return false;
    }
}
