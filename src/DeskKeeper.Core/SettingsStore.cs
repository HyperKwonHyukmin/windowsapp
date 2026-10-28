using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskKeeper.Core;

/// <summary>
/// settings.json 읽기·쓰기. 사용자가 메모장으로 고칠 수 있으므로
/// 쓰기 전에 항상 다시 읽고, 파일에 오류가 있으면 덮어쓰지 않는다.
/// </summary>
public sealed class SettingsStore
{
    // 리플렉션 없이 소스 생성기로 직렬화해 상주 메모리를 줄인다. 한글은 \uXXXX 로 바꾸지 않는다.
    private static readonly SettingsJsonContext Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private bool _loaded;
    private DateTime _stamp;

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? AppPaths.SettingsFile;
    }

    public string FilePath { get; }

    public AppSettings Current { get; private set; } = new();

    /// <summary>마지막으로 읽을 때의 오류. null 이 아니면 저장을 거부한다.</summary>
    public string? LoadError { get; private set; }

    /// <summary>파일이 바뀌었을 때만 다시 읽는다. 다시 읽었으면 true.</summary>
    public bool ReloadIfChanged()
    {
        bool exists = File.Exists(FilePath);
        var stamp = exists ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
        if (_loaded && stamp == _stamp)
            return false;

        _loaded = true;
        _stamp = stamp;
        if (!exists)
        {
            Current = new AppSettings();
            LoadError = null;
            return true;
        }

        try
        {
            using var fs = File.OpenRead(FilePath);
            Current = Normalize(JsonSerializer.Deserialize(fs, Json.AppSettings));
            LoadError = null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 이전 설정은 그대로 두고 오류만 기록한다
            LoadError = ex.Message;
        }
        return true;
    }

    public void Save()
    {
        if (LoadError is not null)
            throw new InvalidOperationException("설정 파일에 오류가 있어 저장하지 않았습니다. 먼저 settings.json 을 고쳐 주세요.");

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
        // 쓰다가 끊겨도 원본이 깨지지 않게 임시 파일에 쓴 뒤 바꿔 넣는다
        var tmp = FilePath + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(Current, Json.AppSettings));
        File.Move(tmp, FilePath, overwrite: true);
        _stamp = File.GetLastWriteTimeUtc(FilePath);
    }

    /// <summary>
    /// 메모장으로 열기 전에 호출. 파일이 없으면 만들고, 감시 폴더가 비어 있으면 기본값을 써 넣어
    /// 사용자가 어떤 키를 고치면 되는지 보이게 한다.
    /// </summary>
    public void EnsureFileExists()
    {
        ReloadIfChanged();
        if (LoadError is not null)
            return;
        if (File.Exists(FilePath) && Current.WatchedFolders is not null)
            return;
        Current.WatchedFolders ??= Expiry.DefaultWatchedFolders();
        Save();
    }

    /// <summary>파일들에 날짜를 붙인다(임시 표시·보류). 같은 경로의 기존 표시는 바꾼다.</summary>
    public void SetMarks(IEnumerable<string> paths, DateTime until)
    {
        ReloadIfChanged();
        foreach (var raw in paths)
        {
            var path = System.IO.Path.GetFullPath(raw);
            Current.Marks.RemoveAll(m => SamePath(m.Path, path));
            Current.Marks.Add(new FileMark { Path = path, Until = until });
        }
        Save();
    }

    public void RemoveMarks(IEnumerable<string> paths)
    {
        ReloadIfChanged();
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        if (Current.Marks.RemoveAll(m => set.Contains(m.Path)) > 0)
            Save();
    }

    /// <summary>이미 없어진 파일의 표시를 정리한다. 설정 파일에 오류가 있으면 아무것도 하지 않는다.</summary>
    public void RemoveMissingMarks()
    {
        ReloadIfChanged();
        if (LoadError is null && Current.Marks.RemoveAll(m => !File.Exists(m.Path)) > 0)
            Save();
    }

    /// <summary>분류 규칙을 고칠 수 있게, 파일에 없으면 기본 규칙을 써 넣는다.</summary>
    public void EnsureCategoriesWritten()
    {
        ReloadIfChanged();
        if (Current.Categories is { Count: > 0 } && File.Exists(FilePath))
            return;
        Current.Categories ??= [];
        if (Current.Categories.Count == 0)
            Current.Categories.AddRange(Categorizer.Defaults());
        Save();
    }

    /// <summary>경로들을 런처에 추가한다. 이미 있는 경로는 건너뛴다. 추가한 개수를 돌려준다.</summary>
    public int AddLauncherItems(IEnumerable<string> paths, string? group = null)
    {
        ReloadIfChanged();
        group = CleanGroup(group);
        int added = 0;
        foreach (var path in paths)
        {
            if (Current.Launcher.Exists(i => SamePath(i.Path, path)))
                continue;
            Current.Launcher.Add(new LauncherItem { Name = DefaultName(path), Path = path, Group = group });
            added++;
        }
        if (added > 0)
        {
            RegisterGroup(group);
            Save();
        }
        return added;
    }

    /// <summary>항목의 분류를 바꾼다. group 이 null 이면 "분류 없음".</summary>
    public void SetLauncherGroup(string path, string? group)
    {
        ReloadIfChanged();
        group = CleanGroup(group);
        var item = Current.Launcher.Find(i => SamePath(i.Path, path));
        if (item is null)
            return;
        item.Group = group;
        RegisterGroup(group);
        Save();
    }

    /// <summary>분류를 순서 목록에서 앞(-1)이나 뒤(+1)로 옮긴다.</summary>
    public void MoveLauncherGroup(string group, int delta)
    {
        ReloadIfChanged();
        var groups = Current.LauncherGroups;
        int from = groups.FindIndex(g => SameGroup(g, group));
        int to = from + delta;
        if (from < 0 || to < 0 || to >= groups.Count)
            return;
        (groups[from], groups[to]) = (groups[to], groups[from]);
        Save();
    }

    /// <summary>분류 이름을 바꾼다. 이미 있는 이름이면 그 분류로 합친다.</summary>
    public void RenameLauncherGroup(string oldName, string newName)
    {
        ReloadIfChanged();
        var clean = CleanGroup(newName);
        if (clean is null || SameGroup(oldName, clean))
            return;
        foreach (var item in Current.Launcher.Where(i => i.Group is not null && SameGroup(i.Group, oldName)))
            item.Group = clean;
        var groups = Current.LauncherGroups;
        int index = groups.FindIndex(g => SameGroup(g, oldName));
        bool exists = groups.Exists(g => SameGroup(g, clean));
        if (index >= 0 && exists)
            groups.RemoveAt(index);
        else if (index >= 0)
            groups[index] = clean;
        else
            RegisterGroup(clean);
        Save();
    }

    private void RegisterGroup(string? group)
    {
        if (group is not null && !Current.LauncherGroups.Exists(g => SameGroup(g, group)))
            Current.LauncherGroups.Add(group);
    }

    private static string? CleanGroup(string? group) =>
        string.IsNullOrWhiteSpace(group) ? null : group.Trim();

    private static bool SameGroup(string a, string b) =>
        string.Equals(a, b, StringComparison.CurrentCultureIgnoreCase);

    /// <summary>목록에서만 뺀다. 실제 파일은 건드리지 않는다.</summary>
    public void RemoveLauncherItem(string path)
    {
        ReloadIfChanged();
        if (Current.Launcher.RemoveAll(i => SamePath(i.Path, path)) > 0)
            Save();
    }

    private static AppSettings Normalize(AppSettings? settings)
    {
        settings ??= new AppSettings();
        settings.Launcher ??= [];
        settings.Marks ??= [];
        settings.Marks.RemoveAll(m => m is null || string.IsNullOrWhiteSpace(m.Path));
        settings.WatchedFolders?.RemoveAll(w => w is null || string.IsNullOrWhiteSpace(w.Path));
        if (settings.MarkDays <= 0)
            settings.MarkDays = 30;
        // 손으로 고치다 빠진 값 보정: 경로 없는 항목은 버리고, 이름이 없으면 파일 이름을 쓴다
        settings.Launcher.RemoveAll(i => i is null || string.IsNullOrWhiteSpace(i.Path));
        settings.LauncherGroups ??= [];
        settings.LauncherGroups.RemoveAll(string.IsNullOrWhiteSpace);
        foreach (var item in settings.Launcher)
        {
            if (string.IsNullOrWhiteSpace(item.Name))
                item.Name = DefaultName(item.Path);
            item.Group = CleanGroup(item.Group);
            // 손으로 항목에만 분류를 적었어도 순서 목록에 넣는다
            if (item.Group is { } g && !settings.LauncherGroups.Exists(x => SameGroup(x, g)))
                settings.LauncherGroups.Add(g);
        }
        return settings;
    }

    private static string DefaultName(string path)
    {
        // 폴더는 폴더 이름, 파일은 확장자를 뺀 이름. 드라이브 루트(C:\)처럼 이름이 없으면 경로 그대로
        var trimmed = path.TrimEnd('\\', '/');
        var name = Directory.Exists(path)
            ? System.IO.Path.GetFileName(trimmed)
            : System.IO.Path.GetFileNameWithoutExtension(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext
{
}
