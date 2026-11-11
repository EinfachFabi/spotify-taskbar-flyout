using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace SpotifyTaskbarFlyout;

/// <summary>Snapshot of the Spotify playback state.</summary>
internal sealed record PlaybackState(
    bool HasSession,
    string Title,
    string Artist,
    string Album,
    bool IsPlaying,
    bool CanPlayPause,
    bool CanPrevious,
    bool CanNext,
    bool CanSeek,
    bool CanShuffle,
    bool IsShuffleActive,
    bool CanRepeat,
    MediaPlaybackAutoRepeatMode RepeatMode,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset PositionTimestamp)
{
    public static PlaybackState Empty { get; } = new(
        false, string.Empty, string.Empty, string.Empty,
        false, false, false, false, false,
        false, false, false, MediaPlaybackAutoRepeatMode.None,
        TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.MinValue);

    /// <summary>Extrapolated current position (Spotify reports the timeline only sporadically).</summary>
    public TimeSpan CurrentPosition
    {
        get
        {
            if (Duration <= TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            TimeSpan position = Position;
            if (IsPlaying && PositionTimestamp != DateTimeOffset.MinValue)
            {
                TimeSpan elapsed = DateTimeOffset.Now - PositionTimestamp;
                if (elapsed > TimeSpan.Zero)
                {
                    position += elapsed;
                }
            }

            return position > Duration ? Duration : position;
        }
    }
}

/// <summary>
/// Controls Spotify via the Windows System Media Transport Controls (SMTC).
/// Works without Spotify login/API key with both the desktop and the Microsoft Store version.
/// </summary>
internal sealed class MediaController : IDisposable
{
    private readonly SynchronizationContext _ui;
    private SessionManager? _manager;
    private Session? _session;
    private int _thumbnailVersion;
    private bool _disposed;

    public MediaController()
    {
        _ui = SynchronizationContext.Current
              ?? throw new InvalidOperationException("MediaController must be created on the UI thread.");
    }

    /// <summary>Raised on the UI thread when the state changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised on the UI thread when the cover art changes.</summary>
    public event EventHandler? ThumbnailChanged;

    public PlaybackState State { get; private set; } = PlaybackState.Empty;

    public Image? Thumbnail { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await SessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => Post(UpdateSession);
            UpdateSession();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    public Task TogglePlayPauseAsync() => RunAsync(s => s.TryTogglePlayPauseAsync().AsTask());

    public Task NextAsync() => RunAsync(s => s.TrySkipNextAsync().AsTask());

    public Task PreviousAsync() => RunAsync(s => s.TrySkipPreviousAsync().AsTask());

    public Task SeekAsync(TimeSpan position) => RunAsync(s => s.TryChangePlaybackPositionAsync(position.Ticks).AsTask());

    public Task ToggleShuffleAsync() => RunAsync(s => s.TryChangeShuffleActiveAsync(!State.IsShuffleActive).AsTask());

    public Task CycleRepeatAsync()
    {
        MediaPlaybackAutoRepeatMode next = State.RepeatMode switch
        {
            MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List,
            MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
            _ => MediaPlaybackAutoRepeatMode.None,
        };
        return RunAsync(s => s.TryChangeAutoRepeatModeAsync(next).AsTask());
    }

    private async Task RunAsync(Func<Session, Task<bool>> action)
    {
        Session? session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            await action(session);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    private static bool IsSpotify(Session session)
    {
        try
        {
            // Desktop: "Spotify.exe", Microsoft Store: "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"
            return session.SourceAppUserModelId.Contains("spotify", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void UpdateSession()
    {
        if (_disposed || _manager is null)
        {
            return;
        }

        Session? found = null;
        try
        {
            found = _manager.GetSessions().FirstOrDefault(IsSpotify);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }

        if (found is not null && _session is not null && found.SourceAppUserModelId == _session.SourceAppUserModelId)
        {
            return;
        }

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        _session = found;

        if (_session is null)
        {
            State = PlaybackState.Empty;
            SetThumbnail(null);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;

        RefreshPlayback();
        RefreshTimeline();
        _ = RefreshMediaPropertiesAsync();
    }

    private void OnMediaPropertiesChanged(Session sender, MediaPropertiesChangedEventArgs args) =>
        Post(() => _ = RefreshMediaPropertiesAsync());

    private void OnPlaybackInfoChanged(Session sender, PlaybackInfoChangedEventArgs args) =>
        Post(RefreshPlayback);

    private void OnTimelinePropertiesChanged(Session sender, TimelinePropertiesChangedEventArgs args) =>
        Post(RefreshTimeline);

    private async Task RefreshMediaPropertiesAsync()
    {
        Session? session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            GlobalSystemMediaTransportControlsSessionMediaProperties props = await session.TryGetMediaPropertiesAsync();
            if (session != _session || props is null)
            {
                return;
            }

            bool trackChanged = props.Title != State.Title || props.Artist != State.Artist;

            State = State with
            {
                HasSession = true,
                Title = props.Title ?? string.Empty,
                Artist = props.Artist ?? string.Empty,
                Album = props.AlbumTitle ?? string.Empty,
            };
            StateChanged?.Invoke(this, EventArgs.Empty);

            if (trackChanged || Thumbnail is null)
            {
                await LoadThumbnailAsync(props.Thumbnail);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    private void RefreshPlayback()
    {
        Session? session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            GlobalSystemMediaTransportControlsSessionPlaybackInfo info = session.GetPlaybackInfo();
            GlobalSystemMediaTransportControlsSessionPlaybackControls controls = info.Controls;
            bool isPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            // "Freeze" the position on play/pause changes so the extrapolation stays correct.
            TimeSpan position = State.CurrentPosition;

            State = State with
            {
                HasSession = true,
                IsPlaying = isPlaying,
                CanPlayPause = controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled,
                CanPrevious = controls.IsPreviousEnabled,
                CanNext = controls.IsNextEnabled,
                CanSeek = controls.IsPlaybackPositionEnabled,
                CanShuffle = controls.IsShuffleEnabled,
                IsShuffleActive = info.IsShuffleActive ?? false,
                CanRepeat = controls.IsRepeatEnabled,
                RepeatMode = info.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None,
                Position = position,
                PositionTimestamp = DateTimeOffset.Now,
            };
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    private void RefreshTimeline()
    {
        Session? session = _session;
        if (session is null)
        {
            return;
        }

        try
        {
            GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = session.GetTimelineProperties();
            TimeSpan duration = timeline.EndTime - timeline.StartTime;
            TimeSpan position = timeline.Position - timeline.StartTime;
            DateTimeOffset timestamp = timeline.LastUpdatedTime;

            // Guard against invalid/future timestamps.
            if (timestamp == default || timestamp > DateTimeOffset.Now)
            {
                timestamp = DateTimeOffset.Now;
            }

            State = State with
            {
                Duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration,
                Position = position < TimeSpan.Zero ? TimeSpan.Zero : position,
                PositionTimestamp = timestamp,
            };
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    private async Task LoadThumbnailAsync(IRandomAccessStreamReference? reference)
    {
        int version = ++_thumbnailVersion;

        if (reference is null)
        {
            SetThumbnail(null);
            return;
        }

        try
        {
            using IRandomAccessStreamWithContentType winStream = await reference.OpenReadAsync();
            using Stream source = winStream.AsStreamForRead();
            var memory = new MemoryStream();
            await source.CopyToAsync(memory);
            memory.Position = 0;

            Image image;
            using (var loaded = Image.FromStream(memory))
            {
                image = new Bitmap(loaded);
            }

            memory.Dispose();

            if (version != _thumbnailVersion || _disposed)
            {
                image.Dispose();
                return;
            }

            SetThumbnail(image);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            if (version == _thumbnailVersion)
            {
                SetThumbnail(null);
            }
        }
    }

    private void SetThumbnail(Image? image)
    {
        Image? old = Thumbnail;
        Thumbnail = image;
        ThumbnailChanged?.Invoke(this, EventArgs.Empty);
        old?.Dispose();
    }

    private void Post(Action action) => _ui.Post(_ =>
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }, null);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            _session = null;
        }

        Thumbnail?.Dispose();
        Thumbnail = null;
    }
}
