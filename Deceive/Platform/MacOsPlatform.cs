// Deceive macOS port — original project by molenzwiebel (github.com/molenzwiebel/Deceive), GPL-3.0.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Deceive.Platform;

/// <summary>
///     macOS implementation of <see cref="IPlatform" />.
///
///     Differences from the original Windows code, point by point:
///     <list type="bullet">
///         <item>The Riot Client lives in an <c>.app</c> bundle under <c>/Applications</c>; we launch
///               the inner Mach-O binary directly so that <c>--client-config-url</c> is honoured
///               (the macOS <c>open</c> command swallows custom arguments).</item>
///         <item><c>RiotClientInstalls.json</c> is not under <c>%ProgramData%</c>; on macOS it has been
///               observed under <c>~/Library/Application Support/Riot Games/</c> and
///               <c>/Users/Shared/Riot Games/</c>. We probe several candidates and fall back to the
///               canonical bundle path.</item>
///         <item>VALORANT (<c>VALORANT-Win64-Shipping</c>) and 2XKO have no native macOS client, so
///               their Windows process names are irrelevant here.</item>
///         <item>Editing <c>/etc/hosts</c> needs root, obtained through <c>osascript ... with
///               administrator privileges</c> (the native macOS password dialog).</item>
///     </list>
/// </summary>
internal sealed class MacOsPlatform : IPlatform
{
    // Canonical bundle locations on macOS.
    private const string RiotClientApp = "/Applications/Riot Client.app";
    private const string RiotClientBinary = RiotClientApp + "/Contents/MacOS/RiotClientServices";

    // Process names as reported by macOS for the relevant Riot processes. Note these differ from
    // the Windows names: there is no VALORANT-Win64-Shipping process on macOS, and League runs two
    // helper processes (LeagueClient + LeagueClientUx).
    public IReadOnlyList<string> RiotProcessNames { get; } = new[]
    {
        "RiotClientServices",
        "Riot Client",
        "LeagueClient",
        "LeagueClientUx",
        "LeagueClientUxRender",
        "LoR"
    };

    public string RiotClientProcessName => "RiotClientServices";

    public string? GetRiotClientPath()
    {
        // 1) Try the RiotClientInstalls.json metadata files, mirroring the Windows logic but at the
        //    macOS-specific locations. The schema (rc_default/rc_live/rc_beta) is the same.
        foreach (var installPath in InstallJsonCandidates())
        {
            if (!File.Exists(installPath))
                continue;

            try
            {
                var data = JsonSerializer.Deserialize<JsonNode>(File.ReadAllText(installPath));
                var rcPaths = new List<string?>
                {
                    data?["rc_default"]?.ToString(),
                    data?["rc_live"]?.ToString(),
                    data?["rc_beta"]?.ToString()
                };

                var found = rcPaths.FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
                if (found is not null)
                    return found;
            }
            catch
            {
                // The RC occasionally corrupts this file; ignore and try the next candidate.
            }
        }

        // 2) Fall back to the canonical bundle path. On macOS this is by far the common case, since
        //    the install metadata is less reliably present than on Windows.
        if (File.Exists(RiotClientBinary))
            return RiotClientBinary;

        return null;
    }

    private static IEnumerable<string> InstallJsonCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // CommonApplicationData maps to /usr/share on macOS, which Riot does not use, so we probe
        // the real macOS locations explicitly.
        yield return Path.Combine(home, "Library/Application Support/Riot Games/RiotClientInstalls.json");
        yield return "/Users/Shared/Riot Games/RiotClientInstalls.json";
        yield return "/Users/Shared/Riot Games/Metadata/RiotClientInstalls.json";
    }

    public bool IsClientRunning() => GetProcesses().Any();

    public bool IsRiotClientRunning() =>
        Process.GetProcessesByName(RiotClientProcessName).Any();

    private IEnumerable<Process> GetProcesses()
    {
        var current = Process.GetCurrentProcess();
        var candidates = new List<Process>();

        // Other Deceive instances (same process name as us).
        candidates.AddRange(Process.GetProcessesByName(current.ProcessName)
            .Where(p => p.Id != current.Id));

        foreach (var name in RiotProcessNames)
            candidates.AddRange(Process.GetProcessesByName(name));

        // De-duplicate by PID (a process can match multiple queries).
        return candidates.GroupBy(p => p.Id).Select(g => g.First());
    }

    public void KillProcesses()
    {
        foreach (var process in GetProcesses())
        {
            try
            {
                process.Refresh();
                if (process.HasExited)
                    continue;
                process.Kill();
                process.WaitForExit();
            }
            catch (Win32Exception ex)
            {
                // EPERM (errno 1) / EACCES (errno 13): we lack permission to signal the process.
                if (ex.NativeErrorCode is 1 or 13)
                    throw new PlatformAccessDeniedException(
                        "Deceive could not stop existing Riot processes because it does not have the right permissions.");
                throw;
            }
            catch (InvalidOperationException)
            {
                // Process already exited between the enumeration and the kill; ignore.
            }
        }
    }

    public ProcessStartInfo BuildRiotClientStartInfo(string riotClientPath, string arguments)
    {
        // Launch the bundle's inner binary directly. UseShellExecute=false keeps it a child process
        // (so we can listen for its exit) and ensures our custom arguments are passed verbatim.
        return new ProcessStartInfo
        {
            FileName = riotClientPath,
            Arguments = arguments,
            UseShellExecute = false
        };
    }

    public bool IsGameSupported(LaunchGame game) => game switch
    {
        // League of Legends, Legends of Runeterra and the bare Riot Client run on macOS.
        LaunchGame.LoL => true,
        LaunchGame.LoR => true,
        LaunchGame.RiotClient => true,
        // VALORANT and 2XKO (Lion) have no native macOS client.
        LaunchGame.VALORANT => false,
        LaunchGame.Lion => false,
        _ => false
    };

    public async Task<bool> TryAddHostsEntryAsync(string domain)
    {
        // /etc/hosts is root-owned. Use AppleScript's "with administrator privileges" so macOS shows
        // its native authentication dialog rather than us trying to manage sudo ourselves.
        // We guard against duplicate entries with `grep -q` before appending.
        var shell =
            $"/usr/bin/grep -q '{domain}' /etc/hosts || /bin/echo '127.0.0.1 {domain}' >> /etc/hosts";
        var appleScript = $"do shell script \"{shell.Replace("\"", "\\\"")}\" with administrator privileges";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                ArgumentList = { "-e", appleScript },
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            var process = Process.Start(psi);
            if (process is null)
                return false;

            await process.WaitForExitAsync();
            // osascript exits 0 on success; non-zero if the user cancelled the auth dialog (-128) etc.
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to elevate to edit hosts file: {ex}");
            return false;
        }
    }
}
