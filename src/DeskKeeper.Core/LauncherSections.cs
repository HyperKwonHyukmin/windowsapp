namespace DeskKeeper.Core;

/// <summary>런처·트레이 메뉴에서 분류별로 묶어 보여 줄 때의 구역 하나. Group 이 null 이면 "분류 없음".</summary>
public sealed record LauncherSection(string? Group, IReadOnlyList<LauncherItem> Items)
{
    public const string UngroupedName = "분류 없음";

    public string Title => Group ?? UngroupedName;

    /// <summary>
    /// 분류 순서(LauncherGroups)대로 묶는다. 항목이 없는 분류는 뺀다.
    /// 분류를 하나도 안 썼으면 제목 없는 구역 하나만 돌려준다(HasGroups == false).
    /// </summary>
    public static List<LauncherSection> Build(AppSettings settings)
    {
        var items = settings.Launcher;
        var sections = new List<LauncherSection>();
        foreach (var group in settings.LauncherGroups)
        {
            var members = items.Where(i => i.Group is not null &&
                string.Equals(i.Group, group, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (members.Count > 0)
                sections.Add(new LauncherSection(group, members));
        }
        var ungrouped = items.Where(i => i.Group is null).ToList();
        if (ungrouped.Count > 0)
            sections.Add(new LauncherSection(null, ungrouped));
        return sections;
    }

    /// <summary>분류 제목을 보여야 하는지(분류를 하나라도 쓰고 있을 때).</summary>
    public static bool HasGroups(List<LauncherSection> sections) => sections.Exists(s => s.Group is not null);
}
