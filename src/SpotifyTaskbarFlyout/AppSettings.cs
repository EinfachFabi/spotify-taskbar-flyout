using System.Text;
using System.Text.Json;

namespace SpotifyTaskbarFlyout;

internal sealed record SpotifyTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAtUtc);

internal sealed class PlaylistFolder
{
    public string Name { get; set; } = string.Empty;

    public bool Collapsed { get; set; }

    public List<string> PlaylistIds { get; set; } = new();
}

/// <summary>
/// Persists settings and local playlist folders in settings.json,
/// and DPAPI-encrypted Spotify OAuth tokens in auth.dat.
/// </summary>
internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyTaskbarFlyout");

    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    private static string TokensPath => Path.Combine(DataDirectory, "auth.dat");

    public string SpotifyClientId { get; set; } = string.Empty;

    public string? UserDisplayName { get; set; }

    public List<PlaylistFolder> Folders { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
                loaded.Folders ??= new List<PlaylistFolder>();
                foreach (PlaylistFolder f in loaded.Folders)
                {
                    f.PlaylistIds ??= new List<string>();
                }

                return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }

    public static SpotifyTokens? LoadTokens()
    {
        try
        {
            if (!File.Exists(TokensPath))
            {
                return null;
            }

            byte[] encrypted = File.ReadAllBytes(TokensPath);
            byte[] plain = NativeMethods.UnprotectData(encrypted);
            return JsonSerializer.Deserialize<SpotifyTokens>(Encoding.UTF8.GetString(plain));
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return null;
        }
    }

    public static void SaveTokens(SpotifyTokens? tokens)
    {
        try
        {
            if (tokens is null)
            {
                if (File.Exists(TokensPath))
                {
                    File.Delete(TokensPath);
                }

                return;
            }

            Directory.CreateDirectory(DataDirectory);
            byte[] plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens));
            File.WriteAllBytes(TokensPath, NativeMethods.ProtectData(plain));
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
    }
}
