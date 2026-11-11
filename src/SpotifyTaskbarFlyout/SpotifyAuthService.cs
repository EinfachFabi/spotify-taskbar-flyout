using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpotifyTaskbarFlyout;

/// <summary>
/// Handles Spotify OAuth 2.0 Authorization Code Flow with PKCE using a local loopback callback.
/// No client secret is required.
/// </summary>
internal sealed class SpotifyAuthService : IDisposable
{
    public const string RedirectUri = "http://127.0.0.1:43819/callback";

    private const string AuthorizeEndpoint = "https://accounts.spotify.com/authorize";
    private const string TokenEndpoint = "https://accounts.spotify.com/api/token";
    private const string Scopes =
        "user-read-playback-state user-modify-playback-state user-read-currently-playing playlist-read-private playlist-read-collaborative";

    private readonly AppSettings _settings;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private SpotifyTokens? _tokens;

    public SpotifyAuthService(AppSettings settings, HttpClient http)
    {
        _settings = settings;
        _http = http;
        _tokens = AppSettings.LoadTokens();
    }

    public bool IsConnected => _tokens is not null && !string.IsNullOrWhiteSpace(_settings.SpotifyClientId);

    public async Task<string?> GetValidAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_tokens is null || string.IsNullOrWhiteSpace(_settings.SpotifyClientId))
            {
                return null;
            }

            if (_tokens.ExpiresAtUtc > DateTimeOffset.UtcNow.AddSeconds(45))
            {
                return _tokens.AccessToken;
            }

            if (string.IsNullOrWhiteSpace(_tokens.RefreshToken))
            {
                Disconnect();
                return null;
            }

            SpotifyTokens? refreshed = await RefreshTokenAsync(_settings.SpotifyClientId, _tokens.RefreshToken, cancellationToken);
            if (refreshed is null)
            {
                return null;
            }

            _tokens = refreshed;
            AppSettings.SaveTokens(_tokens);
            return _tokens.AccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    public async Task<bool> LoginAsync(string clientId, CancellationToken cancellationToken)
    {
        clientId = clientId.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("Spotify Client ID is required.", nameof(clientId));
        }

        string codeVerifier = GenerateRandomBase64Url(64);
        string codeChallenge = ComputeSha256Base64Url(codeVerifier);
        string state = GenerateRandomBase64Url(24);

        using var listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:43819/callback/");
        listener.Start();

        string authUrl = $"{AuthorizeEndpoint}"
                         + $"?client_id={Uri.EscapeDataString(clientId)}"
                         + $"&response_type=code"
                         + $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}"
                         + $"&code_challenge_method=S256"
                         + $"&code_challenge={Uri.EscapeDataString(codeChallenge)}"
                         + $"&state={Uri.EscapeDataString(state)}"
                         + $"&scope={Uri.EscapeDataString(Scopes)}";

        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        using CancellationTokenRegistration reg = cancellationToken.Register(() =>
        {
            try
            {
                listener.Stop();
            }
            catch
            {
                // Ignore listener shutdown errors on cancellation.
            }
        });

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync();
        }
        catch when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        string? returnedState = context.Request.QueryString["state"];
        string? code = context.Request.QueryString["code"];
        string? error = context.Request.QueryString["error"];

        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code) || returnedState != state)
        {
            await WriteHtmlResponseAsync(
                context.Response,
                "Connection failed",
                $"Spotify returned an error ({WebUtility.HtmlEncode(error ?? "invalid state")}). You can close this tab and try again.");
            return false;
        }

        SpotifyTokens? tokens = await ExchangeCodeAsync(clientId, code, codeVerifier, cancellationToken);
        if (tokens is null)
        {
            await WriteHtmlResponseAsync(
                context.Response,
                "Token exchange failed",
                "Could not exchange authorization code for a Spotify access token. Please verify your Client ID and Redirect URI.");
            return false;
        }

        _settings.SpotifyClientId = clientId;
        _settings.Save();
        _tokens = tokens;
        AppSettings.SaveTokens(tokens);

        await WriteHtmlResponseAsync(
            context.Response,
            "Spotify Connected",
            "Your Spotify account is now connected to Spotify Taskbar Flyout. You can close this tab.");

        return true;
    }

    public void Disconnect()
    {
        _tokens = null;
        _settings.UserDisplayName = null;
        _settings.Save();
        AppSettings.SaveTokens(null);
    }

    private async Task<SpotifyTokens?> ExchangeCodeAsync(string clientId, string code, string codeVerifier, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = codeVerifier,
        };

        return await RequestTokenAsync(form, previousRefreshToken: null, ct);
    }

    private async Task<SpotifyTokens?> RefreshTokenAsync(string clientId, string refreshToken, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        };

        return await RequestTokenAsync(form, refreshToken, ct);
    }

    private async Task<SpotifyTokens?> RequestTokenAsync(Dictionary<string, string> form, string? previousRefreshToken, CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using HttpResponseMessage response = await _http.PostAsync(TokenEndpoint, content, ct);
            string json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                Log.Error(new HttpRequestException($"Spotify token endpoint returned {(int)response.StatusCode}: {json}"));
                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized && previousRefreshToken is not null)
                {
                    Disconnect();
                }

                return null;
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string accessToken = root.GetProperty("access_token").GetString() ?? string.Empty;
            string refreshToken = root.TryGetProperty("refresh_token", out JsonElement rt) && rt.ValueKind == JsonValueKind.String
                ? rt.GetString() ?? previousRefreshToken ?? string.Empty
                : previousRefreshToken ?? string.Empty;
            int expiresIn = root.TryGetProperty("expires_in", out JsonElement exp) ? exp.GetInt32() : 3600;

            return new SpotifyTokens(accessToken, refreshToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return null;
        }
    }

    private static async Task WriteHtmlResponseAsync(HttpListenerResponse response, string title, string message)
    {
        string html =
            "<!doctype html><html><head><meta charset='utf-8'>"
            + $"<title>{WebUtility.HtmlEncode(title)}</title>"
            + "<style>body{font-family:'Segoe UI',sans-serif;background:#121212;color:#f5f5f5;display:flex;align-items:center;justify-content:center;height:100vh;margin:0}"
            + ".card{background:#1e1e1e;border:1px solid #333;border-radius:12px;padding:32px 36px;max-width:440px;text-align:center}"
            + "h1{margin:0 0 12px;font-size:22px;color:#1DB954}p{margin:0;color:#ccc;line-height:1.5}</style></head>"
            + $"<body><div class='card'><h1>{WebUtility.HtmlEncode(title)}</h1><p>{WebUtility.HtmlEncode(message)}</p></div></body></html>";

        byte[] buffer = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
        response.OutputStream.Close();
    }

    private static string GenerateRandomBase64Url(int byteLength)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(byteLength);
        return Base64UrlEncode(bytes);
    }

    private static string ComputeSha256Base64Url(string input)
    {
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(input));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _tokenLock.Dispose();
}
