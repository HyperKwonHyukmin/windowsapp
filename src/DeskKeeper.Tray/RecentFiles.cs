using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using DeskKeeper.Core;

namespace DeskKeeper.Tray;

/// <summary>
/// Windows 최근 항목(%APPDATA%\Microsoft\Windows\Recent 의 바로가기)에서 최근 연 문서·폴더를 읽는다.
/// 런처를 열 때 한 번만 읽고 창과 함께 버린다.
/// </summary>
static class RecentFiles
{
    /// <summary>최근 것부터 count 개. 이미 등록한 경로·없어진 파일·바로가기(.lnk) 대상은 뺀다.</summary>
    public static List<LauncherItem> Read(int count, IEnumerable<string> registeredPaths)
    {
        var result = new List<LauncherItem>();
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (!Directory.Exists(dir))
            return result;

        var skip = new HashSet<string>(registeredPaths.Select(p => p.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
        IEnumerable<FileInfo> links;
        try
        {
            links = new DirectoryInfo(dir).EnumerateFiles("*.lnk").OrderByDescending(f => f.LastWriteTimeUtc).Take(count * 4).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }

        foreach (var link in links)
        {
            if (result.Count >= count)
                break;
            var target = ResolveTarget(link.FullName);
            if (string.IsNullOrEmpty(target))
                continue;
            target = target.TrimEnd('\\');
            // 등록된 것, 이미 넣은 것, 바로가기 자체, 사라진 파일(네트워크 경로는 확인이 느려 확인 없이 넣는다)
            if (!skip.Add(target) || target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!target.StartsWith(@"\\") && !File.Exists(target) && !Directory.Exists(target))
                continue;
            var name = Path.GetFileName(target);
            result.Add(new LauncherItem { Name = name.Length > 0 ? name : target, Path = target });
        }
        return result;
    }

    // ── IShellLink 로 바로가기 대상 읽기 ──────────────────────

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxPath, IntPtr findData, uint flags);
        // 나머지 메서드는 쓰지 않지만 순서를 맞추기 위해 이름만 둔다
        void GetIDList(out IntPtr idl);
        void SetIDList(IntPtr idl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    /// <summary>바로가기의 대상 경로. 읽지 못하면 null. (Resolve 는 부르지 않는다: 없는 대상을 찾느라 멈출 수 있음)</summary>
    private static string? ResolveTarget(string lnkPath)
    {
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(1024);
            ((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or FileNotFoundException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (link is not null)
                Marshal.ReleaseComObject(link);
        }
    }
}
