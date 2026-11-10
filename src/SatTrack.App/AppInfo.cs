namespace SatTrack.App;

public static class AppInfo
{
    /// <summary>Name shown in the window title, dialogs and saved logs.</summary>
    public const string Name = "GS-232B Interface Software";

    private static Icon? _icon;

    /// <summary>The app icon (dish and satellite), for window title bars and the taskbar.</summary>
    public static Icon? Icon
    {
        get
        {
            if (_icon is not null) return _icon;
            using var s = typeof(AppInfo).Assembly.GetManifestResourceStream("SatTrack.App.app.ico");
            if (s is not null) _icon = new Icon(s);
            return _icon;
        }
    }
}
