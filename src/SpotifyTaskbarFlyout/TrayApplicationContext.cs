using Microsoft.Win32;

namespace SpotifyTaskbarFlyout;

/// <summary>Owns the system tray icon, English quick menu, Spotify API client and flyout window.</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private readonly SpotifyAuthService _auth;
    private readonly SpotifyApiClient _api;
    private readonly MediaController _media;
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _menu;
    private readonly FlyoutForm _flyout;

    private readonly ToolStripMenuItem _nowPlayingItem;
    private readonly ToolStripMenuItem _previousItem;
    private readonly ToolStripMenuItem _playPauseItem;
    private readonly ToolStripMenuItem _nextItem;
    private readonly ToolStripMenuItem _playlistsMenu;
    private readonly ToolStripMenuItem _accountMenu;
    private readonly ToolStripMenuItem _autostartItem;

    public TrayApplicationContext()
    {
        _settings = AppSettings.Load();
        _http = new HttpClient();
        _auth = new SpotifyAuthService(_settings, _http);
        _api = new SpotifyApiClient(_auth, _http);
        _media = new MediaController();

        string version = Application.ProductVersion.Split('+')[0];

        _nowPlayingItem = new ToolStripMenuItem(Strings.MenuOpenSpotify, null, (_, _) => SpotifyLauncher.Open());
        _previousItem = new ToolStripMenuItem(Strings.MenuPrevious, null, (_, _) => _ = _media.PreviousAsync());
        _playPauseItem = new ToolStripMenuItem(Strings.MenuPlay, null, (_, _) => _ = _media.TogglePlayPauseAsync());
        _nextItem = new ToolStripMenuItem(Strings.MenuNext, null, (_, _) => _ = _media.NextAsync());

        _playlistsMenu = new ToolStripMenuItem(Strings.MenuPlaylists);
        _accountMenu = new ToolStripMenuItem(Strings.MenuConnectSpotify);

        _autostartItem = new ToolStripMenuItem(Strings.MenuStartWithWindows, null, (_, _) =>
        {
            AutoStart.SetEnabled(!AutoStart.IsEnabled);
            UpdateMenu();
        });

        var aboutItem = new ToolStripMenuItem(Strings.MenuAbout(version), null, (_, _) => ShowAbout(version));
        var exitItem = new ToolStripMenuItem(Strings.MenuExit, null, (_, _) => ExitThread());

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(new ToolStripItem[]
        {
            _nowPlayingItem, new ToolStripSeparator(),
            _previousItem, _playPauseItem, _nextItem, new ToolStripSeparator(),
            _playlistsMenu, _accountMenu, new ToolStripSeparator(),
            _autostartItem, aboutItem, new ToolStripSeparator(),
            exitItem,
        });
        _menu.Opening += (_, _) => UpdateMenu();

        _flyout = new FlyoutForm(_media, _api, _settings, _menu, ConnectSpotifyAsync);

        _trayIcon = new NotifyIcon
        {
            Icon = AppIcon.Load(SystemInformation.SmallIconSize),
            Text = Strings.AppName,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _trayIcon.MouseClick += OnTrayMouseClick;

        _media.StateChanged += (_, _) => UpdateTrayText();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        UpdateMenu();
        _ = _media.InitializeAsync();
        if (_api.IsConnected)
        {
            _ = InitializeSpotifyAccountAsync();
        }
    }

    private async Task InitializeSpotifyAccountAsync()
    {
        string? name = await _api.GetCurrentUserDisplayNameAsync();
        if (!string.IsNullOrWhiteSpace(name))
        {
            _settings.UserDisplayName = name;
            _settings.Save();
        }

        await _api.RefreshPlaybackAsync();
        await _api.RefreshPlaylistsAsync();
        UpdateMenu();
        _flyout.RefreshLayoutAndData();
    }

    private async Task ConnectSpotifyAsync()
    {
        using var dialog = new ConnectSpotifyForm(_auth, _settings);
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            await InitializeSpotifyAccountAsync();
            _flyout.OpenFlyout();
        }
    }

    private void DisconnectSpotify()
    {
        _auth.Disconnect();
        _api.ClearCache();
        UpdateMenu();
        _flyout.RefreshLayoutAndData();
    }

    private void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Middle)
        {
            if (_media.State.HasSession)
            {
                _ = _media.TogglePlayPauseAsync();
            }
            else
            {
                SpotifyLauncher.Open();
            }

            return;
        }

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        // Clicking the tray icon while the flyout is open triggers Deactivate right before
        // MouseClick; avoid reopening immediately in that case.
        if ((DateTime.UtcNow - _flyout.LastDeactivatedUtc).TotalMilliseconds < 250)
        {
            return;
        }

        _flyout.ToggleFlyout();
    }

    private void UpdateMenu()
    {
        _autostartItem.Checked = AutoStart.IsEnabled;

        PlaybackState state = _media.State;
        _playPauseItem.Text = state.IsPlaying ? Strings.MenuPause : Strings.MenuPlay;
        _playPauseItem.Enabled = state.HasSession && state.CanPlayPause;
        _previousItem.Enabled = state.HasSession && state.CanPrevious;
        _nextItem.Enabled = state.HasSession && state.CanNext;

        string nowPlaying = state.HasSession && !string.IsNullOrWhiteSpace(state.Title)
            ? $"{state.Title} – {state.Artist}"
            : Strings.MenuOpenSpotify;
        _nowPlayingItem.Text = Truncate(nowPlaying, 60).Replace("&", "&&");

        // Account menu
        _accountMenu.DropDownItems.Clear();
        if (_api.IsConnected)
        {
            string label = string.IsNullOrWhiteSpace(_settings.UserDisplayName) ? "Connected" : _settings.UserDisplayName;
            _accountMenu.Text = Strings.MenuConnectedAs(Truncate(label, 24)).Replace("&", "&&");
            _accountMenu.Click -= OnConnectMenuClick;
            _accountMenu.DropDownItems.Add(new ToolStripMenuItem(Strings.MenuDisconnectSpotify, null, (_, _) => DisconnectSpotify()));
        }
        else
        {
            _accountMenu.Text = Strings.MenuConnectSpotify;
            _accountMenu.Click -= OnConnectMenuClick;
            _accountMenu.Click += OnConnectMenuClick;
        }

        // Playlists submenu
        _playlistsMenu.DropDownItems.Clear();
        _playlistsMenu.Visible = _api.IsConnected;
        if (_api.IsConnected)
        {
            string? activeUri = _api.LastPlayback?.ContextUri;
            if (_api.Playlists.Count == 0)
            {
                var emptyItem = new ToolStripMenuItem(Strings.MenuNoPlaylists) { Enabled = false };
                _playlistsMenu.DropDownItems.Add(emptyItem);
            }
            else
            {
                var byId = _api.Playlists.ToDictionary(p => p.Id, StringComparer.Ordinal);
                var assigned = new HashSet<string>(StringComparer.Ordinal);

                foreach (PlaylistFolder folder in _settings.Folders)
                {
                    var folderMenu = new ToolStripMenuItem(Truncate(folder.Name, 36).Replace("&", "&&"));
                    foreach (string id in folder.PlaylistIds)
                    {
                        if (byId.TryGetValue(id, out SpotifyPlaylist? pl) && assigned.Add(id))
                        {
                            var plItem = new ToolStripMenuItem(Truncate(pl.Name, 40).Replace("&", "&&"), null, (_, _) => _ = _api.PlayPlaylistAsync(pl.Uri))
                            {
                                Checked = string.Equals(pl.Uri, activeUri, StringComparison.OrdinalIgnoreCase),
                            };
                            folderMenu.DropDownItems.Add(plItem);
                        }
                    }

                    if (folderMenu.DropDownItems.Count > 0)
                    {
                        _playlistsMenu.DropDownItems.Add(folderMenu);
                    }
                }

                if (_playlistsMenu.DropDownItems.Count > 0 && _api.Playlists.Any(p => !assigned.Contains(p.Id)))
                {
                    _playlistsMenu.DropDownItems.Add(new ToolStripSeparator());
                }

                foreach (SpotifyPlaylist pl in _api.Playlists)
                {
                    if (assigned.Contains(pl.Id))
                    {
                        continue;
                    }

                    var item = new ToolStripMenuItem(Truncate(pl.Name, 40).Replace("&", "&&"), null, (_, _) => _ = _api.PlayPlaylistAsync(pl.Uri))
                    {
                        Checked = string.Equals(pl.Uri, activeUri, StringComparison.OrdinalIgnoreCase),
                    };
                    _playlistsMenu.DropDownItems.Add(item);
                }
            }

            _playlistsMenu.DropDownItems.Add(new ToolStripSeparator());
            _playlistsMenu.DropDownItems.Add(new ToolStripMenuItem(Strings.MenuRefreshPlaylists, null, (_, _) => _ = _api.RefreshPlaylistsAsync()));
        }
    }

    private void OnConnectMenuClick(object? sender, EventArgs e) => _ = ConnectSpotifyAsync();

    private void UpdateTrayText()
    {
        PlaybackState state = _media.State;
        string text = state.HasSession && !string.IsNullOrWhiteSpace(state.Title)
            ? $"{(state.IsPlaying ? "▶" : "⏸")} {state.Title}\n{state.Artist}"
            : Strings.AppName;

        // NotifyIcon.Text is limited to 127 characters.
        _trayIcon.Text = Truncate(text, 127);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            _flyout.ApplyTheme();
        }
    }

    private static void ShowAbout(string version) =>
        MessageBox.Show(Strings.AboutText(version), Strings.AboutTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);

    protected override void ExitThreadCore()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _flyout.Dispose();
        _menu.Dispose();
        _media.Dispose();
        _api.ClearCache();
        _auth.Dispose();
        _http.Dispose();

        base.ExitThreadCore();
    }
}
