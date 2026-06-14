// Deceive macOS port — original project by molenzwiebel (github.com/molenzwiebel/Deceive), GPL-3.0.
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Deceive.Platform;

/// <summary>
///     OS-specific glue: locating the Riot Client, detecting/killing running Riot processes,
///     building the launch <see cref="ProcessStartInfo" />, and editing the hosts file.
///
///     Everything else in Deceive (the config proxy, the XMPP chat proxy, certificate handling,
///     persistence) is pure cross-platform .NET and lives outside this interface.
/// </summary>
internal interface IPlatform
{
    /// <summary>Path to the Riot Client executable, or <c>null</c> if it could not be located.</summary>
    string? GetRiotClientPath();

    /// <summary>Process names that indicate a Riot client/game is already running.</summary>
    IReadOnlyList<string> RiotProcessNames { get; }

    /// <summary>The single process name corresponding to the Riot Client itself.</summary>
    string RiotClientProcessName { get; }

    /// <summary>True if any Riot client/game (or a previous Deceive) is currently running.</summary>
    bool IsClientRunning();

    /// <summary>True if the Riot Client process specifically is running.</summary>
    bool IsRiotClientRunning();

    /// <summary>Kill all running Riot clients/games. Throws <see cref="PlatformAccessDeniedException" /> on EPERM.</summary>
    void KillProcesses();

    /// <summary>
    ///     Build the <see cref="ProcessStartInfo" /> that launches the Riot Client with the given
    ///     arguments. On macOS this points at the binary inside the <c>.app</c> bundle so that
    ///     command-line arguments (notably <c>--client-config-url</c>) are honoured.
    /// </summary>
    ProcessStartInfo BuildRiotClientStartInfo(string riotClientPath, string arguments);

    /// <summary>Whether the given game has a native client on this platform.</summary>
    bool IsGameSupported(LaunchGame game);

    /// <summary>
    ///     Attempt to add <c>127.0.0.1 {domain}</c> to the system hosts file, elevating privileges
    ///     as required by the platform. Returns true on success. Returns false (without throwing) if
    ///     the user declined elevation or it otherwise failed.
    /// </summary>
    Task<bool> TryAddHostsEntryAsync(string domain);
}

/// <summary>Thrown when killing Riot processes fails due to insufficient permissions.</summary>
internal sealed class PlatformAccessDeniedException : System.Exception
{
    public PlatformAccessDeniedException(string message) : base(message)
    {
    }
}
