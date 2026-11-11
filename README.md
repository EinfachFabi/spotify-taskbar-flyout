<p align="center">
  <img src="src/SpotifyTaskbarFlyout/Resources/app.ico" width="96" alt="spotify-taskbar-flyout">
</p>

<h1 align="center">spotify-taskbar-flyout</h1>

<p align="center">
  A lightweight Windows system tray flyout for controlling Spotify.
</p>




https://github.com/user-attachments/assets/339ee940-b439-4e56-9682-3960ca846d8d





## Features

- System tray icon that opens a native-style flyout above the taskbar
- Current track, artist, album, cover art, and interactive seek bar
- Play/Pause, Previous, Next, Shuffle, and Repeat
- Spotify in-app volume slider and playlist switcher via Spotify account login
- Collapsible local playlist folders (right-click a playlist in the flyout)
- Matches Windows light and dark system theme
- Optional autostart with Windows

## Installation

1. Download the latest release from [Releases](../../releases/latest):
   - `spotify-taskbar-flyout.exe` (self-contained, no runtime required)
   - `spotify-taskbar-flyout-small.exe` (requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0))
2. Place the `.exe` anywhere and run it.
3. Optional: right-click the tray icon and enable **Start with Windows**.

> Because the executable is not code-signed, Windows SmartScreen may show a warning on first launch. Click **More info** -> **Run anyway**.

## Spotify Login (In-App Volume & Playlists)

Basic playback controls work immediately via Windows Media Controls. To control Spotify's internal volume and switch playlists:

1. Click **Connect Spotify** in the flyout (or right-click the tray icon -> **Connect Spotify account...**).
2. Create an app in the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard).
3. Add this Redirect URI in the app settings:
   ```
   http://127.0.0.1:43819/callback
   ```
4. Paste the **Client ID** into the dialog and connect.

Spotify Premium is required by the Spotify Web API for playback modification endpoints.

## Controls

- **Left-click tray icon:** Open/close flyout
- **Middle-click tray icon:** Play/Pause
- **Right-click tray icon:** Quick menu and playlists
- **Shortcuts (flyout open):** `Space`/`K` Play/Pause, `Left`/`Right` Seek 5s, `Ctrl+Left`/`Right` Prev/Next, `Up`/`Down` Volume, `M` Mute, `Esc` Close

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet run --project src/SpotifyTaskbarFlyout
./build.ps1
```


## License

[MIT](LICENSE)

*Not affiliated with Spotify AB.*
