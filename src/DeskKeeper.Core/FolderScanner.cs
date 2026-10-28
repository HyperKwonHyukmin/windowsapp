using System.IO.Enumeration;

namespace DeskKeeper.Core;

/// <summary>폴더 하나. 크기·파일 수는 하위 폴더를 모두 합한 값이다.</summary>
public sealed class DirNode
{
    public DirNode(string name, string fullPath, string relativePath, DirNode? parent)
    {
        Name = name;
        FullPath = fullPath;
        RelativePath = relativePath;
        Parent = parent;
    }

    public string Name { get; }
    public string FullPath { get; }
    /// <summary>스캔 시작 폴더 기준 경로. 시작 폴더는 ".".</summary>
    public string RelativePath { get; }
    public DirNode? Parent { get; }
    public List<DirNode> Children { get; } = [];
    public long TotalSize { get; set; }
    public int TotalFiles { get; set; }

    public bool IsSameOrUnder(DirNode ancestor)
    {
        for (var d = this; d is not null; d = d.Parent)
        {
            if (d == ancestor)
                return true;
        }
        return false;
    }
}

public sealed class FileEntry
{
    public FileEntry(DirNode dir, string name, long size, DateTime lastWriteTime, string category)
    {
        Dir = dir;
        Name = name;
        Size = size;
        LastWriteTime = lastWriteTime;
        Category = category;
    }

    public DirNode Dir { get; }
    public string Name { get; }
    public long Size { get; }
    public DateTime LastWriteTime { get; }
    public string Category { get; }
    /// <summary>삭제 후보로 체크했는지. 필터를 바꿔도 유지된다.</summary>
    public bool Checked { get; set; }

    public string FullPath => Path.Combine(Dir.FullPath, Name);
}

public sealed class ScanResult
{
    public ScanResult(DirNode root, List<FileEntry> files, int skippedDirs)
    {
        Root = root;
        Files = files;
        SkippedDirs = skippedDirs;
    }

    public DirNode Root { get; }
    public List<FileEntry> Files { get; }
    /// <summary>권한 등으로 읽지 못한 폴더 수.</summary>
    public int SkippedDirs { get; }

    /// <summary>파일을 목록에서 빼고 상위 폴더 합계를 줄인다(휴지통으로 보낸 뒤 호출).</summary>
    public void Remove(IReadOnlyCollection<FileEntry> removed)
    {
        if (removed.Count == 0)
            return;
        var set = removed as HashSet<FileEntry> ?? [.. removed];
        Files.RemoveAll(set.Contains);
        foreach (var f in set)
        {
            for (var d = f.Dir; d is not null; d = d.Parent)
            {
                d.TotalSize -= f.Size;
                d.TotalFiles--;
            }
        }
    }
}

public static class FolderScanner
{
    private readonly record struct Entry(string Name, bool IsDirectory, long Length, DateTime LastWriteTime);

    // 숨김 파일은 포함한다(임시 파일이 숨김인 경우가 많다). 시스템 파일과 연결 지점(무한 반복 위험)은 건너뛴다.
    internal static readonly FileAttributes SkipAttributes = FileAttributes.System | FileAttributes.ReparsePoint;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = SkipAttributes,
    };

    /// <param name="parent">여러 폴더를 한 트리로 묶을 때의 상위(가상) 노드. 합계도 그쪽으로 올라간다.</param>
    /// <param name="keep">넣을 파일만 고르는 조건(전체 경로, 수정 시각). 고른 경우 빈 폴더는 트리에서 뺀다.</param>
    public static ScanResult Scan(string rootPath, Categorizer categorizer, IProgress<int>? progress, CancellationToken ct,
        DirNode? parent = null, Func<string, DateTime, bool>? keep = null)
    {
        rootPath = Path.GetFullPath(rootPath);
        var rootName = Path.GetFileName(rootPath.TrimEnd('\\')) is { Length: > 0 } n ? n : rootPath;
        var root = new DirNode(rootName, rootPath, parent is null ? "." : rootName, parent);
        parent?.Children.Add(root);
        var files = new List<FileEntry>();
        int skipped = 0;
        var pending = new Stack<DirNode>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            List<Entry> entries;
            try
            {
                // FileInfo 를 만들지 않고 필요한 값만 꺼내 빠르게 읽는다
                entries = [.. new FileSystemEnumerable<Entry>(dir.FullPath,
                    (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Length, e.LastWriteTimeUtc.LocalDateTime),
                    Options)];
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                skipped++;
                continue;
            }

            foreach (var e in entries)
            {
                if (e.IsDirectory)
                {
                    var rel = dir.RelativePath == "." ? e.Name : Path.Combine(dir.RelativePath, e.Name);
                    var child = new DirNode(e.Name, Path.Combine(dir.FullPath, e.Name), rel, dir);
                    dir.Children.Add(child);
                    pending.Push(child);
                    continue;
                }

                if (keep is not null && !keep(Path.Combine(dir.FullPath, e.Name), e.LastWriteTime))
                    continue;
                files.Add(new FileEntry(dir, e.Name, e.Length, e.LastWriteTime, categorizer.Classify(e.Name)));
                AddToTotals(dir, e.Length);
                if (files.Count % 2000 == 0)
                    progress?.Report(files.Count);
            }
        }

        if (keep is not null)
            PruneEmpty(root);
        progress?.Report(files.Count);
        return new ScanResult(root, files, skipped);
    }

    /// <summary>폴더와 그 위의 모든 폴더(가상 노드 포함) 합계에 파일 하나를 더한다.</summary>
    public static void AddToTotals(DirNode dir, long size)
    {
        for (var d = dir; d is not null; d = d.Parent)
        {
            d.TotalSize += size;
            d.TotalFiles++;
        }
    }

    private static void PruneEmpty(DirNode dir)
    {
        dir.Children.RemoveAll(c => c.TotalFiles == 0);
        foreach (var child in dir.Children)
            PruneEmpty(child);
    }
}
