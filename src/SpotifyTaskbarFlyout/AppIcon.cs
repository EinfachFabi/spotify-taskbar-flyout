namespace SpotifyTaskbarFlyout;

internal static class AppIcon
{
    public static Icon Load(Size size)
    {
        try
        {
            using Stream? stream = typeof(AppIcon).Assembly.GetManifestResourceStream("SpotifyTaskbarFlyout.app.ico");
            if (stream is not null)
            {
                return new Icon(stream, size);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
