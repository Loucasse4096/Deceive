![Deceive Logo](http://i.thijsmolendijk.nl/deceive.png)

[![Discord](https://discordapp.com/api/guilds/249481856687407104/widget.png?style=shield)](https://discord.gg/bfxdsRC)

> [!CAUTION]
> The `deceive.app` website is **UNRELATED** to this project and does not represent this project. **THE DOWNLOAD ON THAT SITE CONTAINS MALWARE**. If you downloaded anything from `deceive.app`, reinstall your PC.

# :tophat: Deceive

Deceive allows you to appear offline in League of Legends, VALORANT and Legends of Runeterra without any loss of functionality! Talk to your friends, communicate in champion select and queue up together, all while sneakily appearing offline to all your friends.

Once started, Deceive will be a little icon in your notification tray that allows you to manage your chat presence, whether it be online, offline, or mobile.

# FAQ

### Where can I download Deceive?
Click the [Releases](https://github.com/molenzwiebel/Deceive/releases) tab at the top to download the latest version.

### Can I still invite people? Can they invite me?
Your friends list will work as normal, which means that you can invite everyone. Your friends will not be able to invite you, even if they enter your name manually.

### Can I talk in lobbies/champion/agent select?
Yes, you can talk in lobbies just fine. Only your global "presence" is filtered.

### How do I use Deceive with a specific game?
The first time you launch Deceive, you will be able to choose which game to launch and whether to remember that decision. You can also use the Deceive tray icon to launch a different game.

You can also launch Deceive with `lol`, `lor`, or `valorant` as command-line argument to automatically launch your game of choice.

### Is this approved by Riot?
Riot has confirmed that [you won't get banned](https://i.thijsmolendijk.nl/deceive_ok.png) for using Deceive. It may break at any time though.

### How do I solve the "failing to resolve some required domains" issue?
Deceive works by sitting between the Riot Client and the chat servers. To do that, it needs to intercept traffic, which involves giving the client a different address to connect to. We use `deceive-localhost.molenzwiebel.xyz`, which normally resolves to your local computer. For some network setups/ISPs/school/work networks, this domain does not resolve. If you are on such a network, you'll need to either change your DNS to something like [Cloudflare's 1.1.1.1](https://developers.cloudflare.com/1.1.1.1/setup/windows/) or [Google's 8.8.8.8](https://developers.google.com/speed/public-dns/docs/using), or [manually add](https://kb.parallels.com/en/129398) the entry `127.0.0.1 deceive-localhost.molenzwiebel.xyz` to your `hosts` file. Note that for both of these options you will need Administrator access on your PC. If you are having trouble with this step, Google or LLMs like ChatGPT can usually walk you through the required steps.

### I'm more of a visual learner. Do you have a video?
Sure thing! Just click the preview below:  
[![Youtube Preview](http://img.youtube.com/vi/bfsbtd39GqE/maxresdefault.jpg)](https://youtu.be/bfsbtd39GqE)

---

# :apple: macOS port (Apple Silicon)

This branch contains an unofficial **macOS** port of Deceive, targeting Apple Silicon
(`osx-arm64`) and Intel (`osx-x64`). It is a faithful port of
[molenzwiebel/Deceive](https://github.com/molenzwiebel/Deceive) and remains licensed under
**GPL-3.0**. All credit for the original tool and its mechanism goes to **molenzwiebel**.

The port keeps the original mechanism exactly: a local **config proxy** rewrites the chat
server in the Riot Client configuration, and a local **XMPP-over-TLS chat proxy** rewrites
your presence stanzas. Nothing about how Deceive masks your status was changed — only the
Windows-specific glue (UI, process/path detection, hosts editing) was rewritten.

> [!NOTE]
> Only games with a **native macOS client** can be launched: **League of Legends** and
> **Legends of Runeterra** (plus launching just the Riot Client). VALORANT and 2XKO have no
> macOS client, so they are intentionally not offered in the launcher on Mac.

## UI stack

The Windows tray UI (Windows Forms `NotifyIcon`/`MessageBox`) was rewritten with
**[Avalonia](https://avaloniaui.net/) 11** and its `TrayIcon` + `NativeMenu`, which render a
real `NSStatusItem` menu-bar item on macOS. Avalonia was chosen over a Swift/AppKit front-end
or .NET MAUI because it keeps the whole app in a single C# project with a single `dotnet`
build, runs the (unchanged) network engine in-process with no IPC, and is MIT-licensed
(GPL-compatible). See `AvaloniaUserInterface.cs` for the tray/menu/dialog code and
`IUserInterface.cs` for the abstraction that keeps the engine UI-framework-agnostic.

## Building

Requires the **.NET 8 SDK**.

```bash
# Build a self-contained Deceive.app (Apple Silicon by default):
./build-macos.sh

# For Intel Macs:
RID=osx-x64 ./build-macos.sh
```

The bundle is produced at `artifacts/Deceive.app`. It is self-contained — no .NET runtime is
required on the target Mac.

For quick development you can also just run it directly:

```bash
dotnet run --project Deceive/Deceive.csproj
# or launch a specific game directly (same CLI as the original):
dotnet run --project Deceive/Deceive.csproj -- lol
```

## Running / Gatekeeper

The bundle is only ad-hoc signed, so on first launch macOS may say it is from an
"unidentified developer". For personal use, remove the quarantine flag:

```bash
xattr -dr com.apple.quarantine artifacts/Deceive.app
open artifacts/Deceive.app
```

By default the app shows both a Dock icon and a menu-bar icon (so the startup game-picker
dialog reliably comes to the front). To make it a pure menu-bar app with no Dock icon, add
`<key>LSUIElement</key><true/>` to `Info.plist` (a comment in `build-macos.sh` marks the spot).

## hosts file / DNS

Deceive needs `deceive-localhost.molenzwiebel.xyz` to resolve to `127.0.0.1`. If it doesn't,
Deceive offers to add the entry to `/etc/hosts` for you, prompting for your **administrator
password** via the native macOS dialog (`osascript ... with administrator privileges`).
Alternatively, switch your DNS to `1.1.1.1` or `8.8.8.8`.

## TLS certificate

Unchanged from the original: Deceive downloads a pre-signed certificate (whose chain the Riot
Client already trusts) from `mln.cx/deceive/localhost.pfx` and caches it under
`~/Library/Application Support/Deceive/`. No certificate needs to be installed into the macOS
keychain. (Generating a self-signed cert on the fly via `CertificateRequest` is possible but
would require the Riot Client to trust it, so the original download mechanism was kept.)

## Maintenance — what breaks when Riot changes things

Deceive breaks when Riot changes the format of its config or presence data. The two places to
look first:

1. **Config rewrite** (`ConfigProxy.cs`) — if Riot renames/moves `chat.host`, `chat.port`,
   `chat.affinities`, or the geo/PAS affinity flow, the JSON rewrite throws. This surfaces the
   dedicated error alert ("Deceive was unable to rewrite a League of Legends configuration
   file…") and Deceive exits. **Diagnose** via `~/Library/Application Support/Deceive/debug.log`:
   look for the `ORIGINAL CLIENTCONFIG` / `MODIFIED CLIENTCONFIG` traces and the exception
   right after.
2. **Presence rewrite** (`ProxiedConnection.cs`) — if Riot changes the XMPP presence schema
   (e.g. the `<games>`/`<show>` structure, per-game tags like `league_of_legends`/`valorant`/
   `bacon`/`lion`, or the roster `<query>` element used to inject the fake "Deceive Active!"
   contact), masking can silently stop working even though no error is shown. **Diagnose** via
   the `RC TO SERVER` / `DECEIVE TO SERVER` / `SERVER TO RC` traces in the same `debug.log`.

If either breaks, first check for a newer upstream Deceive release, since the rewrite logic in
those two files is shared with the original project.
