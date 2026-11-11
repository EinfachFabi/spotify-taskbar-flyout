using System.Globalization;
using System.Resources;

namespace SpotifyTaskbarFlyout;

/// <summary>English UI strings for the system tray menu and flyout.</summary>
internal static class Strings
{
    private static readonly ResourceManager Resources = new("SpotifyTaskbarFlyout.Strings", typeof(Strings).Assembly);

    private static string Get(string name) =>
        Resources.GetString(name, CultureInfo.InvariantCulture) ?? name;

    private static string Format(string name, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, Get(name), args);

    public static string AppName => Get(nameof(AppName));

    public static string MenuOpenSpotify => Get(nameof(MenuOpenSpotify));
    public static string MenuPlay => Get(nameof(MenuPlay));
    public static string MenuPause => Get(nameof(MenuPause));
    public static string MenuPrevious => Get(nameof(MenuPrevious));
    public static string MenuNext => Get(nameof(MenuNext));
    public static string MenuPlaylists => Get(nameof(MenuPlaylists));
    public static string MenuNoPlaylists => Get(nameof(MenuNoPlaylists));
    public static string MenuRefreshPlaylists => Get(nameof(MenuRefreshPlaylists));
    public static string MenuConnectSpotify => Get(nameof(MenuConnectSpotify));
    public static string MenuDisconnectSpotify => Get(nameof(MenuDisconnectSpotify));
    public static string MenuStartWithWindows => Get(nameof(MenuStartWithWindows));
    public static string MenuExit => Get(nameof(MenuExit));

    public static string MenuAbout(string version) => Format(nameof(MenuAbout), version);
    public static string MenuConnectedAs(string name) => Format(nameof(MenuConnectedAs), name);

    public static string FlyoutNotRunning => Get(nameof(FlyoutNotRunning));
    public static string FlyoutClickToOpen => Get(nameof(FlyoutClickToOpen));
    public static string FlyoutPlaying => Get(nameof(FlyoutPlaying));
    public static string FlyoutPaused => Get(nameof(FlyoutPaused));
    public static string FlyoutPlaylists => Get(nameof(FlyoutPlaylists));
    public static string FlyoutConnectPrompt => Get(nameof(FlyoutConnectPrompt));
    public static string FlyoutConnectButton => Get(nameof(FlyoutConnectButton));
    public static string FlyoutLoadingPlaylists => Get(nameof(FlyoutLoadingPlaylists));
    public static string FlyoutNoPlaylists => Get(nameof(FlyoutNoPlaylists));

    public static string FlyoutTrackSingle => Get(nameof(FlyoutTrackSingle));
    public static string FlyoutTracksCount(int count) => count == 1 ? FlyoutTrackSingle : Format(nameof(FlyoutTracksCount), count);
    public static string FlyoutPlaylistLabel => Get(nameof(FlyoutPlaylistLabel));
    public static string FlyoutFolderPlaylistsCount(int count) =>
        count == 1 ? Get("FlyoutFolderPlaylistSingle") : Format(nameof(FlyoutFolderPlaylistsCount), count);

    public static string FolderMoveTo => Get(nameof(FolderMoveTo));
    public static string FolderNew => Get(nameof(FolderNew));
    public static string FolderRemoveFrom => Get(nameof(FolderRemoveFrom));
    public static string FolderRename => Get(nameof(FolderRename));
    public static string FolderDelete => Get(nameof(FolderDelete));
    public static string FolderPromptTitle => Get(nameof(FolderPromptTitle));
    public static string FolderPromptLabel => Get(nameof(FolderPromptLabel));

    public static string TipShuffle => Get(nameof(TipShuffle));
    public static string TipPrevious => Get(nameof(TipPrevious));
    public static string TipNext => Get(nameof(TipNext));
    public static string TipPlay => Get(nameof(TipPlay));
    public static string TipPause => Get(nameof(TipPause));
    public static string TipRepeat => Get(nameof(TipRepeat));
    public static string TipMute => Get(nameof(TipMute));
    public static string TipUnmute => Get(nameof(TipUnmute));
    public static string TipVolume => Get(nameof(TipVolume));
    public static string TipVolumeUnavailable => Get(nameof(TipVolumeUnavailable));
    public static string TipRefreshPlaylists => Get(nameof(TipRefreshPlaylists));
    public static string TipMenu => Get(nameof(TipMenu));
    public static string TipOpenSpotify => Get(nameof(TipOpenSpotify));

    public static string TipSeek(string position, string duration) => Format(nameof(TipSeek), position, duration);

    public static string ErrorSpotifyLaunch => Get(nameof(ErrorSpotifyLaunch));

    public static string AboutTitle => Get(nameof(AboutTitle));

    public static string AboutText(string version) => Format(nameof(AboutText), version);
}
