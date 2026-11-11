using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.Graphics.Imaging;

namespace SpotifyTaskbarFlyout;

internal sealed record SpotifyPlaylist(
    string Id,
    string Name,
    string Uri,
    int? TrackCount,
    string? ImageUrl);

internal sealed record SpotifyPlaybackInfo(
    bool HasDevice,
    string? DeviceId,
    bool SupportsVolume,
    int VolumePercent,
    bool IsPlaying,
    bool ShuffleState,
    string RepeatState,
    string? ContextUri);

internal enum ApiActionResult
{
    Ok,
    NotConnected,
    NoActiveDevice,
    PremiumRequired,
    Failed,
}

/// <summary>
/// Lightweight client for the Spotify Web API (in-app volume, playlists, shuffle/repeat, device playback).
/// </summary>
internal sealed class SpotifyApiClient
{
    private const string BaseUrl = "https://api.spotify.com/v1";

    private readonly SpotifyAuthService _auth;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Image> _thumbnailCache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _thumbGate = new(6, 6);

    public SpotifyApiClient(SpotifyAuthService auth, HttpClient http)
    {
        _auth = auth;
        _http = http;
    }

    public bool IsConnected => _auth.IsConnected;

    public IReadOnlyList<SpotifyPlaylist> Playlists { get; private set; } = Array.Empty<SpotifyPlaylist>();

    public SpotifyPlaybackInfo? LastPlayback { get; private set; }

    public event EventHandler? PlaylistsChanged;

    public event EventHandler? ThumbnailLoaded;

    public async Task<string?> GetCurrentUserDisplayNameAsync(CancellationToken ct = default)
    {
        using HttpResponseMessage? res = await SendAsync(HttpMethod.Get, "/me", null, ct);
        if (res is null || !res.IsSuccessStatusCode)
        {
            return null;
        }

        string json = await res.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("display_name", out JsonElement dn) && dn.ValueKind == JsonValueKind.String)
        {
            return dn.GetString();
        }

        return doc.RootElement.TryGetProperty("id", out JsonElement id) ? id.GetString() : null;
    }

    public async Task<SpotifyPlaybackInfo?> RefreshPlaybackAsync(CancellationToken ct = default)
    {
        using HttpResponseMessage? res = await SendAsync(HttpMethod.Get, "/me/player", null, ct);
        if (res is null)
        {
            return null;
        }

        if (res.StatusCode == HttpStatusCode.NoContent)
        {
            LastPlayback = null;
            return null;
        }

        if (!res.IsSuccessStatusCode)
        {
            return LastPlayback;
        }

        string json = await res.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string? deviceId = null;
        bool supportsVolume = false;
        int volumePercent = 100;

        if (root.TryGetProperty("device", out JsonElement dev) && dev.ValueKind == JsonValueKind.Object)
        {
            deviceId = dev.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() : null;
            supportsVolume = !dev.TryGetProperty("supports_volume", out JsonElement sv) || sv.ValueKind != JsonValueKind.False;
            if (dev.TryGetProperty("volume_percent", out JsonElement vp) && vp.ValueKind == JsonValueKind.Number)
            {
                volumePercent = vp.GetInt32();
            }
        }

        bool isPlaying = root.TryGetProperty("is_playing", out JsonElement ip) && ip.ValueKind == JsonValueKind.True;
        bool shuffle = root.TryGetProperty("shuffle_state", out JsonElement sh) && sh.ValueKind == JsonValueKind.True;
        string repeat = root.TryGetProperty("repeat_state", out JsonElement rep) && rep.ValueKind == JsonValueKind.String
            ? rep.GetString() ?? "off"
            : "off";

        string? contextUri = null;
        if (root.TryGetProperty("context", out JsonElement ctx) && ctx.ValueKind == JsonValueKind.Object
            && ctx.TryGetProperty("uri", out JsonElement uriEl) && uriEl.ValueKind == JsonValueKind.String)
        {
            contextUri = uriEl.GetString();
        }

        var info = new SpotifyPlaybackInfo(
            HasDevice: deviceId is not null,
            DeviceId: deviceId,
            SupportsVolume: supportsVolume,
            VolumePercent: Math.Clamp(volumePercent, 0, 100),
            IsPlaying: isPlaying,
            ShuffleState: shuffle,
            RepeatState: repeat,
            ContextUri: contextUri);

        LastPlayback = info;
        return info;
    }

    public async Task<IReadOnlyList<SpotifyPlaylist>> RefreshPlaylistsAsync(CancellationToken ct = default)
    {
        var list = new List<SpotifyPlaylist>();
        string? nextEndpoint = "/me/playlists?limit=50";

        while (nextEndpoint is not null && list.Count < 500)
        {
            using HttpResponseMessage? res = await SendAsync(HttpMethod.Get, nextEndpoint, null, ct);
            if (res is null || !res.IsSuccessStatusCode)
            {
                break;
            }

            string json = await res.Content.ReadAsStringAsync(ct);
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string? id = item.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() : null;
                    string? name = item.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() : null;
                    string? uri = item.TryGetProperty("uri", out JsonElement uriEl) ? uriEl.GetString() : null;
                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(uri))
                    {
                        continue;
                    }

                    int? trackCount = ParseTrackCount(item);
                    string? imageUrl = PickBestImageUrl(item);

                    list.Add(new SpotifyPlaylist(id, name, uri, trackCount, imageUrl));
                }
            }

            if (root.TryGetProperty("next", out JsonElement nextEl) && nextEl.ValueKind == JsonValueKind.String)
            {
                string? nextUrl = nextEl.GetString();
                nextEndpoint = nextUrl is not null && nextUrl.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase)
                    ? nextUrl[BaseUrl.Length..]
                    : null;
            }
            else
            {
                nextEndpoint = null;
            }
        }

        Playlists = list;
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        _ = LoadAllThumbnailsAsync(list, ct);
        return list;
    }

    private static int? ParseTrackCount(JsonElement playlist)
    {
        // Spotify's 2026 Web API renamed playlist.tracks -> playlist.items; support both.
        foreach (string key in new[] { "items", "tracks" })
        {
            if (playlist.TryGetProperty(key, out JsonElement el)
                && el.ValueKind == JsonValueKind.Object
                && el.TryGetProperty("total", out JsonElement totalEl)
                && totalEl.ValueKind == JsonValueKind.Number)
            {
                return totalEl.GetInt32();
            }
        }

        return null;
    }

    private static string? PickBestImageUrl(JsonElement playlist)
    {
        if (!playlist.TryGetProperty("images", out JsonElement imgs) || imgs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? fallback = null;
        string? bestSmall = null;
        int bestWidth = int.MaxValue;

        foreach (JsonElement img in imgs.EnumerateArray())
        {
            if (img.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? url = img.TryGetProperty("url", out JsonElement urlEl) && urlEl.ValueKind == JsonValueKind.String
                ? urlEl.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            fallback ??= url;

            if (img.TryGetProperty("width", out JsonElement wEl) && wEl.ValueKind == JsonValueKind.Number)
            {
                int w = wEl.GetInt32();
                if (w >= 48 && w < bestWidth)
                {
                    bestWidth = w;
                    bestSmall = url;
                }
            }
        }

        return bestSmall ?? fallback;
    }

    public Image? GetCachedThumbnail(string? url) =>
        url is not null && _thumbnailCache.TryGetValue(url, out Image? img) ? img : null;

    private async Task LoadAllThumbnailsAsync(IReadOnlyList<SpotifyPlaylist> playlists, CancellationToken ct)
    {
        var urls = playlists
            .Select(p => p.ImageUrl)
            .Where(u => !string.IsNullOrWhiteSpace(u) && !_thumbnailCache.ContainsKey(u!))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (urls.Count == 0)
        {
            return;
        }

        IEnumerable<Task> tasks = urls.Select(url => LoadPlaylistThumbnailAsync(url!, ct));
        await Task.WhenAll(tasks);
    }

    public async Task<Image?> LoadPlaylistThumbnailAsync(string url, CancellationToken ct = default)
    {
        if (_thumbnailCache.TryGetValue(url, out Image? cached))
        {
            return cached;
        }

        await _thumbGate.WaitAsync(ct);
        try
        {
            if (_thumbnailCache.TryGetValue(url, out cached))
            {
                return cached;
            }

            byte[] bytes = await _http.GetByteArrayAsync(url, ct);
            Bitmap? bmp = await DecodeBitmapAsync(bytes);
            if (bmp is null)
            {
                return null;
            }

            if (_thumbnailCache.TryAdd(url, bmp))
            {
                ThumbnailLoaded?.Invoke(this, EventArgs.Empty);
                return bmp;
            }

            bmp.Dispose();
            return _thumbnailCache.GetValueOrDefault(url);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return null;
        }
        finally
        {
            _thumbGate.Release();
        }
    }

    private static async Task<Bitmap?> DecodeBitmapAsync(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var loaded = Image.FromStream(ms);
            return new Bitmap(loaded, new Size(64, 64));
        }
        catch (ArgumentException)
        {
            // GDI+ does not decode WebP/AVIF; fall back to Windows Imaging Component (WIC).
            using var ms = new MemoryStream(bytes);
            Windows.Graphics.Imaging.BitmapDecoder decoder =
                await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
            const uint size = 64;
            var transform = new BitmapTransform
            {
                ScaledWidth = size,
                ScaledHeight = size,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            PixelDataProvider pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            byte[] pixels = pixelData.DetachPixelData();
            var bmp = new Bitmap((int)size, (int)size, PixelFormat.Format32bppPArgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, (int)size, (int)size), ImageLockMode.WriteOnly, bmp.PixelFormat);
            try
            {
                Marshal.Copy(pixels, 0, data.Scan0, Math.Min(pixels.Length, data.Stride * (int)size));
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            return bmp;
        }
    }

    public Task<ApiActionResult> SetVolumeAsync(int volumePercent, CancellationToken ct = default)
    {
        int clamped = Math.Clamp(volumePercent, 0, 100);
        return PutPlaybackCommandAsync($"/me/player/volume?volume_percent={clamped}", bodyJson: null, ct);
    }

    public Task<ApiActionResult> PlayPlaylistAsync(string contextUri, CancellationToken ct = default)
    {
        string body = JsonSerializer.Serialize(new { context_uri = contextUri });
        return PutPlaybackCommandAsync("/me/player/play", body, ct);
    }

    public Task<ApiActionResult> SetShuffleAsync(bool state, CancellationToken ct = default) =>
        PutPlaybackCommandAsync($"/me/player/shuffle?state={(state ? "true" : "false")}", bodyJson: null, ct);

    public Task<ApiActionResult> SetRepeatAsync(string state, CancellationToken ct = default) =>
        PutPlaybackCommandAsync($"/me/player/repeat?state={Uri.EscapeDataString(state)}", bodyJson: null, ct);

    public void ClearCache()
    {
        Playlists = Array.Empty<SpotifyPlaylist>();
        LastPlayback = null;
        foreach (Image img in _thumbnailCache.Values)
        {
            img.Dispose();
        }

        _thumbnailCache.Clear();
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<ApiActionResult> PutPlaybackCommandAsync(string endpoint, string? bodyJson, CancellationToken ct)
    {
        if (!IsConnected)
        {
            return ApiActionResult.NotConnected;
        }

        using HttpResponseMessage? res = await SendAsync(HttpMethod.Put, endpoint, bodyJson, ct);
        if (res is null)
        {
            return ApiActionResult.Failed;
        }

        if (res.IsSuccessStatusCode)
        {
            return ApiActionResult.Ok;
        }

        if (res.StatusCode == HttpStatusCode.Forbidden)
        {
            return ApiActionResult.PremiumRequired;
        }

        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            string? deviceId = await FindBestDeviceIdAsync(ct);
            if (deviceId is null)
            {
                return ApiActionResult.NoActiveDevice;
            }

            string sep = endpoint.Contains('?') ? "&" : "?";
            using HttpResponseMessage? retry = await SendAsync(HttpMethod.Put, $"{endpoint}{sep}device_id={Uri.EscapeDataString(deviceId)}", bodyJson, ct);
            if (retry is not null && retry.IsSuccessStatusCode)
            {
                return ApiActionResult.Ok;
            }

            return retry?.StatusCode == HttpStatusCode.Forbidden ? ApiActionResult.PremiumRequired : ApiActionResult.NoActiveDevice;
        }

        string err = await res.Content.ReadAsStringAsync(ct);
        Log.Error(new HttpRequestException($"Spotify API PUT {endpoint} failed ({(int)res.StatusCode}): {err}"));
        return ApiActionResult.Failed;
    }

    private async Task<string?> FindBestDeviceIdAsync(CancellationToken ct)
    {
        using HttpResponseMessage? res = await SendAsync(HttpMethod.Get, "/me/player/devices", null, ct);
        if (res is null || !res.IsSuccessStatusCode)
        {
            return null;
        }

        string json = await res.Content.ReadAsStringAsync(ct);
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("devices", out JsonElement devices) || devices.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? fallback = null;
        foreach (JsonElement dev in devices.EnumerateArray())
        {
            string? id = dev.TryGetProperty("id", out JsonElement idEl) ? idEl.GetString() : null;
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            bool isActive = dev.TryGetProperty("is_active", out JsonElement actEl) && actEl.ValueKind == JsonValueKind.True;
            if (isActive)
            {
                return id;
            }

            string? type = dev.TryGetProperty("type", out JsonElement typeEl) ? typeEl.GetString() : null;
            if (string.Equals(type, "Computer", StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }

            fallback ??= id;
        }

        return fallback;
    }

    private async Task<HttpResponseMessage?> SendAsync(HttpMethod method, string endpoint, string? bodyJson, CancellationToken ct)
    {
        string? token = await _auth.GetValidAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(method, BaseUrl + endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (bodyJson is not null)
            {
                request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
            }

            return await _http.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return null;
        }
    }
}
