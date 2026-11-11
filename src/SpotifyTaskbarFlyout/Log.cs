namespace SpotifyTaskbarFlyout;

/// <summary>Minimal error log in %APPDATA%\SpotifyTaskbarFlyout\error.log.</summary>
internal static class Log
{
    private static readonly object Sync = new();

    private static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyTaskbarFlyout");

    public static void Error(Exception? exception)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DataDirectory);
                string file = Path.Combine(DataDirectory, "error.log");

                // Keep the file from growing forever.
                if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024)
                {
                    File.Delete(file);
                }

                File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never cause a crash itself.
        }
    }
}
