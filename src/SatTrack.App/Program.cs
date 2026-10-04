namespace SatTrack.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // High-DPI mode, visual styles and default font come from the project file
        // (ApplicationHighDpiMode = PerMonitorV2).
        ApplicationConfiguration.Initialize();

        Application.ThreadException += (_, e) => ShowError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) ShowError(ex);
        };

        Application.Run(new MainForm());
    }

    private static void ShowError(Exception ex)
    {
        MessageBox.Show(ex.Message, $"{AppInfo.Name}: unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
