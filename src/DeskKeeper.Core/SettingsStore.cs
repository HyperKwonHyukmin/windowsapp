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

    /// <summary>파일이 없으면 빈 설정으로 만든다(메모장으로 열기 전에 호출).</summary>
    public void EnsureFileExists()
    {
        ReloadIfChanged();
        if (!File.Exists(FilePath))
            Save();
    }

    /// <summary>경로들을 런처에 추가한다. 이미 있는 경로는 건너뛴다. 추가한 개수를 돌려준다.</summary>
    public int AddLauncherItems(IEnumerable<string> paths)
    {
        ReloadIfChanged();
        int added = 0;
        foreach (var path in paths)
        {
            if (Current.Launcher.Exists(i => SamePath(i.Path, path)))
                continue;
            Current.Launcher.Add(new LauncherItem { Name = DefaultName(path), Path = path });
            added++;
        }
        if (added > 0)
            Save();
        return added;
    }

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
        // 손으로 고치다 빠진 값 보정: 경로 없는 항목은 버리고, 이름이 없으면 파일 이름을 쓴다
        settings.Launcher.RemoveAll(i => i is null || string.IsNullOrWhiteSpace(i.Path));
        foreach (var item in settings.Launcher)
        {
            if (string.IsNullOrWhiteSpace(item.Name))
                item.Name = DefaultName(item.Path);
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
