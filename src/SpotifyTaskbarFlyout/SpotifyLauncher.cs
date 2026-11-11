using System.Diagnostics;

namespace SpotifyTaskbarFlyout;

internal static class SpotifyLauncher
{
    /// <summary>Starts Spotify or brings the running Spotify window to the foreground.</summary>
    public static void Open()
    {
        try
        {
            Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            MessageBox.Show(Strings.ErrorSpotifyLaunch, Strings.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
