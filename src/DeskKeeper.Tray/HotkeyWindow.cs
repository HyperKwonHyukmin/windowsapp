using System.Runtime.InteropServices;

namespace DeskKeeper.Tray;

/// <summary>전역 단축키를 받는 보이지 않는 메시지 전용 창.</summary>
sealed class HotkeyWindow : NativeWindow, IDisposable
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 1;
    private static readonly IntPtr HwndMessage = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public HotkeyWindow(uint modifiers, Keys key)
    {
        CreateHandle(new CreateParams { Parent = HwndMessage });
        // 다른 프로그램이 이미 쓰는 조합이면 false
        IsRegistered = RegisterHotKey(Handle, HotkeyId, modifiers | ModNoRepeat, (uint)key);
    }

    public bool IsRegistered { get; }

    public event Action? Pressed;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && m.WParam == HotkeyId)
            Pressed?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (IsRegistered)
            UnregisterHotKey(Handle, HotkeyId);
        DestroyHandle();
    }
}
