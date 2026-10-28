namespace DeskKeeper.Core;

/// <summary>settings.json 의 최상위 구조. 사용자가 직접 편집하는 파일이다.</summary>
public sealed class AppSettings
{
    public List<LauncherItem> Launcher { get; set; } = [];

    /// <summary>런처 분류의 표시 순서. 항목에 쓰인 분류가 여기 없으면 뒤에 붙인다.</summary>
    public List<string> LauncherGroups { get; set; } = [];

    /// <summary>폴더 분석의 확장자 분류. 없으면(null) Categorizer.Defaults 를 쓴다.</summary>
    public List<CategoryRule>? Categories { get; set; }

    /// <summary>기한을 넘긴 파일을 알려 줄 폴더. 없으면(null) 다운로드 30일.</summary>
    public List<WatchedFolder>? WatchedFolders { get; set; }

    /// <summary>"임시 표시"로 붙이는 기본 일수.</summary>
    public int MarkDays { get; set; } = 30;

    /// <summary>파일별 날짜 표시. 날짜가 지나면 정리 후보, 아직이면 제외(보류).</summary>
    public List<FileMark> Marks { get; set; } = [];
}

public sealed class WatchedFolder
{
    public string Path { get; set; } = "";
    /// <summary>마지막 수정 후 이 일수가 지나면 정리 후보.</summary>
    public int Days { get; set; } = 30;
}

public sealed class FileMark
{
    public string Path { get; set; } = "";
    public DateTime Until { get; set; }
}

/// <summary>분류 이름과 그 분류에 속하는 확장자(".op2" 형식).</summary>
public sealed class CategoryRule
{
    public string Name { get; set; } = "";
    public List<string> Extensions { get; set; } = [];
}

/// <summary>런처에 등록한 항목 하나(프로그램·폴더·파일·URL).</summary>
public sealed class LauncherItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Arguments { get; set; }
    public string? WorkingDirectory { get; set; }
    /// <summary>런처 분류 이름. null 이면 "분류 없음".</summary>
    public string? Group { get; set; }
}
