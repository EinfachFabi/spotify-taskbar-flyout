using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Windows.Media;
using static SpotifyTaskbarFlyout.NativeMethods;

namespace SpotifyTaskbarFlyout;

/// <summary>
/// Native-style Windows system tray flyout for controlling Spotify:
/// track info, seek bar, transport controls, Spotify in-app volume, and playlist switching with folders.
/// </summary>
internal sealed class FlyoutForm : Form
{
    private enum HitArea
    {
        None,
        Header,
        Progress,
        Shuffle,
        Previous,
        PlayPause,
        Next,
        Repeat,
        Mute,
        Volume,
        RefreshPlaylists,
        PlaylistRow,
        ConnectButton,
        OpenSpotify,
        Menu,
    }

    private sealed record DisplayRow(PlaylistFolder? Folder, SpotifyPlaylist? Playlist, bool Indented);

    // Logical dimensions (at 96 DPI).
    private const int FlyoutWidth = 360;
    private const int MarginX = 16;
    private const int MarginTop = 16;
    private const int CoverSize = 64;
    private const int PlaylistRowHeight = 40;
    private const int PlaylistListHeight = 200;
    private const int ConnectCardHeight = 96;
    private const int FooterHeight = 44;

    private static readonly bool IsWindows11 = Environment.OSVersion.Version.Build >= 22000;

    private readonly MediaController _media;
    private readonly SpotifyApiClient _api;
    private readonly AppSettings _settings;
    private readonly ContextMenuStrip _menu;
    private readonly Func<Task> _connectCallback;
    private readonly ToolTip _toolTip;
    private readonly System.Windows.Forms.Timer _tickTimer;
    private readonly System.Windows.Forms.Timer _animTimer;
    private readonly System.Windows.Forms.Timer _volumeDebounceTimer;

    private Theme _theme = Theme.FromSystem();
    private Font? _titleFont;
    private Font? _artistFont;
    private Font? _albumFont;
    private Font? _smallFont;
    private Font? _sectionFont;
    private Font? _footerFont;

    private Rectangle _headerRect;
    private Rectangle _coverRect;
    private Rectangle _textRect;
    private Rectangle _progressRect;
    private Rectangle _progressHitRect;
    private Rectangle _timeRect;
    private Rectangle _shuffleRect;
    private Rectangle _prevRect;
    private Rectangle _playRect;
    private Rectangle _nextRect;
    private Rectangle _repeatRect;
    private Rectangle _muteRect;
    private Rectangle _volumeRect;
    private Rectangle _volumeHitRect;
    private Rectangle _volumeLabelRect;
    private Rectangle _dividerRect;
    private Rectangle _playlistsHeaderRect;
    private Rectangle _refreshPlaylistsRect;
    private Rectangle _playlistsListRect;
    private Rectangle _connectButtonRect;
    private Rectangle _footerRect;
    private Rectangle _openSpotifyRect;
    private Rectangle _menuRect;

    private List<DisplayRow> _rows = new();

    private HitArea _hover = HitArea.None;
    private HitArea _pressed = HitArea.None;
    private HitArea _toolTipArea = HitArea.None;
    private int _hoverRowIndex = -1;
    private int _pressedRowIndex = -1;

    private bool _scrubbingProgress;
    private bool _scrubbingVolume;
    private bool _menuOpen;

    // Spotify in-app volume (0..100) synced via Spotify Web API.
    private int _volumePercent = 100;
    private int _preMuteVolume = 50;
    private int? _pendingVolumePercent;
    private bool _supportsVolume;
    private DateTime _lastVolumeEditUtc = DateTime.MinValue;
    private DateTime _lastApiPollUtc = DateTime.MinValue;

    // Playlist state
    private int _scrollOffset;
    private bool _loadingPlaylists;
    private string? _statusMessage;

    // Open slide/fade animation state.
    private int _animStartY;
    private int _animTargetY;
    private long _animStartTicks;
    private const int AnimDurationMs = 130;

    public FlyoutForm(
        MediaController media,
        SpotifyApiClient api,
        AppSettings settings,
        ContextMenuStrip menu,
        Func<Task> connectCallback)
    {
        _media = media;
        _api = api;
        _settings = settings;
        _menu = menu;
        _connectCallback = connectCallback;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        Text = Strings.AppName;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _toolTip = new ToolTip { InitialDelay = 450, ReshowDelay = 150, AutoPopDelay = 6000 };

        _tickTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _tickTimer.Tick += OnTick;

        _animTimer = new System.Windows.Forms.Timer { Interval = 15 };
        _animTimer.Tick += OnAnimationTick;

        _volumeDebounceTimer = new System.Windows.Forms.Timer { Interval = 120 };
        _volumeDebounceTimer.Tick += OnVolumeDebounceTick;

        _media.StateChanged += OnMediaChanged;
        _media.ThumbnailChanged += OnThumbnailChanged;
        _api.PlaylistsChanged += OnPlaylistsChanged;
        _api.ThumbnailLoaded += OnPlaylistThumbnailLoaded;
        _menu.Closed += OnContextMenuClosed;
    }

    public DateTime LastDeactivatedUtc { get; private set; } = DateTime.MinValue;

    private float DpiScale => DeviceDpi / 96f;

    private int S(float value) => (int)Math.Round(value * DpiScale);

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW;
            if (!IsWindows11)
            {
                cp.ClassStyle |= CS_DROPSHADOW;
            }

            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyDwmAttributes();
        UpdateMetrics();
        RebuildRows();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateMetrics();
        if (Visible)
        {
            PositionFlyout(animate: false);
        }

        Invalidate();
    }

    public void ApplyTheme()
    {
        _theme = Theme.FromSystem();
        if (IsHandleCreated)
        {
            ApplyDwmAttributes();
        }

        Invalidate();
    }

    public void ToggleFlyout()
    {
        if (Visible)
        {
            CloseFlyout();
        }
        else
        {
            OpenFlyout();
        }
    }

    public void OpenFlyout()
    {
        _statusMessage = null;
        UpdateMetrics();
        RebuildRows();
        PositionFlyout(animate: true);

        Show();
        SetForegroundWindow(Handle);
        Activate();
        _tickTimer.Start();
        Invalidate();

        if (_api.IsConnected)
        {
            _ = SyncSpotifyApiAsync(refreshPlaylistsIfEmpty: true);
        }
    }

    public void CloseFlyout()
    {
        _animTimer.Stop();
        _tickTimer.Stop();
        _scrubbingProgress = false;
        _scrubbingVolume = false;
        _pressed = HitArea.None;
        _hover = HitArea.None;
        _hoverRowIndex = -1;
        _pressedRowIndex = -1;

        if (Visible)
        {
            Hide();
        }
    }

    public void RefreshLayoutAndData()
    {
        UpdateMetrics();
        RebuildRows();
        if (Visible)
        {
            PositionFlyout(animate: false);
            if (_api.IsConnected)
            {
                _ = SyncSpotifyApiAsync(refreshPlaylistsIfEmpty: true);
            }

            Invalidate();
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        LastDeactivatedUtc = DateTime.UtcNow;
        if (!_menuOpen)
        {
            CloseFlyout();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        PlaybackState state = _media.State;
        switch (e.KeyCode)
        {
            case Keys.Escape:
                CloseFlyout();
                e.Handled = true;
                break;

            case Keys.Space:
            case Keys.K:
                if (state.HasSession)
                {
                    _ = _media.TogglePlayPauseAsync();
                }
                else
                {
                    SpotifyLauncher.Open();
                }

                e.Handled = true;
                break;

            case Keys.Left:
                if (e.Control && state.HasSession && state.CanPrevious)
                {
                    _ = _media.PreviousAsync();
                }
                else if (state.HasSession && state.CanSeek && state.Duration > TimeSpan.Zero)
                {
                    TimeSpan target = state.CurrentPosition - TimeSpan.FromSeconds(5);
                    _ = _media.SeekAsync(target < TimeSpan.Zero ? TimeSpan.Zero : target);
                }

                e.Handled = true;
                break;

            case Keys.Right:
                if (e.Control && state.HasSession && state.CanNext)
                {
                    _ = _media.NextAsync();
                }
                else if (state.HasSession && state.CanSeek && state.Duration > TimeSpan.Zero)
                {
                    TimeSpan target = state.CurrentPosition + TimeSpan.FromSeconds(5);
                    _ = _media.SeekAsync(target > state.Duration ? state.Duration : target);
                }

                e.Handled = true;
                break;

            case Keys.Up:
                AdjustSpotifyVolume(5);
                e.Handled = true;
                break;

            case Keys.Down:
                AdjustSpotifyVolume(-5);
                e.Handled = true;
                break;

            case Keys.M:
                ToggleMute();
                e.Handled = true;
                break;
        }
    }

    private void OnContextMenuClosed(object? sender, ToolStripDropDownClosedEventArgs e)
    {
        _menuOpen = false;
        if (Visible && !ContainsFocus)
        {
            CloseFlyout();
        }
    }

    private void OnMediaChanged(object? sender, EventArgs e)
    {
        if (Visible)
        {
            Invalidate();
            if (_api.IsConnected)
            {
                _ = SyncSpotifyApiAsync(refreshPlaylistsIfEmpty: false);
            }
        }
    }

    private void OnThumbnailChanged(object? sender, EventArgs e)
    {
        if (Visible)
        {
            Invalidate(_coverRect);
        }
    }

    private void OnPlaylistsChanged(object? sender, EventArgs e)
    {
        _scrollOffset = 0;
        RebuildRows();
        if (Visible)
        {
            Invalidate();
        }
    }

    private void OnPlaylistThumbnailLoaded(object? sender, EventArgs e)
    {
        if (!IsHandleCreated || IsDisposed || !Visible)
        {
            return;
        }

        try
        {
            BeginInvoke(() =>
            {
                if (Visible && !IsDisposed)
                {
                    Invalidate(_playlistsListRect);
                }
            });
        }
        catch
        {
            // Form may be closing.
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!Visible)
        {
            return;
        }

        if (_media.State.IsPlaying && !_scrubbingProgress)
        {
            Invalidate(_progressHitRect);
            Invalidate(_timeRect);
        }

        if (_api.IsConnected && (DateTime.UtcNow - _lastApiPollUtc).TotalSeconds >= 2.5)
        {
            _ = SyncSpotifyApiAsync(refreshPlaylistsIfEmpty: false);
        }
    }

    private async Task SyncSpotifyApiAsync(bool refreshPlaylistsIfEmpty)
    {
        if (!_api.IsConnected)
        {
            return;
        }

        _lastApiPollUtc = DateTime.UtcNow;

        SpotifyPlaybackInfo? info = await _api.RefreshPlaybackAsync();
        if (info is not null && !IsDisposed)
        {
            _supportsVolume = info.SupportsVolume;
            if (!_scrubbingVolume && (DateTime.UtcNow - _lastVolumeEditUtc).TotalMilliseconds > 1200)
            {
                _volumePercent = info.VolumePercent;
                if (_volumePercent > 0)
                {
                    _preMuteVolume = _volumePercent;
                }
            }

            if (Visible)
            {
                Invalidate();
            }
        }

        if (refreshPlaylistsIfEmpty && _api.Playlists.Count == 0 && !_loadingPlaylists)
        {
            await RefreshPlaylistsAsync();
        }
    }

    private async Task RefreshPlaylistsAsync()
    {
        if (!_api.IsConnected || _loadingPlaylists)
        {
            return;
        }

        _loadingPlaylists = true;
        _statusMessage = null;
        if (Visible) Invalidate(_playlistsListRect);

        try
        {
            await _api.RefreshPlaylistsAsync();
        }
        finally
        {
            _loadingPlaylists = false;
            if (Visible && !IsDisposed)
            {
                Invalidate();
            }
        }
    }

    private void RebuildRows()
    {
        IReadOnlyList<SpotifyPlaylist> playlists = _api.Playlists;
        var byId = playlists.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<DisplayRow>();

        foreach (PlaylistFolder folder in _settings.Folders)
        {
            var members = new List<SpotifyPlaylist>();
            foreach (string id in folder.PlaylistIds)
            {
                if (byId.TryGetValue(id, out SpotifyPlaylist? pl) && assigned.Add(id))
                {
                    members.Add(pl);
                }
            }

            rows.Add(new DisplayRow(folder, null, false));
            if (!folder.Collapsed)
            {
                foreach (SpotifyPlaylist pl in members)
                {
                    rows.Add(new DisplayRow(folder, pl, true));
                }
            }
        }

        foreach (SpotifyPlaylist pl in playlists)
        {
            if (!assigned.Contains(pl.Id))
            {
                rows.Add(new DisplayRow(null, pl, false));
            }
        }

        _rows = rows;
        int maxScroll = Math.Max(0, _rows.Count * S(PlaylistRowHeight) - _playlistsListRect.Height);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, maxScroll);
    }

    private void ApplyDwmAttributes()
    {
        try
        {
            int dark = _theme.IsLight ? 0 : 1;
            DwmSetWindowAttribute(Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            int corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        catch
        {
            // Ignore on older Windows 10 builds.
        }
    }

    #region Layout & positioning

    private void UpdateMetrics()
    {
        int width = S(FlyoutWidth);
        int mx = S(MarginX);
        int innerW = width - 2 * mx;

        int cover = S(CoverSize);
        int y = S(MarginTop);

        _coverRect = new Rectangle(mx, y, cover, cover);
        _textRect = new Rectangle(_coverRect.Right + S(14), y, innerW - cover - S(14), cover);
        _headerRect = new Rectangle(mx, y, innerW, cover);

        y = _headerRect.Bottom + S(16);

        int progH = Math.Max(4, S(4));
        _progressRect = new Rectangle(mx, y, innerW, progH);
        _progressHitRect = Rectangle.Inflate(_progressRect, 0, S(8));

        y = _progressRect.Bottom + S(6);
        _timeRect = new Rectangle(mx, y, innerW, S(16));

        y = _timeRect.Bottom + S(10);

        // Centered transport row: [Shuffle]  [Prev]  [Play/Pause]  [Next]  [Repeat]
        int playSize = S(42);
        int sideBtn = S(34);
        int smallBtn = S(30);
        int gap = S(10);

        int rowWidth = smallBtn + gap + sideBtn + gap + playSize + gap + sideBtn + gap + smallBtn;
        int rowX = (width - rowWidth) / 2;
        int rowCenterY = y + playSize / 2;

        _shuffleRect = new Rectangle(rowX, rowCenterY - smallBtn / 2, smallBtn, smallBtn);
        _prevRect = new Rectangle(_shuffleRect.Right + gap, rowCenterY - sideBtn / 2, sideBtn, sideBtn);
        _playRect = new Rectangle(_prevRect.Right + gap, y, playSize, playSize);
        _nextRect = new Rectangle(_playRect.Right + gap, rowCenterY - sideBtn / 2, sideBtn, sideBtn);
        _repeatRect = new Rectangle(_nextRect.Right + gap, rowCenterY - smallBtn / 2, smallBtn, smallBtn);

        y = _playRect.Bottom + S(14);

        // Spotify in-app volume row: [Mute icon] [====Slider====] [100%]
        int muteSize = S(28);
        int labelW = S(38);
        int volRowH = S(28);
        _muteRect = new Rectangle(mx, y, muteSize, volRowH);
        _volumeLabelRect = new Rectangle(width - mx - labelW, y, labelW, volRowH);

        int volLeft = _muteRect.Right + S(8);
        int volRight = _volumeLabelRect.Left - S(8);
        int volH = Math.Max(4, S(4));
        _volumeRect = new Rectangle(volLeft, y + (volRowH - volH) / 2, Math.Max(40, volRight - volLeft), volH);
        _volumeHitRect = Rectangle.Inflate(_volumeRect, 0, S(10));

        y += volRowH + S(10);

        // Divider + Playlists / Connect section
        _dividerRect = new Rectangle(mx, y, innerW, 1);
        y += S(10);

        if (_api.IsConnected)
        {
            _playlistsHeaderRect = new Rectangle(mx, y, innerW - S(28), S(24));
            _refreshPlaylistsRect = new Rectangle(width - mx - S(26), y - S(1), S(26), S(26));
            y += S(28);

            _playlistsListRect = new Rectangle(mx, y, innerW, S(PlaylistListHeight));
            _connectButtonRect = Rectangle.Empty;
            y = _playlistsListRect.Bottom + S(12);
        }
        else
        {
            _playlistsHeaderRect = new Rectangle(mx, y, innerW, S(22));
            _refreshPlaylistsRect = Rectangle.Empty;
            _playlistsListRect = new Rectangle(mx, y + S(24), innerW, S(ConnectCardHeight));
            int btnW = S(156);
            int btnH = S(32);
            _connectButtonRect = new Rectangle(
                mx + (innerW - btnW) / 2,
                _playlistsListRect.Bottom - btnH - S(12),
                btnW,
                btnH);
            y = _playlistsListRect.Bottom + S(12);
        }

        // Footer bar
        int footerH = S(FooterHeight);
        _footerRect = new Rectangle(0, y, width, footerH);
        _openSpotifyRect = new Rectangle(S(10), y + S(6), S(140), footerH - S(12));
        _menuRect = new Rectangle(width - S(10) - S(34), y + S(6), S(34), footerH - S(12));

        ClientSize = new Size(width, _footerRect.Bottom);

        _titleFont?.Dispose();
        _artistFont?.Dispose();
        _albumFont?.Dispose();
        _smallFont?.Dispose();
        _sectionFont?.Dispose();
        _footerFont?.Dispose();

        _titleFont = new Font("Segoe UI Semibold", S(14.5f), FontStyle.Regular, GraphicsUnit.Pixel);
        _artistFont = new Font("Segoe UI", S(12.5f), FontStyle.Regular, GraphicsUnit.Pixel);
        _albumFont = new Font("Segoe UI", S(11.5f), FontStyle.Regular, GraphicsUnit.Pixel);
        _smallFont = new Font("Segoe UI", S(11f), FontStyle.Regular, GraphicsUnit.Pixel);
        _sectionFont = new Font("Segoe UI Semibold", S(12f), FontStyle.Regular, GraphicsUnit.Pixel);
        _footerFont = new Font("Segoe UI Semibold", S(12f), FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private void PositionFlyout(bool animate)
    {
        Point cursor = Cursor.Position;
        Screen screen = Screen.FromPoint(cursor);
        Rectangle work = screen.WorkingArea;
        Rectangle bounds = screen.Bounds;
        int margin = S(12);

        int x;
        int y;

        if (work.Bottom < bounds.Bottom)
        {
            x = IsWindows11
                ? work.Right - Width - margin
                : Math.Clamp(cursor.X - Width / 2, work.Left + margin, work.Right - Width - margin);
            y = work.Bottom - Height - margin;
        }
        else if (work.Top > bounds.Top)
        {
            x = Math.Clamp(cursor.X - Width / 2, work.Left + margin, work.Right - Width - margin);
            y = work.Top + margin;
        }
        else if (work.Right < bounds.Right)
        {
            x = work.Right - Width - margin;
            y = Math.Clamp(cursor.Y - Height / 2, work.Top + margin, work.Bottom - Height - margin);
        }
        else
        {
            x = work.Left + margin;
            y = work.Bottom - Height - margin;
        }

        if (animate)
        {
            int slideOffset = work.Top > bounds.Top ? -S(10) : S(10);
            _animStartY = y + slideOffset;
            _animTargetY = y;
            _animStartTicks = Environment.TickCount64;
            Location = new Point(x, _animStartY);
            Opacity = 0.01;
            _animTimer.Start();
        }
        else
        {
            Location = new Point(x, y);
            Opacity = 1.0;
        }
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        double elapsed = Environment.TickCount64 - _animStartTicks;
        double t = Math.Clamp(elapsed / AnimDurationMs, 0.0, 1.0);
        double ease = 1.0 - Math.Pow(1.0 - t, 3.0);

        int y = (int)Math.Round(_animStartY + (_animTargetY - _animStartY) * ease);
        Location = new Point(Location.X, y);
        Opacity = Math.Clamp(0.2 + 0.8 * ease, 0.0, 1.0);

        if (t >= 1.0)
        {
            _animTimer.Stop();
            Location = new Point(Location.X, _animTargetY);
            Opacity = 1.0;
        }
    }

    #endregion

    #region Mouse & interaction

    private HitArea HitTest(Point p, out int rowIndex)
    {
        rowIndex = -1;
        PlaybackState state = _media.State;

        if (_playRect.Contains(p)) return HitArea.PlayPause;
        if (_prevRect.Contains(p)) return HitArea.Previous;
        if (_nextRect.Contains(p)) return HitArea.Next;
        if (_shuffleRect.Contains(p)) return HitArea.Shuffle;
        if (_repeatRect.Contains(p)) return HitArea.Repeat;
        if (_muteRect.Contains(p)) return HitArea.Mute;
        if (_volumeHitRect.Contains(p)) return HitArea.Volume;
        if (_openSpotifyRect.Contains(p)) return HitArea.OpenSpotify;
        if (_menuRect.Contains(p)) return HitArea.Menu;

        if (!_refreshPlaylistsRect.IsEmpty && _refreshPlaylistsRect.Contains(p))
        {
            return HitArea.RefreshPlaylists;
        }

        if (!_connectButtonRect.IsEmpty && _connectButtonRect.Contains(p))
        {
            return HitArea.ConnectButton;
        }

        if (_api.IsConnected && _playlistsListRect.Contains(p) && _rows.Count > 0)
        {
            int rowH = S(PlaylistRowHeight);
            int idx = (p.Y - _playlistsListRect.Top + _scrollOffset) / rowH;
            if (idx >= 0 && idx < _rows.Count)
            {
                rowIndex = idx;
                return HitArea.PlaylistRow;
            }
        }

        if (state.HasSession && state.CanSeek && state.Duration > TimeSpan.Zero && _progressHitRect.Contains(p))
        {
            return HitArea.Progress;
        }

        if (_headerRect.Contains(p)) return HitArea.Header;

        return HitArea.None;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Right)
        {
            HitArea rightArea = HitTest(e.Location, out int rightRow);
            if (rightArea == HitArea.PlaylistRow && rightRow >= 0 && rightRow < _rows.Count)
            {
                ShowRowContextMenu(_rows[rightRow], e.Location);
            }

            return;
        }

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        HitArea area = HitTest(e.Location, out int rowIdx);
        _pressed = area;
        _pressedRowIndex = rowIdx;
        Capture = true;

        if (area == HitArea.Progress)
        {
            _scrubbingProgress = true;
            SeekToMouseX(e.X);
        }
        else if (area == HitArea.Volume && CanControlSpotifyVolume)
        {
            _scrubbingVolume = true;
            SetVolumeFromMouseX(e.X);
        }

        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_scrubbingProgress)
        {
            SeekToMouseX(e.X);
            return;
        }

        if (_scrubbingVolume)
        {
            SetVolumeFromMouseX(e.X);
            return;
        }

        HitArea hover = HitTest(e.Location, out int rowIdx);
        if (hover != _hover || rowIdx != _hoverRowIndex)
        {
            _hover = hover;
            _hoverRowIndex = rowIdx;
            Cursor = hover is HitArea.None ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }

        UpdateToolTip(hover);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        Capture = false;
        HitArea pressed = _pressed;
        int pressedRow = _pressedRowIndex;
        _pressed = HitArea.None;
        _pressedRowIndex = -1;

        if (_scrubbingProgress)
        {
            _scrubbingProgress = false;
            SeekToMouseX(e.X);
            Invalidate();
            return;
        }

        if (_scrubbingVolume)
        {
            _scrubbingVolume = false;
            SetVolumeFromMouseX(e.X);
            Invalidate();
            return;
        }

        Invalidate();

        if (HitTest(e.Location, out int currentRow) != pressed || currentRow != pressedRow)
        {
            return;
        }

        PlaybackState state = _media.State;
        switch (pressed)
        {
            case HitArea.Header:
            case HitArea.OpenSpotify:
                SpotifyLauncher.Open();
                CloseFlyout();
                break;

            case HitArea.PlayPause:
                if (state.HasSession)
                {
                    _ = _media.TogglePlayPauseAsync();
                }
                else
                {
                    SpotifyLauncher.Open();
                    CloseFlyout();
                }

                break;

            case HitArea.Previous:
                if (state.HasSession && state.CanPrevious)
                {
                    _ = _media.PreviousAsync();
                }

                break;

            case HitArea.Next:
                if (state.HasSession && state.CanNext)
                {
                    _ = _media.NextAsync();
                }

                break;

            case HitArea.Shuffle:
                _ = ToggleShuffleAsync();
                break;

            case HitArea.Repeat:
                _ = CycleRepeatAsync();
                break;

            case HitArea.Mute:
                ToggleMute();
                break;

            case HitArea.RefreshPlaylists:
                _ = RefreshPlaylistsAsync();
                break;

            case HitArea.PlaylistRow:
                if (pressedRow >= 0 && pressedRow < _rows.Count)
                {
                    DisplayRow row = _rows[pressedRow];
                    if (row.Playlist is { } pl)
                    {
                        _ = PlaySelectedPlaylistAsync(pl);
                    }
                    else if (row.Folder is { } folder)
                    {
                        folder.Collapsed = !folder.Collapsed;
                        _settings.Save();
                        RebuildRows();
                        Invalidate(_playlistsListRect);
                    }
                }

                break;

            case HitArea.ConnectButton:
                CloseFlyout();
                _ = _connectCallback();
                break;

            case HitArea.Menu:
                _menuOpen = true;
                _menu.Show(this, new Point(_menuRect.Right - _menu.Width, _menuRect.Top - _menu.Height));
                break;
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_scrubbingProgress && !_scrubbingVolume)
        {
            _hover = HitArea.None;
            _hoverRowIndex = -1;
            _toolTipArea = HitArea.None;
            _toolTip.Hide(this);
            Invalidate();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        if (_api.IsConnected && _playlistsListRect.Contains(e.Location) && _rows.Count > 0)
        {
            int maxScroll = Math.Max(0, _rows.Count * S(PlaylistRowHeight) - _playlistsListRect.Height);
            int step = S(PlaylistRowHeight);
            _scrollOffset = Math.Clamp(_scrollOffset - Math.Sign(e.Delta) * step, 0, maxScroll);
            Invalidate(_playlistsListRect);
            return;
        }

        int delta = Math.Sign(e.Delta) * 5;
        AdjustSpotifyVolume(delta);
    }

    private void ShowRowContextMenu(DisplayRow row, Point location)
    {
        var menu = new ContextMenuStrip();

        if (row.Playlist is { } pl)
        {
            menu.Items.Add(new ToolStripMenuItem(Strings.MenuPlay, null, (_, _) => _ = PlaySelectedPlaylistAsync(pl)));

            var moveMenu = new ToolStripMenuItem(Strings.FolderMoveTo);
            foreach (PlaylistFolder folder in _settings.Folders)
            {
                string folderName = folder.Name.Replace("&", "&&");
                var folderItem = new ToolStripMenuItem(folderName, null, (_, _) => MovePlaylistToFolder(pl.Id, folder))
                {
                    Checked = folder.PlaylistIds.Contains(pl.Id),
                };
                moveMenu.DropDownItems.Add(folderItem);
            }

            if (_settings.Folders.Count > 0)
            {
                moveMenu.DropDownItems.Add(new ToolStripSeparator());
            }

            moveMenu.DropDownItems.Add(new ToolStripMenuItem(Strings.FolderNew, null, (_, _) => CreateFolderAndMove(pl.Id)));
            menu.Items.Add(moveMenu);

            if (row.Folder is not null)
            {
                menu.Items.Add(new ToolStripMenuItem(Strings.FolderRemoveFrom, null, (_, _) => MovePlaylistToFolder(pl.Id, null)));
            }
        }
        else if (row.Folder is { } folder)
        {
            menu.Items.Add(new ToolStripMenuItem(Strings.FolderRename, null, (_, _) => RenameFolder(folder)));
            menu.Items.Add(new ToolStripMenuItem(Strings.FolderDelete, null, (_, _) => DeleteFolder(folder)));
        }

        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            menu.Dispose();
            if (Visible && !ContainsFocus)
            {
                CloseFlyout();
            }
        };

        _menuOpen = true;
        menu.Show(this, location);
    }

    private void MovePlaylistToFolder(string playlistId, PlaylistFolder? target)
    {
        foreach (PlaylistFolder f in _settings.Folders)
        {
            f.PlaylistIds.Remove(playlistId);
        }

        if (target is not null && !target.PlaylistIds.Contains(playlistId))
        {
            target.PlaylistIds.Add(playlistId);
            target.Collapsed = false;
        }

        _settings.Save();
        RebuildRows();
        Invalidate(_playlistsListRect);
    }

    private void CreateFolderAndMove(string playlistId)
    {
        _menuOpen = true;
        try
        {
            string? name = PromptFolderName(string.Empty);
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            PlaylistFolder? existing = _settings.Folders.FirstOrDefault(f =>
                string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new PlaylistFolder { Name = name };
                _settings.Folders.Add(existing);
            }

            MovePlaylistToFolder(playlistId, existing);
        }
        finally
        {
            _menuOpen = false;
            if (Visible)
            {
                SetForegroundWindow(Handle);
                Activate();
            }
        }
    }

    private void RenameFolder(PlaylistFolder folder)
    {
        _menuOpen = true;
        try
        {
            string? name = PromptFolderName(folder.Name);
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            folder.Name = name;
            _settings.Save();
            RebuildRows();
            Invalidate(_playlistsListRect);
        }
        finally
        {
            _menuOpen = false;
            if (Visible)
            {
                SetForegroundWindow(Handle);
                Activate();
            }
        }
    }

    private void DeleteFolder(PlaylistFolder folder)
    {
        _settings.Folders.Remove(folder);
        _settings.Save();
        RebuildRows();
        Invalidate(_playlistsListRect);
    }

    private string? PromptFolderName(string initialValue)
    {
        using var dlg = new Form
        {
            Text = Strings.FolderPromptTitle,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(320, 128),
            BackColor = _theme.Background,
            ForeColor = _theme.Foreground,
            Font = new Font("Segoe UI", 9.5f),
            TopMost = true,
        };

        var label = new Label
        {
            Text = Strings.FolderPromptLabel,
            Location = new Point(16, 14),
            Size = new Size(288, 20),
        };

        var box = new TextBox
        {
            Text = initialValue,
            Location = new Point(16, 38),
            Size = new Size(288, 26),
            BackColor = _theme.ControlBg,
            ForeColor = _theme.Foreground,
            BorderStyle = BorderStyle.FixedSingle,
        };

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(140, 80),
            Size = new Size(78, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.Accent,
            ForeColor = Color.White,
        };
        ok.FlatAppearance.BorderSize = 0;

        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(226, 80),
            Size = new Size(78, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.ControlBg,
            ForeColor = _theme.Foreground,
        };
        cancel.FlatAppearance.BorderColor = _theme.Border;

        dlg.Controls.AddRange(new Control[] { label, box, ok, cancel });
        dlg.AcceptButton = ok;
        dlg.CancelButton = cancel;

        return dlg.ShowDialog(this) == DialogResult.OK ? box.Text.Trim() : null;
    }

    private bool CanControlSpotifyVolume =>
        _api.IsConnected && (_api.LastPlayback is null || (_api.LastPlayback.HasDevice && _api.LastPlayback.SupportsVolume));

    private async Task ToggleShuffleAsync()
    {
        if (_api.IsConnected)
        {
            bool current = _api.LastPlayback?.ShuffleState ?? _media.State.IsShuffleActive;
            ApiActionResult result = await _api.SetShuffleAsync(!current);
            ShowApiFeedbackIfNeeded(result);
            await SyncSpotifyApiAsync(refreshPlaylistsIfEmpty: false);
        }
        else if (_media.State.HasSession && _media.State.CanShuffle)
        {
            await _media.ToggleShuffleAsync();
        }
    }

    private async Task CycleRepeatAsync()
    {
        if (_api.IsConnected)
        {
            string current = _api.LastPlayback?.RepeatState ?? "off";
            string next = current switch
            {
                "off" => "context",
                "context" => "track",
                _ => "off",
            };
            ApiActionResult result = await _api.SetRepeatAsync(next);
            ShowApiFeedbackIfNeeded(result);
            await SyncSpotifyApiAsync(refreshPlaylistsIfEmpty: false);
        }
        else if (_media.State.HasSession && _media.State.CanRepeat)
        {
            await _media.CycleRepeatAsync();
        }
    }

    private async Task PlaySelectedPlaylistAsync(SpotifyPlaylist playlist)
    {
        _statusMessage = $"Starting \"{playlist.Name}\"...";
        Invalidate(_playlistsListRect);

        ApiActionResult result = await _api.PlayPlaylistAsync(playlist.Uri);
        if (result == ApiActionResult.NoActiveDevice)
        {
            SpotifyLauncher.Open();
            _statusMessage = "Opening Spotify – click playlist again once active.";
            Invalidate(_playlistsListRect);
            return;
        }

        if (result == ApiActionResult.Ok)
        {
            _statusMessage = null;
            await Task.Delay(350);
            await SyncSpotifyApiAsync(refreshPlaylistsIfEmpty: false);
        }
        else
        {
            ShowApiFeedbackIfNeeded(result);
        }
    }

    private void ShowApiFeedbackIfNeeded(ApiActionResult result)
    {
        _statusMessage = result switch
        {
            ApiActionResult.PremiumRequired => "Spotify Premium is required for remote playback control.",
            ApiActionResult.NoActiveDevice => "No active Spotify device found. Open Spotify first.",
            ApiActionResult.Failed => "Spotify API request failed. Try refreshing.",
            _ => _statusMessage,
        };
        if (Visible)
        {
            Invalidate();
        }
    }

    private void SeekToMouseX(int mouseX)
    {
        PlaybackState state = _media.State;
        if (!state.HasSession || state.Duration <= TimeSpan.Zero)
        {
            return;
        }

        double fraction = Math.Clamp((mouseX - _progressRect.Left) / (double)_progressRect.Width, 0, 1);
        TimeSpan target = TimeSpan.FromTicks((long)(state.Duration.Ticks * fraction));
        _ = _media.SeekAsync(target);
        Invalidate(_progressHitRect);
        Invalidate(_timeRect);
    }

    private void SetVolumeFromMouseX(int mouseX)
    {
        if (!CanControlSpotifyVolume)
        {
            return;
        }

        float fraction = Math.Clamp((mouseX - _volumeRect.Left) / (float)_volumeRect.Width, 0f, 1f);
        QueueSpotifyVolume((int)Math.Round(fraction * 100));
    }

    private void AdjustSpotifyVolume(int delta)
    {
        if (!CanControlSpotifyVolume)
        {
            return;
        }

        QueueSpotifyVolume(_volumePercent + delta);
    }

    private void ToggleMute()
    {
        if (!CanControlSpotifyVolume)
        {
            return;
        }

        if (_volumePercent > 0)
        {
            _preMuteVolume = _volumePercent;
            QueueSpotifyVolume(0);
        }
        else
        {
            QueueSpotifyVolume(_preMuteVolume > 0 ? _preMuteVolume : 50);
        }
    }

    private void QueueSpotifyVolume(int percent)
    {
        int clamped = Math.Clamp(percent, 0, 100);
        _volumePercent = clamped;
        if (clamped > 0)
        {
            _preMuteVolume = clamped;
        }

        _pendingVolumePercent = clamped;
        _lastVolumeEditUtc = DateTime.UtcNow;
        Invalidate(Rectangle.Union(_muteRect, _volumeLabelRect));

        _volumeDebounceTimer.Stop();
        _volumeDebounceTimer.Start();
    }

    private async void OnVolumeDebounceTick(object? sender, EventArgs e)
    {
        _volumeDebounceTimer.Stop();
        if (_pendingVolumePercent is not int target)
        {
            return;
        }

        _pendingVolumePercent = null;
        ApiActionResult result = await _api.SetVolumeAsync(target);
        ShowApiFeedbackIfNeeded(result);
    }

    private void UpdateToolTip(HitArea area)
    {
        if (area == _toolTipArea)
        {
            return;
        }

        _toolTipArea = area;
        PlaybackState state = _media.State;
        string? text = area switch
        {
            HitArea.Header => state.HasSession ? Strings.TipOpenSpotify : Strings.FlyoutClickToOpen,
            HitArea.Progress => Strings.TipSeek(FormatTime(state.CurrentPosition), FormatTime(state.Duration)),
            HitArea.Shuffle => Strings.TipShuffle,
            HitArea.Previous => Strings.TipPrevious,
            HitArea.PlayPause => !state.HasSession ? Strings.TipOpenSpotify : state.IsPlaying ? Strings.TipPause : Strings.TipPlay,
            HitArea.Next => Strings.TipNext,
            HitArea.Repeat => Strings.TipRepeat,
            HitArea.Mute => !CanControlSpotifyVolume ? Strings.TipVolumeUnavailable : (_volumePercent == 0 ? Strings.TipUnmute : Strings.TipMute),
            HitArea.Volume => CanControlSpotifyVolume ? Strings.TipVolume : Strings.TipVolumeUnavailable,
            HitArea.RefreshPlaylists => Strings.TipRefreshPlaylists,
            HitArea.OpenSpotify => Strings.TipOpenSpotify,
            HitArea.Menu => Strings.TipMenu,
            _ => null,
        };

        _toolTip.SetToolTip(this, text);
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    #endregion

    #region Painting

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(_theme.Background);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        PlaybackState state = _media.State;

        DrawHeader(g, state);
        DrawProgress(g, state);
        DrawTransportControls(g, state);
        DrawVolumeRow(g);
        DrawPlaylistsSection(g);
        DrawFooter(g);

        using var borderPen = new Pen(_theme.Border, 1f);
        g.DrawRectangle(borderPen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    private void DrawHeader(Graphics g, PlaybackState state)
    {
        if (_hover == HitArea.Header || _pressed == HitArea.Header)
        {
            Color bg = _pressed == HitArea.Header ? _theme.Pressed : _theme.Hover;
            using var hoverBrush = new SolidBrush(bg);
            using GraphicsPath hoverPath = RoundedRect(Rectangle.Inflate(_headerRect, S(6), S(6)), S(8));
            g.FillPath(hoverBrush, hoverPath);
        }

        using GraphicsPath coverPath = RoundedRect(_coverRect, S(6));
        Image? cover = _media.Thumbnail;
        if (cover is not null)
        {
            GraphicsState saved = g.Save();
            g.SetClip(coverPath, CombineMode.Intersect);
            g.DrawImage(cover, _coverRect);
            g.Restore(saved);
        }
        else
        {
            DrawMusicPlaceholder(g, coverPath, _coverRect);
        }

        using var borderPen = new Pen(_theme.Border, 1f);
        g.DrawPath(borderPen, coverPath);

        if (_titleFont is null || _artistFont is null || _albumFont is null)
        {
            return;
        }

        string title = state.HasSession
            ? (string.IsNullOrWhiteSpace(state.Title) ? "Spotify" : state.Title)
            : Strings.FlyoutNotRunning;
        string artist = state.HasSession
            ? (string.IsNullOrWhiteSpace(state.Artist) ? (state.IsPlaying ? Strings.FlyoutPlaying : Strings.FlyoutPaused) : state.Artist)
            : Strings.FlyoutClickToOpen;
        string album = state.HasSession ? state.Album : string.Empty;

        using var format = new StringFormat(StringFormatFlags.NoWrap | StringFormatFlags.LineLimit)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            HotkeyPrefix = HotkeyPrefix.None,
        };

        float titleH = _titleFont.GetHeight(g);
        float artistH = _artistFont.GetHeight(g);
        float albumH = string.IsNullOrWhiteSpace(album) ? 0f : _albumFont.GetHeight(g);
        float totalH = titleH + artistH + (albumH > 0 ? albumH + S(2) : 0);
        float top = _textRect.Top + Math.Max(0, (_textRect.Height - totalH) / 2f);

        using var fgBrush = new SolidBrush(_theme.Foreground);
        using var secBrush = new SolidBrush(_theme.Secondary);
        using var terBrush = new SolidBrush(_theme.Tertiary);

        g.DrawString(title, _titleFont, fgBrush, new RectangleF(_textRect.Left, top, _textRect.Width, titleH + 1), format);
        g.DrawString(artist, _artistFont, secBrush, new RectangleF(_textRect.Left, top + titleH + S(1), _textRect.Width, artistH + 1), format);

        if (albumH > 0)
        {
            g.DrawString(album, _albumFont, terBrush, new RectangleF(_textRect.Left, top + titleH + artistH + S(3), _textRect.Width, albumH + 1), format);
        }
    }

    private void DrawProgress(Graphics g, PlaybackState state)
    {
        float radius = _progressRect.Height / 2f;
        using (var trackBrush = new SolidBrush(_theme.Track))
        using (GraphicsPath trackPath = RoundedRect(_progressRect, radius))
        {
            g.FillPath(trackBrush, trackPath);
        }

        bool hasDuration = state.HasSession && state.Duration > TimeSpan.Zero;
        if (hasDuration)
        {
            double fraction = Math.Clamp(state.CurrentPosition.TotalMilliseconds / state.Duration.TotalMilliseconds, 0, 1);
            int width = (int)Math.Round(_progressRect.Width * fraction);
            if (width > 0)
            {
                bool active = _hover == HitArea.Progress || _scrubbingProgress || state.IsPlaying;
                Color fillColor = active ? _theme.Accent : _theme.Secondary;
                var fillRect = new Rectangle(_progressRect.Left, _progressRect.Top, Math.Max(width, _progressRect.Height), _progressRect.Height);
                using var fillBrush = new SolidBrush(fillColor);
                using GraphicsPath fillPath = RoundedRect(fillRect, radius);
                g.FillPath(fillBrush, fillPath);
            }

            if ((_hover == HitArea.Progress || _scrubbingProgress) && state.CanSeek)
            {
                float knob = S(12);
                float kx = _progressRect.Left + width - knob / 2f;
                float ky = _progressRect.Top + _progressRect.Height / 2f - knob / 2f;
                using var knobBrush = new SolidBrush(_theme.Foreground);
                g.FillEllipse(knobBrush, kx, ky, knob, knob);
            }
        }

        if (_smallFont is null)
        {
            return;
        }

        string posText = hasDuration ? FormatTime(state.CurrentPosition) : "0:00";
        string durText = hasDuration ? FormatTime(state.Duration) : "0:00";
        using var timeBrush = new SolidBrush(hasDuration ? _theme.Secondary : _theme.Disabled);
        using var rightFmt = new StringFormat { Alignment = StringAlignment.Far, HotkeyPrefix = HotkeyPrefix.None };
        g.DrawString(posText, _smallFont, timeBrush, _timeRect);
        g.DrawString(durText, _smallFont, timeBrush, _timeRect, rightFmt);
    }

    private void DrawTransportControls(Graphics g, PlaybackState state)
    {
        bool hasSession = state.HasSession;
        bool apiConnected = _api.IsConnected;

        bool shuffleEnabled = apiConnected || (hasSession && state.CanShuffle);
        bool shuffleActive = apiConnected
            ? (_api.LastPlayback?.ShuffleState ?? state.IsShuffleActive)
            : state.IsShuffleActive;

        DrawRoundButton(g, _shuffleRect, HitArea.Shuffle, shuffleEnabled,
            shuffleActive ? _theme.Accent : null,
            (gr, r, br) => DrawShuffleIcon(gr, r, br, shuffleActive));

        DrawRoundButton(g, _prevRect, HitArea.Previous, hasSession && state.CanPrevious, null, DrawPreviousIcon);

        bool playEnabled = !hasSession || state.CanPlayPause;
        DrawPlayButton(g, _playRect, playEnabled, hasSession && state.IsPlaying);

        DrawRoundButton(g, _nextRect, HitArea.Next, hasSession && state.CanNext, null, DrawNextIcon);

        bool repeatEnabled = apiConnected || (hasSession && state.CanRepeat);
        MediaPlaybackAutoRepeatMode repeatMode = apiConnected
            ? (_api.LastPlayback?.RepeatState switch
            {
                "track" => MediaPlaybackAutoRepeatMode.Track,
                "context" => MediaPlaybackAutoRepeatMode.List,
                _ => MediaPlaybackAutoRepeatMode.None,
            })
            : state.RepeatMode;

        bool repeatActive = repeatMode != MediaPlaybackAutoRepeatMode.None;
        DrawRoundButton(g, _repeatRect, HitArea.Repeat, repeatEnabled,
            repeatActive ? _theme.Accent : null,
            (gr, r, br) => DrawRepeatIcon(gr, r, br, repeatMode));
    }

    private void DrawPlayButton(Graphics g, Rectangle rect, bool enabled, bool isPlaying)
    {
        Color bg = !enabled ? _theme.Track
            : _pressed == HitArea.PlayPause ? _theme.AccentPressed
            : _hover == HitArea.PlayPause ? _theme.AccentHover
            : _theme.Accent;

        using var bgBrush = new SolidBrush(bg);
        g.FillEllipse(bgBrush, rect);

        float size = rect.Width * 0.38f;
        var iconRect = new RectangleF(rect.Left + (rect.Width - size) / 2f, rect.Top + (rect.Height - size) / 2f, size, size);
        using var fgBrush = new SolidBrush(Color.White);
        if (isPlaying)
        {
            DrawPauseIcon(g, iconRect, fgBrush);
        }
        else
        {
            iconRect.Offset(size * 0.06f, 0);
            DrawPlayIcon(g, iconRect, fgBrush);
        }
    }

    private void DrawRoundButton(
        Graphics g,
        Rectangle rect,
        HitArea area,
        bool enabled,
        Color? activeColor,
        Action<Graphics, RectangleF, Brush> drawIcon)
    {
        if (enabled && (_hover == area || _pressed == area))
        {
            Color bg = _pressed == area ? _theme.Pressed : _theme.Hover;
            using var bgBrush = new SolidBrush(bg);
            g.FillEllipse(bgBrush, rect);
        }

        float size = rect.Width * 0.44f;
        var iconRect = new RectangleF(rect.Left + (rect.Width - size) / 2f, rect.Top + (rect.Height - size) / 2f, size, size);
        Color fg = !enabled ? _theme.Disabled : (activeColor ?? _theme.Foreground);
        using var brush = new SolidBrush(fg);
        drawIcon(g, iconRect, brush);
    }

    private void DrawVolumeRow(Graphics g)
    {
        bool enabled = CanControlSpotifyVolume;

        if (enabled && (_hover == HitArea.Mute || _pressed == HitArea.Mute))
        {
            Color bg = _pressed == HitArea.Mute ? _theme.Pressed : _theme.Hover;
            using var bgBrush = new SolidBrush(bg);
            using GraphicsPath path = RoundedRect(_muteRect, S(6));
            g.FillPath(bgBrush, path);
        }

        float iconSize = Math.Min(_muteRect.Width, _muteRect.Height) * 0.56f;
        var speakerRect = new RectangleF(
            _muteRect.Left + (_muteRect.Width - iconSize) / 2f,
            _muteRect.Top + (_muteRect.Height - iconSize) / 2f,
            iconSize,
            iconSize);

        float level = enabled ? _volumePercent / 100f : 0f;
        using (var iconBrush = new SolidBrush(enabled ? _theme.Foreground : _theme.Disabled))
        {
            DrawSpeakerIcon(g, speakerRect, iconBrush, level, enabled && _volumePercent == 0);
        }

        float radius = _volumeRect.Height / 2f;
        using (var trackBrush = new SolidBrush(_theme.Track))
        using (GraphicsPath trackPath = RoundedRect(_volumeRect, radius))
        {
            g.FillPath(trackBrush, trackPath);
        }

        int fillW = (int)Math.Round(_volumeRect.Width * level);
        if (enabled && fillW > 0)
        {
            var fillRect = new Rectangle(_volumeRect.Left, _volumeRect.Top, Math.Max(fillW, _volumeRect.Height), _volumeRect.Height);
            using var fillBrush = new SolidBrush(_hover == HitArea.Volume || _scrubbingVolume ? _theme.Accent : _theme.Foreground);
            using GraphicsPath fillPath = RoundedRect(fillRect, radius);
            g.FillPath(fillBrush, fillPath);
        }

        if (enabled && (_hover == HitArea.Volume || _scrubbingVolume))
        {
            float knob = S(12);
            float kx = _volumeRect.Left + fillW - knob / 2f;
            float ky = _volumeRect.Top + _volumeRect.Height / 2f - knob / 2f;
            using var knobBrush = new SolidBrush(_theme.Foreground);
            g.FillEllipse(knobBrush, kx, ky, knob, knob);
        }

        if (_smallFont is not null)
        {
            string volText = enabled ? $"{_volumePercent}%" : "–";
            using var labelBrush = new SolidBrush(enabled ? _theme.Secondary : _theme.Disabled);
            using var fmt = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, HotkeyPrefix = HotkeyPrefix.None };
            g.DrawString(volText, _smallFont, labelBrush, _volumeLabelRect, fmt);
        }
    }

    private void DrawPlaylistsSection(Graphics g)
    {
        using (var divBrush = new SolidBrush(_theme.Border))
        {
            g.FillRectangle(divBrush, _dividerRect);
        }

        if (_sectionFont is not null)
        {
            using var hdrBrush = new SolidBrush(_theme.Foreground);
            using var fmt = new StringFormat { LineAlignment = StringAlignment.Center, HotkeyPrefix = HotkeyPrefix.None };
            g.DrawString(Strings.FlyoutPlaylists, _sectionFont, hdrBrush, _playlistsHeaderRect, fmt);
        }

        if (!_api.IsConnected)
        {
            using var cardBrush = new SolidBrush(_theme.ControlBg);
            using GraphicsPath cardPath = RoundedRect(_playlistsListRect, S(8));
            g.FillPath(cardBrush, cardPath);

            using var cardPen = new Pen(_theme.Border, 1f);
            g.DrawPath(cardPen, cardPath);

            if (_smallFont is not null)
            {
                var promptRect = new RectangleF(
                    _playlistsListRect.Left + S(14),
                    _playlistsListRect.Top + S(12),
                    _playlistsListRect.Width - S(28),
                    S(38));
                using var promptBrush = new SolidBrush(_theme.Secondary);
                using var fmt = new StringFormat { Alignment = StringAlignment.Center, HotkeyPrefix = HotkeyPrefix.None };
                g.DrawString(Strings.FlyoutConnectPrompt, _smallFont, promptBrush, promptRect, fmt);
            }

            Color btnColor = _pressed == HitArea.ConnectButton ? _theme.AccentPressed
                : _hover == HitArea.ConnectButton ? _theme.AccentHover
                : _theme.Accent;
            using var btnBrush = new SolidBrush(btnColor);
            using GraphicsPath btnPath = RoundedRect(_connectButtonRect, S(16));
            g.FillPath(btnBrush, btnPath);

            if (_footerFont is not null)
            {
                using var whiteBrush = new SolidBrush(Color.White);
                using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, HotkeyPrefix = HotkeyPrefix.None };
                g.DrawString(Strings.FlyoutConnectButton, _footerFont, whiteBrush, _connectButtonRect, fmt);
            }

            return;
        }

        // Refresh button
        if (_hover == HitArea.RefreshPlaylists || _pressed == HitArea.RefreshPlaylists)
        {
            Color bg = _pressed == HitArea.RefreshPlaylists ? _theme.Pressed : _theme.Hover;
            using var bgBrush = new SolidBrush(bg);
            using GraphicsPath path = RoundedRect(_refreshPlaylistsRect, S(6));
            g.FillPath(bgBrush, path);
        }

        float refSize = _refreshPlaylistsRect.Width * 0.48f;
        var refRect = new RectangleF(
            _refreshPlaylistsRect.Left + (_refreshPlaylistsRect.Width - refSize) / 2f,
            _refreshPlaylistsRect.Top + (_refreshPlaylistsRect.Height - refSize) / 2f,
            refSize,
            refSize);
        using (var refBrush = new SolidBrush(_theme.Secondary))
        {
            DrawRefreshIcon(g, refRect, refBrush);
        }

        if (_loadingPlaylists && _rows.Count == 0)
        {
            DrawCenteredMessage(g, Strings.FlyoutLoadingPlaylists);
            return;
        }

        if (_rows.Count == 0)
        {
            DrawCenteredMessage(g, _statusMessage ?? Strings.FlyoutNoPlaylists);
            return;
        }

        GraphicsState saved = g.Save();
        g.SetClip(_playlistsListRect);

        int rowH = S(PlaylistRowHeight);
        int thumbSize = S(28);
        string? activeUri = _api.LastPlayback?.ContextUri;
        bool hasScrollbar = _rows.Count * rowH > _playlistsListRect.Height;
        int contentRight = _playlistsListRect.Right - (hasScrollbar ? S(8) : 0);

        using var nameFmt = new StringFormat(StringFormatFlags.NoWrap | StringFormatFlags.LineLimit)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            HotkeyPrefix = HotkeyPrefix.None,
        };

        for (int i = 0; i < _rows.Count; i++)
        {
            int rowTop = _playlistsListRect.Top + i * rowH - _scrollOffset;
            if (rowTop + rowH < _playlistsListRect.Top || rowTop > _playlistsListRect.Bottom)
            {
                continue;
            }

            DisplayRow row = _rows[i];
            bool isHovered = _hover == HitArea.PlaylistRow && _hoverRowIndex == i;
            bool isPressed = _pressed == HitArea.PlaylistRow && _pressedRowIndex == i;

            if (row.Playlist is { } pl)
            {
                int indent = row.Indented ? S(14) : 0;
                bool isActive = string.Equals(pl.Uri, activeUri, StringComparison.OrdinalIgnoreCase);
                var rowRect = new Rectangle(_playlistsListRect.Left + indent, rowTop, contentRight - _playlistsListRect.Left - indent, rowH - S(2));

                if (isPressed || isHovered || isActive)
                {
                    Color rowBg = isPressed ? _theme.Pressed : (isHovered ? _theme.Hover : _theme.ControlBg);
                    using var rowBrush = new SolidBrush(rowBg);
                    using GraphicsPath rowPath = RoundedRect(rowRect, S(6));
                    g.FillPath(rowBrush, rowPath);
                }

                if (isActive)
                {
                    using var barBrush = new SolidBrush(_theme.Accent);
                    var bar = new RectangleF(rowRect.Left + S(2), rowRect.Top + S(8), S(3), rowRect.Height - S(16));
                    g.FillRectangle(barBrush, bar);
                }

                var thumbRect = new Rectangle(rowRect.Left + S(8), rowRect.Top + (rowRect.Height - thumbSize) / 2, thumbSize, thumbSize);
                using GraphicsPath thumbPath = RoundedRect(thumbRect, S(4));
                Image? thumb = _api.GetCachedThumbnail(pl.ImageUrl);
                if (thumb is not null)
                {
                    GraphicsState thumbState = g.Save();
                    g.SetClip(thumbPath, CombineMode.Intersect);
                    g.DrawImage(thumb, thumbRect);
                    g.Restore(thumbState);
                }
                else
                {
                    DrawMusicPlaceholder(g, thumbPath, thumbRect);
                }

                int textX = thumbRect.Right + S(10);
                int textW = rowRect.Right - textX - S(8);

                if (_artistFont is not null && _smallFont is not null)
                {
                    float nameH = _artistFont.GetHeight(g);
                    float subH = _smallFont.GetHeight(g);
                    float textTop = rowRect.Top + Math.Max(0, (rowRect.Height - (nameH + subH)) / 2f);

                    using var nameBrush = new SolidBrush(isActive ? _theme.Accent : _theme.Foreground);
                    using var subBrush = new SolidBrush(_theme.Tertiary);

                    string subText = pl.TrackCount is int count
                        ? Strings.FlyoutTracksCount(count)
                        : Strings.FlyoutPlaylistLabel;

                    g.DrawString(pl.Name, _artistFont, nameBrush, new RectangleF(textX, textTop, textW, nameH + 1), nameFmt);
                    g.DrawString(subText, _smallFont, subBrush, new RectangleF(textX, textTop + nameH, textW, subH + 1), nameFmt);
                }
            }
            else if (row.Folder is { } folder)
            {
                var rowRect = new Rectangle(_playlistsListRect.Left, rowTop, contentRight - _playlistsListRect.Left, rowH - S(2));
                bool containsActive = activeUri is not null
                    && _api.Playlists.Any(p => folder.PlaylistIds.Contains(p.Id)
                                               && string.Equals(p.Uri, activeUri, StringComparison.OrdinalIgnoreCase));

                if (isPressed || isHovered)
                {
                    Color rowBg = isPressed ? _theme.Pressed : _theme.Hover;
                    using var rowBrush = new SolidBrush(rowBg);
                    using GraphicsPath rowPath = RoundedRect(rowRect, S(6));
                    g.FillPath(rowBrush, rowPath);
                }

                int chevSize = S(10);
                var chevRect = new RectangleF(rowRect.Left + S(6), rowRect.Top + (rowRect.Height - chevSize) / 2f, chevSize, chevSize);
                using (var chevBrush = new SolidBrush(_theme.Secondary))
                {
                    DrawChevronIcon(g, chevRect, chevBrush, !folder.Collapsed);
                }

                var folderIconRect = new RectangleF(chevRect.Right + S(6), rowRect.Top + (rowRect.Height - S(18)) / 2f, S(18), S(18));
                using (var folderBrush = new SolidBrush(containsActive ? _theme.Accent : _theme.Secondary))
                {
                    DrawFolderIcon(g, folderIconRect, folderBrush);
                }

                int textX = (int)folderIconRect.Right + S(10);
                int textW = rowRect.Right - textX - S(8);
                int memberCount = _api.Playlists.Count(p => folder.PlaylistIds.Contains(p.Id));

                if (_sectionFont is not null && _smallFont is not null)
                {
                    float nameH = _sectionFont.GetHeight(g);
                    float subH = _smallFont.GetHeight(g);
                    float textTop = rowRect.Top + Math.Max(0, (rowRect.Height - (nameH + subH)) / 2f);

                    using var nameBrush = new SolidBrush(containsActive ? _theme.Accent : _theme.Foreground);
                    using var subBrush = new SolidBrush(_theme.Tertiary);

                    g.DrawString(folder.Name, _sectionFont, nameBrush, new RectangleF(textX, textTop, textW, nameH + 1), nameFmt);
                    g.DrawString(Strings.FlyoutFolderPlaylistsCount(memberCount), _smallFont, subBrush, new RectangleF(textX, textTop + nameH, textW, subH + 1), nameFmt);
                }
            }
        }

        if (hasScrollbar)
        {
            int totalH = _rows.Count * rowH;
            int barH = Math.Max(S(24), (int)((double)_playlistsListRect.Height * _playlistsListRect.Height / totalH));
            int maxScroll = totalH - _playlistsListRect.Height;
            int barY = _playlistsListRect.Top + (int)((double)_scrollOffset / maxScroll * (_playlistsListRect.Height - barH));
            var scrollRect = new RectangleF(_playlistsListRect.Right - S(4), barY, S(3), barH);
            using var scrollBrush = new SolidBrush(_theme.Track);
            using GraphicsPath scrollPath = RoundedRect(scrollRect, S(1.5f));
            g.FillPath(scrollBrush, scrollPath);
        }

        g.Restore(saved);

        if (!string.IsNullOrEmpty(_statusMessage) && _smallFont is not null)
        {
            var msgRect = new RectangleF(_playlistsListRect.Left, _playlistsListRect.Bottom - S(18), _playlistsListRect.Width, S(18));
            using var bgBrush = new SolidBrush(Color.FromArgb(220, _theme.ControlBg));
            g.FillRectangle(bgBrush, msgRect);
            using var msgBrush = new SolidBrush(_theme.Accent);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, HotkeyPrefix = HotkeyPrefix.None };
            g.DrawString(_statusMessage, _smallFont, msgBrush, msgRect, fmt);
        }
    }

    private void DrawCenteredMessage(Graphics g, string text)
    {
        if (_smallFont is null) return;
        using var brush = new SolidBrush(_theme.Secondary);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, HotkeyPrefix = HotkeyPrefix.None };
        g.DrawString(text, _smallFont, brush, _playlistsListRect, fmt);
    }

    private void DrawMusicPlaceholder(Graphics g, GraphicsPath clipPath, Rectangle rect)
    {
        using var bg = new SolidBrush(_theme.ControlBg);
        g.FillPath(bg, clipPath);

        using var accent = new SolidBrush(_theme.Accent);
        float w = rect.Width;
        float cx = rect.Left + w / 2f;
        float cy = rect.Top + w / 2f;
        float headR = w * 0.11f;
        float headX = cx - w * 0.07f;
        float headY = cy + w * 0.13f;
        g.FillEllipse(accent, headX - headR * 1.2f, headY - headR, headR * 2.4f, headR * 2f);
        float stemX = headX + headR;
        g.FillRectangle(accent, stemX - w * 0.03f, cy - w * 0.24f, w * 0.06f, headY - (cy - w * 0.24f));
        g.FillRectangle(accent, stemX - w * 0.03f, cy - w * 0.24f, w * 0.18f, w * 0.075f);
    }

    private void DrawFooter(Graphics g)
    {
        using (var footerBrush = new SolidBrush(_theme.Footer))
        {
            g.FillRectangle(footerBrush, _footerRect);
        }

        using (var linePen = new Pen(_theme.Border, 1f))
        {
            g.DrawLine(linePen, _footerRect.Left, _footerRect.Top, _footerRect.Right, _footerRect.Top);
        }

        if (_hover == HitArea.OpenSpotify || _pressed == HitArea.OpenSpotify)
        {
            Color bg = _pressed == HitArea.OpenSpotify ? _theme.Pressed : _theme.Hover;
            using var bgBrush = new SolidBrush(bg);
            using GraphicsPath path = RoundedRect(_openSpotifyRect, S(6));
            g.FillPath(bgBrush, path);
        }

        int dotSize = S(8);
        var dotRect = new RectangleF(_openSpotifyRect.Left + S(10), _openSpotifyRect.Top + (_openSpotifyRect.Height - dotSize) / 2f, dotSize, dotSize);
        using (var dotBrush = new SolidBrush(_theme.Accent))
        {
            g.FillEllipse(dotBrush, dotRect);
        }

        if (_footerFont is not null)
        {
            var textRect = new RectangleF(dotRect.Right + S(8), _openSpotifyRect.Top, _openSpotifyRect.Right - dotRect.Right - S(12), _openSpotifyRect.Height);
            using var textBrush = new SolidBrush(_theme.Foreground);
            using var fmt = new StringFormat { LineAlignment = StringAlignment.Center, HotkeyPrefix = HotkeyPrefix.None };
            g.DrawString(Strings.MenuOpenSpotify, _footerFont, textBrush, textRect, fmt);
        }

        if (_api.IsConnected && !string.IsNullOrWhiteSpace(_settings.UserDisplayName) && _smallFont is not null)
        {
            var userRect = new RectangleF(_openSpotifyRect.Right + S(6), _footerRect.Top, _menuRect.Left - _openSpotifyRect.Right - S(12), _footerRect.Height);
            using var userBrush = new SolidBrush(_theme.Tertiary);
            using var fmt = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, HotkeyPrefix = HotkeyPrefix.None };
            g.DrawString(_settings.UserDisplayName, _smallFont, userBrush, userRect, fmt);
        }

        if (_hover == HitArea.Menu || _pressed == HitArea.Menu)
        {
            Color bg = _pressed == HitArea.Menu ? _theme.Pressed : _theme.Hover;
            using var bgBrush = new SolidBrush(bg);
            using GraphicsPath path = RoundedRect(_menuRect, S(6));
            g.FillPath(bgBrush, path);
        }

        float dot = S(3);
        float cy = _menuRect.Top + _menuRect.Height / 2f - dot / 2f;
        float cx = _menuRect.Left + _menuRect.Width / 2f - dot / 2f;
        float spacing = S(6);
        using var dotsBrush = new SolidBrush(_theme.Secondary);
        g.FillEllipse(dotsBrush, cx - spacing, cy, dot, dot);
        g.FillEllipse(dotsBrush, cx, cy, dot, dot);
        g.FillEllipse(dotsBrush, cx + spacing, cy, dot, dot);
    }

    #region Vector Icons

    private static void DrawPlayIcon(Graphics g, RectangleF r, Brush brush)
    {
        float inset = r.Width * 0.1f;
        using var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new PointF(r.Left + inset, r.Top),
            new PointF(r.Right, r.Top + r.Height / 2f),
            new PointF(r.Left + inset, r.Bottom),
        });
        g.FillPath(brush, path);
    }

    private static void DrawPauseIcon(Graphics g, RectangleF r, Brush brush)
    {
        float bar = r.Width * 0.28f;
        g.FillRectangle(brush, r.Left + r.Width * 0.12f, r.Top, bar, r.Height);
        g.FillRectangle(brush, r.Right - r.Width * 0.12f - bar, r.Top, bar, r.Height);
    }

    private static void DrawPreviousIcon(Graphics g, RectangleF r, Brush brush)
    {
        float bar = r.Width * 0.16f;
        g.FillRectangle(brush, r.Left, r.Top, bar, r.Height);
        using var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new PointF(r.Right, r.Top),
            new PointF(r.Left + bar, r.Top + r.Height / 2f),
            new PointF(r.Right, r.Bottom),
        });
        g.FillPath(brush, path);
    }

    private static void DrawNextIcon(Graphics g, RectangleF r, Brush brush)
    {
        float bar = r.Width * 0.16f;
        g.FillRectangle(brush, r.Right - bar, r.Top, bar, r.Height);
        using var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new PointF(r.Left, r.Top),
            new PointF(r.Right - bar, r.Top + r.Height / 2f),
            new PointF(r.Left, r.Bottom),
        });
        g.FillPath(brush, path);
    }

    private static void DrawShuffleIcon(Graphics g, RectangleF r, Brush brush, bool active)
    {
        using var pen = new Pen(brush, Math.Max(1.4f, r.Width * 0.11f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        float y1 = r.Top + r.Height * 0.28f;
        float y2 = r.Top + r.Height * 0.72f;
        float xMid1 = r.Left + r.Width * 0.34f;
        float xMid2 = r.Left + r.Width * 0.64f;
        float xEnd = r.Right - r.Width * 0.08f;

        g.DrawLines(pen, new[] { new PointF(r.Left, y1), new PointF(xMid1, y1), new PointF(xMid2, y2), new PointF(xEnd, y2) });
        g.DrawLines(pen, new[] { new PointF(r.Left, y2), new PointF(xMid1, y2), new PointF(xMid2, y1), new PointF(xEnd, y1) });

        float arrow = r.Width * 0.16f;
        g.DrawLines(pen, new[] { new PointF(xEnd - arrow, y1 - arrow), new PointF(xEnd, y1), new PointF(xEnd - arrow, y1 + arrow) });
        g.DrawLines(pen, new[] { new PointF(xEnd - arrow, y2 - arrow), new PointF(xEnd, y2), new PointF(xEnd - arrow, y2 + arrow) });

        if (active)
        {
            float d = r.Width * 0.18f;
            g.FillEllipse(brush, r.Left + r.Width / 2f - d / 2f, r.Bottom + r.Height * 0.08f, d, d);
        }
    }

    private static void DrawRepeatIcon(Graphics g, RectangleF r, Brush brush, MediaPlaybackAutoRepeatMode mode)
    {
        using var pen = new Pen(brush, Math.Max(1.4f, r.Width * 0.11f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        float top = r.Top + r.Height * 0.22f;
        float bottom = r.Top + r.Height * 0.78f;
        float left = r.Left + r.Width * 0.08f;
        float right = r.Right - r.Width * 0.08f;
        float arc = r.Width * 0.18f;

        g.DrawLines(pen, new[] { new PointF(left, top + arc), new PointF(left, top), new PointF(right, top) });
        g.DrawLines(pen, new[] { new PointF(right, bottom - arc), new PointF(right, bottom), new PointF(left, bottom) });

        float arrow = r.Width * 0.15f;
        g.DrawLines(pen, new[] { new PointF(right - arrow, top - arrow), new PointF(right, top), new PointF(right - arrow, top + arrow) });
        g.DrawLines(pen, new[] { new PointF(left + arrow, bottom - arrow), new PointF(left, bottom), new PointF(left + arrow, bottom + arrow) });

        if (mode == MediaPlaybackAutoRepeatMode.Track)
        {
            float d = r.Width * 0.24f;
            g.FillEllipse(brush, r.Left + r.Width / 2f - d / 2f, r.Top + r.Height / 2f - d / 2f, d, d);
        }
        else if (mode == MediaPlaybackAutoRepeatMode.List)
        {
            float d = r.Width * 0.18f;
            g.FillEllipse(brush, r.Left + r.Width / 2f - d / 2f, r.Bottom + r.Height * 0.08f, d, d);
        }
    }

    private static void DrawSpeakerIcon(Graphics g, RectangleF r, Brush brush, float volume, bool muted)
    {
        float midY = r.Top + r.Height / 2f;
        using var path = new GraphicsPath();
        path.AddPolygon(new[]
        {
            new PointF(r.Left + r.Width * 0.06f, midY - r.Height * 0.18f),
            new PointF(r.Left + r.Width * 0.26f, midY - r.Height * 0.18f),
            new PointF(r.Left + r.Width * 0.50f, r.Top + r.Height * 0.14f),
            new PointF(r.Left + r.Width * 0.50f, r.Bottom - r.Height * 0.14f),
            new PointF(r.Left + r.Width * 0.26f, midY + r.Height * 0.18f),
            new PointF(r.Left + r.Width * 0.06f, midY + r.Height * 0.18f),
        });
        g.FillPath(brush, path);

        using var pen = new Pen(brush, Math.Max(1.3f, r.Width * 0.09f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        if (muted || volume <= 0.001f)
        {
            float x1 = r.Left + r.Width * 0.64f;
            float x2 = r.Left + r.Width * 0.92f;
            float y1 = midY - r.Height * 0.16f;
            float y2 = midY + r.Height * 0.16f;
            g.DrawLine(pen, x1, y1, x2, y2);
            g.DrawLine(pen, x2, y1, x1, y2);
            return;
        }

        if (volume > 0.01f)
        {
            float d1 = r.Width * 0.34f;
            g.DrawArc(pen, r.Left + r.Width * 0.44f, midY - d1 / 2f, d1, d1, -50, 100);
        }

        if (volume > 0.45f)
        {
            float d2 = r.Width * 0.58f;
            g.DrawArc(pen, r.Left + r.Width * 0.36f, midY - d2 / 2f, d2, d2, -55, 110);
        }
    }

    private static void DrawRefreshIcon(Graphics g, RectangleF r, Brush brush)
    {
        using var pen = new Pen(brush, Math.Max(1.3f, r.Width * 0.12f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        g.DrawArc(pen, r.Left + r.Width * 0.1f, r.Top + r.Height * 0.1f, r.Width * 0.8f, r.Height * 0.8f, -45, 290);
        float ax = r.Left + r.Width * 0.78f;
        float ay = r.Top + r.Height * 0.24f;
        float a = r.Width * 0.18f;
        g.DrawLines(pen, new[] { new PointF(ax - a, ay), new PointF(ax, ay), new PointF(ax, ay - a) });
    }

    private static void DrawChevronIcon(Graphics g, RectangleF r, Brush brush, bool expanded)
    {
        using var pen = new Pen(brush, Math.Max(1.4f, r.Width * 0.16f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        if (expanded)
        {
            g.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * 0.15f, r.Top + r.Height * 0.32f),
                new PointF(r.Left + r.Width * 0.50f, r.Top + r.Height * 0.68f),
                new PointF(r.Right - r.Width * 0.15f, r.Top + r.Height * 0.32f),
            });
        }
        else
        {
            g.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * 0.32f, r.Top + r.Height * 0.15f),
                new PointF(r.Left + r.Width * 0.68f, r.Top + r.Height * 0.50f),
                new PointF(r.Left + r.Width * 0.32f, r.Bottom - r.Height * 0.15f),
            });
        }
    }

    private static void DrawFolderIcon(Graphics g, RectangleF r, Brush brush)
    {
        using var path = new GraphicsPath();
        float top = r.Top + r.Height * 0.14f;
        float tabRight = r.Left + r.Width * 0.44f;
        float bodyTop = r.Top + r.Height * 0.28f;
        float bottom = r.Bottom - r.Height * 0.12f;

        path.AddPolygon(new[]
        {
            new PointF(r.Left, top),
            new PointF(tabRight - r.Width * 0.08f, top),
            new PointF(tabRight, bodyTop),
            new PointF(r.Right, bodyTop),
            new PointF(r.Right, bottom),
            new PointF(r.Left, bottom),
        });
        g.FillPath(brush, path);
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    #endregion

    #endregion

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _media.StateChanged -= OnMediaChanged;
            _media.ThumbnailChanged -= OnThumbnailChanged;
            _api.PlaylistsChanged -= OnPlaylistsChanged;
            _api.ThumbnailLoaded -= OnPlaylistThumbnailLoaded;
            _menu.Closed -= OnContextMenuClosed;
            _tickTimer.Dispose();
            _animTimer.Dispose();
            _volumeDebounceTimer.Dispose();
            _toolTip.Dispose();
            _titleFont?.Dispose();
            _artistFont?.Dispose();
            _albumFont?.Dispose();
            _smallFont?.Dispose();
            _sectionFont?.Dispose();
            _footerFont?.Dispose();
        }

        base.Dispose(disposing);
    }
}
