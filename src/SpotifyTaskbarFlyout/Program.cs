namespace SpotifyTaskbarFlyout;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Allow only a single instance.
        using var mutex = new Mutex(true, @"Local\SpotifyTaskbarFlyout.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();

        // Required by MediaController to marshal WinRT events onto the UI thread.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        Application.Run(new TrayApplicationContext());
    }
}
