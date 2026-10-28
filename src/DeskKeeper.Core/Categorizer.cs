namespace DeskKeeper.Core;

/// <summary>확장자로 파일 분류를 정한다. 같은 확장자가 여러 규칙에 있으면 앞의 규칙이 이긴다.</summary>
public sealed class Categorizer
{
    public const string Other = "기타";

    private readonly Dictionary<string, string> _byExtension = new(StringComparer.OrdinalIgnoreCase);

    public Categorizer(IEnumerable<CategoryRule> rules)
    {
        var names = new List<string>();
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Name))
                continue;
            // 같은 문자열 인스턴스를 공유해 파일 수만큼 이름이 복사되지 않게 한다
            var name = string.Intern(rule.Name.Trim());
            if (!names.Contains(name))
                names.Add(name);
            foreach (var ext in rule.Extensions ?? [])
            {
                var key = ext.Trim();
                if (key.Length == 0)
                    continue;
                if (key[0] != '.')
                    key = "." + key;
                _byExtension.TryAdd(key, name);
            }
        }
        if (!names.Contains(Other))
            names.Add(Other);
        Names = names;
    }

    /// <summary>필터 목록에 보여 줄 분류 이름(규칙 순서, 마지막은 "기타").</summary>
    public IReadOnlyList<string> Names { get; }

    public string Classify(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Length > 0 && _byExtension.TryGetValue(ext, out var name) ? name : Other;
    }

    public static Categorizer FromSettings(AppSettings settings) =>
        new(settings.Categories is { Count: > 0 } rules ? rules : Defaults());

    /// <summary>기본 분류. 조선 구조해석 작업에서 자주 쌓이는 파일을 앞에 둔다.</summary>
    public static List<CategoryRule> Defaults() =>
    [
        Rule("해석 결과", ".op2", ".xdb", ".h5", ".f06", ".f04", ".pch", ".master", ".dball", ".odb"),
        Rule("해석 입력", ".bdf", ".dat", ".nas", ".fem", ".inp"),
        Rule("CAD", ".dwg", ".dxf", ".step", ".stp", ".igs", ".iges", ".x_t", ".sat"),
        Rule("설치·압축", ".msi", ".exe", ".zip", ".7z", ".rar", ".iso", ".cab", ".tar", ".gz"),
        Rule("문서", ".pdf", ".hwp", ".hwpx", ".doc", ".docx", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".txt", ".csv", ".md"),
        Rule("이미지·영상", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".mp4", ".avi", ".mov", ".mkv"),
        Rule("임시", ".tmp", ".temp", ".bak", ".old", ".log"),
    ];

    private static CategoryRule Rule(string name, params string[] extensions) =>
        new() { Name = name, Extensions = [.. extensions] };
}
