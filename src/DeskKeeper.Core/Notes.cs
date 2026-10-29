using System.Text;

namespace DeskKeeper.Core;

/// <summary>메모 파일 하나. 제목은 확장자를 뺀 파일 이름이다.</summary>
public sealed record NoteFile(string Path, string Title, DateTime Modified);

/// <summary>
/// 메모 폴더(메모 하나 = .txt/.md 파일 하나). 런처 메모 화면을 열 때만 읽고 상주 중에는 아무것도 들고 있지 않는다.
/// </summary>
public static class Notes
{
    public static readonly string[] Extensions = [".txt", ".md"];

    /// <summary>미리보기·검색에서 읽는 최대 크기. 이보다 큰 파일은 앞부분만 읽는다.</summary>
    public const int MaxReadBytes = 1024 * 1024;

    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DeskKeeper", "메모");

    /// <summary>
    /// 예전 메모장(ANSI) 파일의 CP949. .NET 기본에는 없어서 등록해야 하는데, 변환표가 상주 메모리를 늘리므로
    /// UTF-8 로 읽다 깨진 파일을 처음 만났을 때만 등록한다.
    /// </summary>
    private static Encoding Cp949
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(949);
        }
    }

    /// <summary>설정의 notesFolder(환경 변수 가능), 없으면 문서\DeskKeeper\메모.</summary>
    public static string FolderOf(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.NotesFolder)
            ? DefaultFolder
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.NotesFolder.Trim()));

    public static bool IsNoteFile(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>폴더 바로 아래의 메모를 최근 수정순으로. 폴더가 없으면 빈 목록.</summary>
    public static List<NoteFile> List(string folder)
    {
        if (!Directory.Exists(folder))
            return [];
        return new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => IsNoteFile(f.Name))
            .Select(f => new NoteFile(f.FullName, Path.GetFileNameWithoutExtension(f.Name), f.LastWriteTime))
            .OrderByDescending(n => n.Modified)
            .ToList();
    }

    /// <summary>내용을 읽는다(앞 1MB). BOM 이 없으면 UTF-8 로 읽어 보고, 깨지면 CP949 로 읽는다.</summary>
    public static string Read(string path)
    {
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[Math.Min(stream.Length, MaxReadBytes)];
            stream.ReadExactly(bytes);
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }))
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }))
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Cp949.GetString(bytes);
        }
    }

    /// <summary>빈 메모(.md)를 만든다. 이름에 못 쓰는 글자는 _ 로 바꾸고, 같은 이름이 있으면 (2)·(3)을 붙인다.</summary>
    public static string Create(string folder, string title)
    {
        Directory.CreateDirectory(folder);
        var safe = string.Concat(title.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim('.', ' ');
        if (safe.Length == 0)
            safe = "새 메모";
        var path = UniquePath(folder, safe, ".md");
        Save(path, "");
        return path;
    }

    /// <summary>
    /// 내용을 저장한다. .txt 는 UTF-8(BOM, 예전 메모장도 한글을 바로 읽게), .md 는 BOM 없는 UTF-8(마크다운 도구의 표준).
    /// 예전 CP949 파일도 저장하면 UTF-8 이 된다.
    /// </summary>
    public static void Save(string path, string text)
    {
        bool bom = !string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase);
        // 쓰다가 끊겨도 원본이 반쯤 잘리지 않게 임시 파일에 쓰고 바꿔 끼운다
        var temp = path + ".dksave";
        File.WriteAllText(temp, text, new UTF8Encoding(bom));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>같은 폴더에 겹치지 않는 이름을 만든다(저장 충돌 때 사본 이름).</summary>
    public static string SiblingPath(string path, string suffix) =>
        UniquePath(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + suffix, Path.GetExtension(path));

    /// <summary>
    /// 흩어진 메모 파일을 메모 폴더로 옮긴다(복사 후 원본 삭제가 아니라 이동). 이미 폴더 안에 있거나
    /// .txt/.md 가 아니면 건너뛴다. 같은 이름이 있으면 (2)를 붙인다. 옮긴 개수를 돌려준다.
    /// </summary>
    public static int MoveIn(string folder, IEnumerable<string> paths)
    {
        Directory.CreateDirectory(folder);
        var root = Path.GetFullPath(folder).TrimEnd('\\');
        int moved = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !IsNoteFile(path))
                continue;
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), root, StringComparison.OrdinalIgnoreCase))
                continue;
            var target = UniquePath(folder, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path));
            File.Move(path, target);
            moved++;
        }
        return moved;
    }

    private static string UniquePath(string folder, string name, string extension)
    {
        var path = Path.Combine(folder, name + extension);
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{name} ({n}){extension}");
        return path;
    }
}
