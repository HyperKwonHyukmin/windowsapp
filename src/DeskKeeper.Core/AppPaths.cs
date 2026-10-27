namespace DeskKeeper.Core;

/// <summary>PC마다 따로 두는 데이터 위치(%APPDATA%\DeskKeeper).</summary>
public static class AppPaths
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskKeeper");

    public static string SettingsFile { get; } = Path.Combine(DataDir, "settings.json");
}
