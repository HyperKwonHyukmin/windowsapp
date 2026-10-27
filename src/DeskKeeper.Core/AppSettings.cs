namespace DeskKeeper.Core;

/// <summary>settings.json 의 최상위 구조. 사용자가 직접 편집하는 파일이다.</summary>
public sealed class AppSettings
{
    public List<LauncherItem> Launcher { get; set; } = [];
}

/// <summary>런처에 등록한 항목 하나(프로그램·폴더·파일·URL).</summary>
public sealed class LauncherItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
}
