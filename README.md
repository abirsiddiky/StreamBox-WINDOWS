# StreamBox — Windows IPTV Player

A modern Windows desktop IPTV player built with **.NET 8**, **Avalonia UI**, and **libmpv** for hardware-accelerated video playback. Stream live TV channels from M3U playlists with per-channel custom HTTP headers, category filtering, and a clean dark UI.

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![Framework](https://img.shields.io/badge/.NET-8.0-purple)
![License](https://img.shields.io/badge/license-GPL--3.0-green)

---

## Features

- **Hardware-accelerated playback** — mpv/libmpv with `hwdec=auto-safe` (Intel, AMD, NVIDIA auto-detection with software fallback)
- **Low-end/virtualized GPU safe** — `profile=fast` keeps mpv's shader-based scaling/debanding off, avoiding CPU-heavy software rendering on weak or GPU-less machines (VMs, old integrated graphics)
- **Network-resilient streaming** — libavformat reconnect options + generous demuxer cache absorb transient drops; silent auto-retry (3 attempts, backoff) on mid-playback errors before surfacing "offline" to the user
- **Player-matched networking** — User-Agent set to match VLC's default, since many free IPTV CDNs reject unrecognized/custom player UAs
- **M3U playlist support** — parse `#EXTINF`, `#EXTVLCOPT`, `#EXTHTTP` directives
- **Per-channel HTTP headers** — custom User-Agent, cookies, referrer per channel via `#EXTVLCOPT` / `#EXTHTTP`
- **Channel logos** — displays `tvg-logo` images (PNG, JPG, SVG, WEBP)
- **Category filtering** — dynamically parsed from `group-title`, horizontally scrollable
- **Search** — instant filter across all channels
- **Safe channel switching** — generation-counter pattern prevents race conditions, stale callbacks, and crash-on-rapid-switch
- **SQLite persistence** — channels cached locally for instant offline loading
- **Background refresh** — playlist updated from network while showing cached data
- **Loading/error overlays** — no blank screen during channel transitions or failures
- **Retry mechanism** — automatic retry on stream failure with manual retry button
- **Single-instance** — only one StreamBox window can run at a time
- **Self-installing dependencies** — Inno Setup installer bundles the VC++ Redistributable and Vulkan Runtime, installing each silently (elevated) only if missing, so end users never see a native-DLL-missing crash
- **Background-flushed logging** — file logging is batched off the UI thread so diagnostic logging never causes playback/UI stutter, especially on low-end hardware

---

## Architecture

```
StreamBox/
├── Assets/                     Icons and images
│   ├── logo.png                App logo (source for .ico generation)
│   └── app-icon.ico            Generated multi-resolution icon
├── Redist/                     Bundled runtime installers (not committed — see Prerequisites)
│   ├── vc_redist.x64.exe
│   └── VulkanRT-Installer.exe
├── Models/
│   └── Channel.cs              Channel data model
├── Native/
│   └── MpvClient.cs            libmpv P/Invoke wrapper (direct, no third-party dependency)
├── Services/
│   ├── DatabaseService.cs      SQLite WAL persistence with error-14 recovery
│   ├── Log.cs                  Thread-safe, background-flushed file logger
│   ├── NativeDialog.cs         Win32 MessageBox fallback
│   ├── PlayerService.cs        Channel switching engine (generation-counter, re-entrancy guard, silent reconnect)
│   └── PlaylistService.cs      M3U parser with #EXTVLCOPT/#EXTHTTP support
├── ViewModels/
│   └── MainViewModel.cs        MVVM ViewModel (CommunityToolkit.Mvvm)
├── Views/
│   ├── MainWindow.axaml        Avalonia XAML layout
│   └── MainWindow.axaml.cs     Code-behind (HWND embedding, overlay management)
├── App.axaml / App.axaml.cs    Application entry + DI container
├── Program.cs                  Main entry point (mutex, global exception handlers)
├── StreamBox.csproj            Project file with native DLL bundling + publish verification
├── StreamBox.iss               Inno Setup installer script (bundles redistributables)
├── build.bat                   Automated build pipeline
├── app.manifest                DPI awareness + Windows version compatibility
└── README.md                   This file
```

### Key Design Decisions

| Decision | Choice | Why |
|---|---|---|
| mpv wrapper | Direct P/Invoke over libmpv client API | Mpv.NET is unmaintained (~2019). The mpv C API is stable and the interop is ~300 lines. |
| Video rendering | Native HWND embed via Win32 `CreateWindowEx` + mpv `wid` option | The OpenGL render API requires hooking Avalonia's compositor — fragile across versions. A raw child HWND is the most reliable path on Windows. |
| libmpv build variant | Baseline `x86_64` (non-`v3`) | `x86_64-v3` builds require AVX2 (Haswell+ CPUs) and crash with an illegal-instruction fault on older CPUs at runtime — hard to diagnose since it happens mid-playback, not at load time. Baseline build trades negligible perf for real compatibility. |
| Rendering profile | mpv `profile=fast` | mpv's default `vo=gpu` uses shader-based high-quality scaling/debanding. With no real GPU (VMs, very old integrated graphics), these shaders run in software and spike CPU to 90–100%. `profile=fast` switches to bilinear scaling with no deband/interpolation — dramatically lower CPU with no visible quality loss for live IPTV. |
| Channel switching | Generation-counter + SemaphoreSlim guard | Prevents race conditions when switching rapidly (A→B→C→D). Old callbacks are silently discarded. |
| Network resilience | libavformat `reconnect` options + generous demuxer cache + app-level silent retry | Free/shared IPTV sources drop connections often. mpv has no reconnect behavior by default (unlike VLC/PotPlayer), so without this a transient blip permanently kills playback. |
| Database | SQLite with WAL mode + busy_timeout | Prevents "database locked" errors. Error-14 auto-recovery deletes and recreates the DB. |
| Logging | Background-flushed queue, not synchronous per-call file I/O | Per-call `File.AppendAllText` on the UI thread (fired dozens of times/second by layout events) causes visible stutter on slow disks/CPUs. Batching every ~500ms removes this without losing diagnostic value. |
| Runtime dependencies | Bundled in the installer, not assumed present | libmpv's PE import table hard-depends on `vulkan-1.dll` (ships with GPU drivers) and may depend on the VC++ runtime. Real PCs almost always have these via GPU drivers, but bare/thin-client/VM installs don't — causing a `DllNotFoundException` before any network activity. The installer now installs both silently if missing. |
| Icon generation | ImageMagick in `build.bat` | Converts `Assets/logo.png` → multi-resolution `.ico` automatically. |

---

## Prerequisites

| Tool | Version | Purpose |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | 8.0+ | Build and publish |
| [Inno Setup](https://jrsoftware.org/isinfo.php) | 6.0+ | Create Windows installer |
| [ImageMagick](https://imagemagick.org/script/download.php) | 7.0+ | Auto-generate `.ico` from `logo.png` |
| [libmpv](https://sourceforge.net/projects/mpv-player-windows/files/libmpv/) | 2.x, **baseline `x86_64` build — NOT the `x86_64-v3` variant** | Native video playback library |
| [VC++ Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe) | latest | Bundled into the installer; place in `Redist/vc_redist.x64.exe` before building |
| [Vulkan Runtime](https://vulkan.lunarg.com/sdk/home) | latest | Bundled into the installer; download and rename to `Redist/VulkanRT-Installer.exe` before building |

---

## Quick Start

### 1. Clone the repository

```bash
git clone https://github.com/abirsiddiky/StreamBox-WINDOWS.git
cd StreamBox-WINDOWS
```

### 2. Download libmpv

Download the **baseline `x86_64` build** (not `x86_64-v3` — that variant requires AVX2 and will crash on older CPUs) and place `libmpv-2.dll` (or `mpv-2.dll`) in:

```
mpv/win-x64/libmpv-2.dll
```

### 3. Download the bundled redistributables

Download these two installers and place them exactly as named below — `build.bat` verifies both exist before compiling the installer:

```
Redist/vc_redist.x64.exe          ← https://aka.ms/vs/17/release/vc_redist.x64.exe
Redist/VulkanRT-Installer.exe     ← from https://vulkan.lunarg.com/sdk/home, renamed
```

### 4. Build and run

```bash
dotnet restore
dotnet run
```

### 5. Build the installer

```bash
build.bat
```

This will:
1. Check for .NET SDK, Inno Setup, and ImageMagick
2. Generate `Assets/app-icon.ico` from `Assets/logo.png`
3. Publish a self-contained single-file executable
4. Verify libmpv DLL and both `Redist/` installers are present
5. Build the Inno Setup installer → `Output/StreamBox-Setup.exe`

The resulting installer silently installs the VC++ Redistributable and Vulkan Runtime (only if not already present, with a UAC prompt as needed) before launching StreamBox for the first time.

---

## Project Structure Deep Dive

### `Native/MpvClient.cs` — libmpv P/Invoke Wrapper

Directly wraps the mpv client API using P/Invoke. No third-party NuGet package.

**Key functions used:**
- `mpv_create` / `mpv_initialize` — create and initialize mpv instance
- `mpv_set_option_string` — configure mpv (hwdec, user-agent, wid, etc.)
- `mpv_command` — send commands (loadfile, stop)
- `mpv_wait_event` — event loop (FileLoaded, EndFile, PropertyChange, Shutdown)
- `mpv_observe_property` — watch for idle-active state changes
- `mpv_terminate_destroy` — clean shutdown

**Hardware decoding:** Set via `hwdec=auto-safe` which tries D3D11VA/VAAPI first, falls back to software.

**Rendering profile:** `profile=fast` avoids CPU-heavy shader-based scaling/debanding — important on VMs and older/GPU-less machines where the default `vo=gpu` scalers run in software and pin the CPU.

**Networking:**
- `user-agent` is set to match VLC's default (`VLC/3.0.20 LibVLC/3.0.20`) — several free IPTV CDNs reject unrecognized custom UAs, even though the same URL works fine in VLC/PotPlayer/the OS default player.
- `stream-lavf-o=reconnect=1,reconnect_streamed=1,reconnect_at_eof=1,reconnect_delay_max=5` plus `cache=yes`, `demuxer-readahead-secs`, `demuxer-max-bytes`, `demuxer-max-back-bytes` give mpv the same kind of resilience to transient network drops that VLC/PotPlayer have built in by default.

### `Services/PlayerService.cs` — Channel Switching Engine

Implements a safe channel-switching system:

```
REQUEST CHANNEL SWITCH
        ↓
INCREMENT GENERATION
        ↓
FIRE BUFFERING STATE (immediately)
        ↓
ACQUIRE SWITCH LOCK (SemaphoreSlim)
        ↓
CANCEL OLD TIMEOUTS
        ↓
STOP/RELEASE OLD MEDIA (off UI thread, 3s timeout)
        ↓
VERIFY GENERATION still current
        ↓
CREATE NEW mpv INSTANCE
        ↓
APPLY PER-CHANNEL HEADERS
        ↓
LOAD STREAM
        ↓
START BUFFERING TIMEOUT (30s, generation-tagged)
        ↓
RELEASE SWITCH LOCK
```

Every async callback verifies `eventGeneration == currentGeneration` before modifying state.

**Mid-playback resilience:** if a stream that was already playing successfully drops with an mpv `EndFile` error, the service does up to 3 silent reconnect attempts (2s/4s/8s backoff) before surfacing the "Stream Unavailable" overlay — this covers the transient network blips that would otherwise show as a false "offline" even though the source is fine. A stream that fails on its *initial* connection attempt still surfaces the error immediately, since that's a genuinely dead/invalid stream, not a drop.

### `Services/PlaylistService.cs` — M3U Parser

Parses M3U playlists with support for:

```m3u
#EXTINF:-1 group-title="News" tvg-logo="https://example.com/logo.png",BBC News
#EXTVLCOPT:http-user-agent=Mozilla/5.0 (Linux; Android 9; ...)
#EXTHTTP:{"cookie":"session=abc123"}
https://stream.example.com/live.m3u8
```

- `#EXTINF` — channel name, group, logo
- `#EXTVLCOPT:http-user-agent=` — per-channel User-Agent
- `#EXTVLCOPT:referrer=` — per-channel referrer
- `#EXTHTTP:{...}` — JSON headers (cookie, authorization, etc.)
- All other `#EXTVLCOPT` keys stored as extra headers

### `Services/DatabaseService.cs` — SQLite Persistence

- **Path:** `%LocalAppData%\StreamBox\streambox.db`
- **Mode:** WAL (Write-Ahead Logging) for concurrent reads
- **Busy timeout:** 5000ms
- **Error-14 recovery:** On SQLite "unable to open database file", deletes `.db`/`.db-shm`/`.db-wal` and recreates
- **Schema:** `channels` table + `settings` table for playlist source configuration

### `Services/Log.cs` — Background-Flushed Logger

Writes to `%LocalAppData%\StreamBox\logs\startup.log`. Log calls enqueue a formatted line instead of writing to disk immediately; a background thread flushes the queue to disk roughly every 500ms in a single batched write. This matters because layout/positioning events on the UI thread can log dozens of times per second — synchronous per-call file I/O there was a measurable source of stutter on low-end hardware. Remaining queued lines are flushed on process exit so nothing is lost on a clean close; a crash may lose the last partial batch.

### `Views/MainWindow.axaml` — UI Layout

```
┌──────────────────────────────────────────┐
<<<<<<< HEAD
│  StreamBox (windows native title bar)    │
=======
│   StreamBox (windows native title bar)   │
>>>>>>> aaae903b6c91e1abd083dde45acf7dd6f67a7779
├──────────────────────────┬───────────────┤
│                          │ Playlists     │
│                          │ (scrollable)  │
│      Video Area          ├───────────────┤
│      (mpv HWND)          │ Search        │
│                          ├───────────────┤
│  [Idle/Buffering/Error]  │ Channel List  │
│                          │               │
└──────────────────────────┴───────────────┘
```

Overlays (Idle, Buffering, Error) are Avalonia controls rendered **behind** the mpv HWND. The HWND is only shown when `PlayerState.Playing`, ensuring overlays are always visible otherwise.

The video HWND's position is only re-applied via Win32 `MoveWindow` when the computed rect actually differs from the last-applied one, since `LayoutUpdated` can fire very frequently — this avoids redundant Win32 calls and a potential re-entrant reposition loop.

---

## Modifying the App

### Adding a new channel field

1. Add the property to `Models/Channel.cs`
2. Add the column to `DatabaseService.cs` schema (handle migration for existing DBs)
3. Update `PlaylistService.cs` parser to populate the field
4. Update `ViewModel` bindings if needed
5. Update `MainWindow.axaml` item template to display it

### Changing the default playlist URL

Edit `Services/PlaylistService.cs`:

```csharp
private const string DefaultPlaylistUrl = "https://your-url.com/playlist.m3u";
```

### Customizing the UI theme

Edit the color values in `Views/MainWindow.axaml`:

```xml
<!-- Main background -->
Background="#1a1a2e"

<!-- Sidebar -->
Background="#16213e"

<!-- Category buttons -->
Background="#2a2a4a" Foreground="#bbb"

<!-- Accent color (used in loading bar, retry button) -->
Foreground="#7c3aed"
```

### Adding a new mpv option

Edit `Native/MpvClient.cs` constructor:

```csharp
Check(Native.mpv_set_option_string(_handle, "your-option", "value"), "set your-option");
```

### Changing the retry behavior

- Initial connection timeout: `Services/PlayerService.cs`, currently 30 seconds:
  ```csharp
  await Task.Delay(TimeSpan.FromSeconds(30), ct);
  ```
- Mid-playback silent reconnect attempts/backoff: `Services/PlayerService.cs`, `MaxSilentReconnectAttempts` and the `2000/4000/8000` ms backoff in the `EndFile` handler.

### Adding a new overlay state

1. Add the state to `PlayerState` enum in `PlayerService.cs`
2. Add the overlay XAML in `MainWindow.axaml`
3. Handle the state in `MainWindow.UpdateOverlayVisibility()`
4. Fire the state from `PlayerService` at the appropriate point

---

## Build Configuration

### `StreamBox.csproj` key properties

```xml
<TargetFramework>net8.0</TargetFramework>
<SelfContained>true</SelfContained>          <!-- Bundles .NET runtime -->
<PublishSingleFile>true</PublishSingleFile>  <!-- Single .exe output -->
<PublishTrimmed>false</PublishTrimmed>       <!-- No trimming (Avalonia uses reflection) -->
```

### Native DLL bundling

The mpv DLL is copied to publish output via an MSBuild target (`CopyMpvNative`), NOT via `<Content>` items (which get bundled into the single-file exe). This keeps the 114MB mpv DLL as a loose file beside the exe.

### Publish verification

The `VerifyMpvNative` target fails the build if no libmpv DLL is found in the publish output. `build.bat` additionally verifies both `Redist/vc_redist.x64.exe` and `Redist/VulkanRT-Installer.exe` exist before invoking Inno Setup, so a build can't accidentally ship without its bundled runtime dependencies.

### Installer dependency bundling (`StreamBox.iss`)

The installer copies `vc_redist.x64.exe` and `VulkanRT-Installer.exe` into a temp folder (`Flags: deleteafterinstall`) and, in the `[Code]` section's `ssPostInstall` step, runs each **only if not already present** on the target machine:

- `VCRedistNeeded()` checks the `HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64` registry key.
- `VulkanRuntimeNeeded()` checks for `vulkan-1.dll` in `{sys}`.

Both are launched via `ShellExec('runas', ...)` (not the `[Run]` section) because the main installer runs with `PrivilegesRequired=lowest`, but these redistributables' own installers require elevation — `ShellExec` with the `runas` verb triggers a UAC prompt for just that step instead of requiring the whole installer to run elevated.

---

## Troubleshooting

### "libmpv DLL not found"

Place `libmpv-2.dll` (64-bit, **baseline `x86_64` build, not `x86_64-v3`**) in `mpv/win-x64/` before running `build.bat`.

### App works on the developer's machine but not on other users' machines

Two independent causes to check, in order:

1. **`System.DllNotFoundException` on startup (before any network activity):** libmpv's PE imports require `vulkan-1.dll`, which ships with GPU drivers. Machines without a real GPU driver (VMs, thin clients) won't have it. Fixed by the installer's bundled Vulkan Runtime — if you built before that was added, install it manually from https://vulkan.lunarg.com/sdk/home.
2. **App crashes or behaves erratically mid-playback with no dialog:** you likely shipped the `x86_64-v3` libmpv build, which requires AVX2 (Haswell+ CPUs, 2014+) and hits an illegal-instruction fault on older CPUs. Re-download the baseline (non-`v3`) build.

### Channel plays fine in VLC/PotPlayer/Windows' default player but shows "Stream Unavailable" in StreamBox on the same machine

Almost always a **User-Agent mismatch** — some free IPTV CDNs reject non-player UAs. This is already addressed by matching VLC's default UA in `MpvClient.cs`; if you've changed it, revert to a recognized player UA.

### Channel plays, then randomly drops to "offline" after a while — but VLC/PotPlayer keep playing the same stream fine

This was mpv's lack of built-in reconnect behavior on transient network drops. Covered by the `stream-lavf-o=reconnect=...` options and the silent-retry logic in `PlayerService.cs` — if you still see this after a fresh build, check `startup.log` for `"silent reconnect"` entries to confirm the retry logic is running, and consider raising `MaxSilentReconnectAttempts`.

### Channels don't load

Check `%LocalAppData%\StreamBox\logs\startup.log` for errors. Common causes:
- Network unreachable (firewall/proxy blocking GitHub raw content)
- M3U URL returns HTML instead of M3U (URL changed or requires auth)
- The IPTV source enforces a single-device connection limit and another device/session is already using it

### Video area is black

The mpv HWND is created lazily on first channel play. If no channel has been selected yet, the idle overlay ("No channel selected") should be visible. If it's not, check the log for HWND creation errors.

### Channel switching crashes

Check the log for generation mismatch messages. If you see "Generation superseded" messages, the switching logic is working correctly — stale callbacks are being discarded.

### Hardware decoding not working / video is laggy or CPU usage is maxed out

Check the log for `hwdec` messages. If mpv falls back to software decoding, it means your GPU driver doesn't support the required hardware decoder — update your GPU drivers. Separately, on VMs or older/GPU-less machines, make sure `profile=fast` is set in `MpvClient.cs` (see Key Design Decisions above) — without it, mpv's default shader-based scaling runs in software and can pin the CPU at 90–100%, causing stutter even when decoding itself isn't the bottleneck.

---

## License

This project is licensed under the GNU General Public License v3.0 — see [LICENSE](LICENSE) for details.

---

## Credits

- [mpv](https://mpv.io/) — media player engine
- [Avalonia UI](https://avaloniaui.net/) — cross-platform .NET UI framework
- [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/) — MVVM toolkit
- [SQLite](https://www.sqlite.org/) — embedded database
- [Inno Setup](https://jrsoftware.org/isinfo.php) — Windows installer creator
