using System.IO.Enumeration;

namespace DeskKeeper.Core;

/// <summary>
/// 정리 후보 판단. 표시(marks)가 있으면 그 날짜를 따르고(지났으면 후보, 아직이면 보류),
/// 없으면 감시 폴더의 기한(마지막 수정 후 N일)을 따른다.
/// </summary>
public static class Expiry
{
    public const string MarkedGroupName = "임시 표시";

    public static List<WatchedFolder> DefaultWatchedFolders() =>
    [
        new() { Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), Days = 30 },
    ];

    /// <summary>경로를 정규화한 사본(손으로 쓴 "C:/..." 도 표시 경로와 맞게).</summary>
    public static List<WatchedFolder> EffectiveWatchedFolders(AppSettings settings) =>
        (settings.WatchedFolders ?? DefaultWatchedFolders())
            .Select(w => new WatchedFolder { Path = SafeFullPath(w.Path), Days = Math.Max(0, w.Days) })
            .Where(w => w.Path.Length > 0)
            .ToList();

    private static string SafeFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "";
        }
    }

    /// <summary>
    /// 상주 프로그램용. 파일 목록을 만들지 않고 개수만 센다.
    /// </summary>
    public static int CountExpired(AppSettings settings, DateTime now, CancellationToken ct)
    {
        var marks = MarkMap(settings);
        var watched = EffectiveWatchedFolders(settings);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FolderScanner.SkipAttributes,
        };

        int count = 0;
        foreach (var folder in watched)
        {
            if (!Directory.Exists(folder.Path))
                continue;
            var cutoff = now.AddDays(-folder.Days);
            var files = new FileSystemEnumerable<(string Path, DateTime LastWrite)>(folder.Path,
                (ref FileSystemEntry e) => (e.ToFullPath(), e.LastWriteTimeUtc.LocalDateTime), options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            };
            try
            {
                foreach (var (path, lastWrite) in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (IsCandidate(path, lastWrite, cutoff, marks, now))
                        count++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 폴더 하나를 못 읽어도 나머지는 센다
            }
        }

        foreach (var mark in settings.Marks)
        {
            if (mark.Until <= now && !IsUnderAny(mark.Path, watched) && File.Exists(mark.Path))
                count++;
        }
        return count;
    }

    /// <summary>
    /// 분석 창용. 감시 폴더별 후보와 감시 폴더 밖의 임시 표시 파일을 가상 루트 하나 아래에 모은다.
    /// </summary>
    public static ScanResult ScanCandidates(AppSettings settings, Categorizer categorizer, IProgress<int>? progress, CancellationToken ct)
    {
        var now = DateTime.Now;
        var marks = MarkMap(settings);
        var watched = EffectiveWatchedFolders(settings);
        var root = new DirNode("정리 후보", "", ".", null);
        var files = new List<FileEntry>();
        int skipped = 0;

        foreach (var folder in watched)
        {
            if (!Directory.Exists(folder.Path))
                continue;
            var cutoff = now.AddDays(-folder.Days);
            var result = FolderScanner.Scan(folder.Path, categorizer, progress, ct, root,
                (path, lastWrite) => IsCandidate(path, lastWrite, cutoff, marks, now));
            files.AddRange(result.Files);
            skipped += result.SkippedDirs;
        }
        root.Children.RemoveAll(c => c.TotalFiles == 0);

        var outside = settings.Marks
            .Where(m => m.Until <= now && !IsUnderAny(m.Path, watched) && File.Exists(m.Path))
            .GroupBy(m => Path.GetDirectoryName(m.Path) ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (outside.Count > 0)
        {
            var group = new DirNode(MarkedGroupName, "", MarkedGroupName, root);
            root.Children.Add(group);
            foreach (var g in outside)
            {
                var dir = new DirNode(g.Key, g.Key, Path.Combine(MarkedGroupName, Path.GetFileName(g.Key)), group);
                group.Children.Add(dir);
                foreach (var mark in g)
                {
                    try
                    {
                        var info = new FileInfo(mark.Path);
                        files.Add(new FileEntry(dir, info.Name, info.Length, info.LastWriteTime, categorizer.Classify(info.Name)));
                        FolderScanner.AddToTotals(dir, info.Length);
                    }
                    catch (IOException)
                    {
                        // 확인 직후 지워진 파일은 건너뛴다
                    }
                }
            }
        }

        return new ScanResult(root, files, skipped);
    }

    private static bool IsCandidate(string path, DateTime lastWrite, DateTime cutoff, Dictionary<string, DateTime> marks, DateTime now) =>
        marks.TryGetValue(path, out var until) ? until <= now : lastWrite < cutoff;

    private static Dictionary<string, DateTime> MarkMap(AppSettings settings)
    {
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in settings.Marks)
            map[m.Path] = m.Until;
        return map;
    }

    private static bool IsUnderAny(string path, List<WatchedFolder> watched) =>
        watched.Any(w => path.StartsWith(w.Path + "\\", StringComparison.OrdinalIgnoreCase));
}
