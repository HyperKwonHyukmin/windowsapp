using System.Runtime.InteropServices;

namespace DeskKeeper.Tray;

/// <summary>테두리 없는 창을 Windows 11 플라이아웃처럼 보이게 하는 DWM·컨트롤 설정.</summary>
static class WindowChrome
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRound = 2;
    private const int EmSetCueBanner = 0x1501;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    /// <summary>둥근 모서리와 얇은 테두리색. Windows 10 에서는 아무 일도 하지 않는다(실패를 무시).</summary>
    public static void ApplyRoundedCorners(IntPtr hwnd, Color border)
    {
        int round = DwmwcpRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
        int colorRef = border.R | (border.G << 8) | (border.B << 16);
        DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref colorRef, sizeof(int));
    }

    /// <summary>포커스가 있어도 보이는 안내 문구(EM_SETCUEBANNER, wParam=1).</summary>
    public static void SetCueBanner(TextBox box, string text)
    {
        SendMessage(box.Handle, EmSetCueBanner, 1, text);
    }
}

/// <summary>
/// 런처 목록에 그릴 셸 아이콘(작은 크기). 런처 창과 함께 만들어지고 닫힐 때 모두 해제한다.
/// </summary>
sealed class ShellIconCache : IDisposable
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly Dictionary<string, (IntPtr Handle, Icon? Icon)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="large">true 면 큰 아이콘(보통 32px, 화면 배율에 따라 커짐), false 면 작은 아이콘.</param>
    public Icon? Get(string path, bool large = false)
    {
        var key = (large ? "L|" : "S|") + path;
        if (_cache.TryGetValue(key, out var hit))
            return hit.Icon;

        var info = new ShFileInfo();
        // 없는 경로(네트워크 끊김 등)에서 셸이 오래 기다리지 않게 존재할 때만 묻는다
        if (File.Exists(path) || Directory.Exists(path))
            SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | (large ? 0 : ShgfiSmallIcon));
        var handle = info.Icon;
        var icon = handle != IntPtr.Zero ? Icon.FromHandle(handle) : null;
        _cache[key] = (handle, icon);
        return icon;
    }

    public void Dispose()
    {
        foreach (var (handle, icon) in _cache.Values)
        {
            icon?.Dispose();
            if (handle != IntPtr.Zero)
                DestroyIcon(handle);
        }
        _cache.Clear();
    }
}
