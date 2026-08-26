# StreamBox — Windows IPTV Player

A modern Windows desktop IPTV player built with **.NET 8**, **Avalonia UI**, and **libmpv** for hardware-accelerated video playback. Stream live TV channels from M3U playlists with per-channel custom HTTP headers, category filtering, and a clean dark UI.

![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11-blue)
![Framework](https://img.shields.io/badge/.NET-8.0-purple)
![License](https://img.shields.io/badge/license-GPL--3.0-green)

---

## Features

- **Hardware-accelerated playback** — mpv/libmpv with `hwdec=auto-safe` (Intel, AMD, NVIDIA auto-detection with software fallback)
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
- **Inno Setup installer** — one-click install with proper file associations

---

## Architecture

```
StreamBox/
├── Assets/                     Icons and images
│   ├── logo.png                App logo (source for .ico generation)
│   └── app-icon.ico            Generated multi-resolution icon
├── Models/
│   └── Channel.cs              Channel data model
├── Native/
│   └── MpvClient.cs            libmpv P/Invoke wrapper (direct, no third-party dependency)
├── Services/
│   ├── DatabaseService.cs      SQLite WAL persistence with error-14 recovery
│   ├── Log.cs                  Thread-safe file logger
│   ├── NativeDialog.cs         Win32 MessageBox fallback
│   ├── PlayerService.cs        Channel switching engine (generation-counter, re-entrancy guard)
│   └── PlaylistService.cs      M3U parser with #EXTVLCOPT/#EXTHTTP support
├── ViewModels/
│   └── MainViewModel.cs        MVVM ViewModel (CommunityToolkit.Mvvm)
├── Views/
│   ├── MainWindow.axaml        Avalonia XAML layout
│   └── MainWindow.axaml.cs     Code-behind (HWND embedding, overlay management)
├── App.axaml / App.axaml.cs    Application entry + DI container
├── Program.cs                  Main entry point (mutex, global exception handlers)
├── StreamBox.csproj            Project file with native DLL bundling + publish verification
├── StreamBox.iss               Inno Setup installer script
├── build.bat                   Automated build pipeline
├── app.manifest                DPI awareness + Windows version compatibility
└── README.md                   This file
```

### Key Design Decisions

| Decision | Choice | Why |
|---|---|---|
| mpv wrapper | Direct P/Invoke over libmpv client API | Mpv.NET is unmaintained (~2019). The mpv C API is stable and the interop is ~300 lines. |
| Video rendering | Native HWND embed via Win32 `CreateWindowEx` + mpv `wid` option | The OpenGL render API requires hooking Avalonia's compositor — fragile across versions. A raw child HWND is the most reliable path on Windows. |
| Channel switching | Generation-counter + SemaphoreSlim guard | Prevents race conditions when switching rapidly (A→B→C→D). Old callbacks are silently discarded. |
| Database | SQLite with WAL mode + busy_timeout | Prevents "database locked" errors. Error-14 auto-recovery deletes and recreates the DB. |
| Icon generation | ImageMagick in `build.bat` | Converts `Assets/logo.png` → multi-resolution `.ico` automatically. |

---

## Prerequisites

| Tool | Version | Purpose |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | 8.0+ | Build and publish |
| [Inno Setup](https://jrsoftware.org/isinfo.php) | 6.0+ | Create Windows installer |
| [ImageMagick](https://imagemagick.org/script/download.php) | 7.0+ | Auto-generate `.ico` from `logo.png` |
| [libmpv](https://sourceforge.net/projects/mpv-player-windows/files/libmpv/) | 2.x | Native video playback library |

---

## Quick Start

### 1. Clone the repository

```bash
git clone https://github.com/abirsiddiky/StreamBox-WINDOWS.git
cd StreamBox-WINDOWS
```

### 2. Download libmpv

Download the 64-bit libmpv build and place `libmpv-2.dll` (or `mpv-2.dll`) in:

```
mpv/win-x64/libmpv-2.dll
```

### 3. Build and run

```bash
dotnet restore
dotnet run
```

### 4. Build the installer

```bash
build.bat
```

This will:
1. Check for .NET SDK, Inno Setup, and ImageMagick
2. Generate `Assets/app-icon.ico` from `Assets/logo.png`
3. Publish a self-contained single-file executable
4. Verify mpv DLL is in publish output
5. Build the Inno Setup installer → `Output/StreamBox-Setup.exe`

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

### `Views/MainWindow.axaml` — UI Layout

```
┌──────────────────────────────────────────┐
│           StreamBox (native title bar)    │
├──────────────────────────┬───────────────┤
│                          │ Categories    │
│                          │ (scrollable)  │
│      Video Area          ├───────────────┤
│      (mpv HWND)          │ Search        │
│                          ├───────────────┤
│  [Idle/Buffering/Error]  │ Channel List  │
│                          │               │
└──────────────────────────┴───────────────┘
```

Overlays (Idle, Buffering, Error) are Avalonia controls rendered **behind** the mpv HWND. The HWND is only shown when `PlayerState.Playing`, ensuring overlays are always visible otherwise.

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

Edit `Services/PlayerService.cs` — the buffering timeout is currently 30 seconds:

```csharp
await Task.Delay(TimeSpan.FromSeconds(30), ct);
```

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

The `VerifyMpvNative` target fails the build if no libmpv DLL is found in the publish output. This prevents accidentally shipping a broken build.

---

## Troubleshooting

### "libmpv DLL not found"

Place `libmpv-2.dll` (64-bit) in `mpv/win-x64/` before running `build.bat`.

### Channels don't load

Check `%LocalAppData%\StreamBox\logs\startup.log` for errors. Common causes:
- Network unreachable (firewall/proxy blocking GitHub raw content)
- M3U URL returns HTML instead of M3U (URL changed or requires auth)

### Video area is black

The mpv HWND is created lazily on first channel play. If no channel has been selected yet, the idle overlay ("No channel selected") should be visible. If it's not, check the log for HWND creation errors.

### Channel switching crashes

Check the log for generation mismatch messages. If you see "Generation superseded" messages, the switching logic is working correctly — stale callbacks are being discarded.

### Hardware decoding not working

Check the log for `hwdec` messages. If mpv falls back to software decoding, it means your GPU driver doesn't support the required hardware decoder. Update your GPU drivers.

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
