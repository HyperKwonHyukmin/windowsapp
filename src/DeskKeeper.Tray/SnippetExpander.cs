using System.Diagnostics;
using System.Runtime.InteropServices;
using DeskKeeper.Core;

namespace DeskKeeper.Tray;

/// <summary>
/// 문구 입력기: 공백·줄 처음 뒤에서 "@단어"를 치면 정해 둔 문구로 바꾼다.
/// - 단어가 정확히 맞고 더 긴 후보가 없으면 즉시, 더 긴 후보가 있으면 스페이스·Enter·Tab 에서 바꾼다.
/// - 영문 입력 상태에서만 동작한다(한글은 입력기가 조합 중이라 친 단어를 믿을 수 없다).
/// - 저수준 키보드 훅(WH_KEYBOARD_LL)을 쓴다. 콜백은 설치한 UI 스레드에서 불리므로 짧게 끝낸다.
/// </summary>
sealed class SnippetExpander : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int LlkhfInjected = 0x10;
    private const int VkBack = 0x08, VkTab = 0x09, VkReturn = 0x0D, VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;
    private const int VkSpace = 0x20, VkLWin = 0x5B, VkRWin = 0x5C, VkLShift = 0xA0, VkRShift = 0xA1, VkCapital = 0x14;
    private const int VkLControl = 0xA2, VkRControl = 0xA3, VkLMenu = 0xA4, VkRMenu = 0xA5, VkHangul = 0x15;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private readonly SettingsStore _store;
    private readonly Control _ui;
    private readonly HookProc _proc;   // 대리자가 GC 로 사라지지 않게 붙잡아 둔다
    private IntPtr _hook;

    /// <summary>@ 뒤에 친 글자들. null 이면 지금 @단어를 치는 중이 아니다.</summary>
    private string? _word;
    /// <summary>직전 글자가 공백·줄바꿈이거나 입력을 막 시작했는지(@ 를 받아들일 자리인지).</summary>
    private bool _atBoundary = true;
    private IntPtr _lastWindow;
    private bool _sending;

    public SnippetExpander(SettingsStore store, Control ui)
    {
        _store = store;
        _ui = ui;
        _proc = HookCallback;
    }

    public bool IsRunning => _hook != IntPtr.Zero;

    /// <summary>훅을 건다. 실패하면(보안 프로그램이 막는 등) false.</summary>
    public bool Start()
    {
        if (_hook != IntPtr.Zero)
            return true;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(module?.ModuleName), 0);
        Reset(boundary: true);
        return _hook != IntPtr.Zero;
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
            UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        Reset(boundary: true);
    }

    public void Dispose() => Stop();

    private void Reset(bool boundary)
    {
        _word = null;
        _atBoundary = boundary;
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown) && !_sending)
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            // 우리가 SendInput 으로 넣은 키는 무시한다(바꿔 넣는 글자를 다시 읽지 않게)
            if ((info.Flags & LlkhfInjected) == 0 && OnKey((int)info.VkCode))
                return 1;   // 이 키를 앱에 전달하지 않는다(바꿀 때 끝 글자로 쓴 스페이스·Enter)
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>키 하나를 본다. true 를 돌려주면 그 키는 삼킨다.</summary>
    private bool OnKey(int vk)
    {
        // 다른 창으로 옮겨 가면 입력 위치를 모르므로 처음부터
        var window = GetForegroundWindow();
        if (window != _lastWindow)
        {
            _lastWindow = window;
            Reset(boundary: true);
        }

        if (vk is VkShift or VkLShift or VkRShift or VkCapital)
            return false;
        // Ctrl·Alt·Win 조합(단축키)이나 한/영 전환은 문구 입력이 아니다
        if (vk is VkControl or VkLControl or VkRControl or VkMenu or VkLMenu or VkRMenu or VkLWin or VkRWin or VkHangul
            || IsDown(VkControl) || IsDown(VkMenu) || IsDown(VkLWin) || IsDown(VkRWin))
        {
            Reset(boundary: false);
            return false;
        }

        bool shift = IsDown(VkShift);

        if (_word is null)
        {
            // Shift+2 = @ (영문·한글 자판 공통 위치)
            if (vk == 0x32 && shift && _atBoundary && IsEnglishInput(_lastWindow))
            {
                _store.ReloadIfChanged();
                if (_store.Current.Snippets.Count > 0)
                    _word = "";
                _atBoundary = false;
                return false;
            }
            _atBoundary = vk is VkSpace or VkReturn or VkTab;
            return false;
        }

        // 여기부터 @단어를 치는 중
        if (vk == VkBack)
        {
            if (_word.Length == 0)
                Reset(boundary: true);   // @ 를 지웠다
            else
                _word = _word[..^1];
            return false;
        }

        if (vk is VkSpace or VkReturn or VkTab)
        {
            var key = _word;
            Reset(boundary: true);
            if (Lookup(key) is { } text)
            {
                // 끝 글자로 친 키는 삼키고, 바꾼 뒤에 다시 넣는다(Enter 로 폼이 먼저 제출되지 않게).
                // 훅 콜백 안에서 SendInput 을 부르지 않도록 콜백이 끝난 뒤 UI 루프에서 보낸다
                int typed = key.Length + 1;
                _ui.BeginInvoke(() => Expand(typed, text, vk));
                return true;
            }
            return false;
        }

        char? ch = vk switch
        {
            >= 0x41 and <= 0x5A => (char)('a' + vk - 0x41),        // 대문자도 같은 단어로 본다
            >= 0x30 and <= 0x39 when !shift => (char)('0' + vk - 0x30),
            _ => null,
        };
        if (ch is null)
        {
            // 영문·숫자가 아닌 글자: 문구 단어가 아니다
            Reset(boundary: false);
            return false;
        }

        _word += ch;
        // 정확히 맞고 더 긴 후보가 없으면 즉시 바꾼다. 이 키는 앱에 전달된 뒤 지우므로 삼키지 않는다
        if (Lookup(_word) is { } now && !HasLongerKey(_word))
        {
            int typed = _word.Length + 1;
            Reset(boundary: false);
            _ui.BeginInvoke(() => Expand(typed, now, null));
            return false;
        }
        if (!HasPrefix(_word))
            Reset(boundary: false);
        return false;
    }

    private string? Lookup(string word) =>
        word.Length > 0 && _store.Current.Snippets.TryGetValue(word, out var text) ? text : null;

    private bool HasLongerKey(string word) =>
        _store.Current.Snippets.Keys.Any(k => k.Length > word.Length && k.StartsWith(word, StringComparison.OrdinalIgnoreCase));

    private bool HasPrefix(string word) =>
        _store.Current.Snippets.Keys.Any(k => k.StartsWith(word, StringComparison.OrdinalIgnoreCase));

    private static bool IsDown(int vk) => GetAsyncKeyState(vk) < 0;

    /// <summary>친 "@단어"를 지우고 문구를 넣는다. terminator 가 있으면 그 키를 마지막에 다시 누른다.</summary>
    private void Expand(int typedCount, string template, int? terminator)
    {
        var text = template
            .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"))
            .Replace("{time}", DateTime.Now.ToString("HH:mm"))
            .Replace("\r\n", "\n");
        var inputs = new List<Input>();
        for (int i = 0; i < typedCount; i++)
            AddKey(inputs, VkBack);
        foreach (var c in text)
        {
            if (c == '\n')
                AddKey(inputs, VkReturn);
            else
                AddChar(inputs, c);
        }
        if (terminator is { } t)
            AddKey(inputs, t);

        _sending = true;
        try
        {
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
        }
        finally
        {
            _sending = false;
        }
    }

    // ── SendInput ─────────────────────────────────────────

    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyUp = 0x0002;
    private const uint KeyeventfUnicode = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    // INPUT 공용체: 가장 큰 멤버(MOUSEINPUT) 크기에 맞춰 둔다
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeybdInput Keyboard;
        [FieldOffset(0)] private readonly MouseInputPad _pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputPad
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    private static void AddKey(List<Input> list, int vk)
    {
        list.Add(new Input { Type = InputKeyboard, U = new InputUnion { Keyboard = new KeybdInput { Vk = (ushort)vk } } });
        list.Add(new Input { Type = InputKeyboard, U = new InputUnion { Keyboard = new KeybdInput { Vk = (ushort)vk, Flags = KeyeventfKeyUp } } });
    }

    /// <summary>글자를 유니코드로 직접 넣는다(한글도 입력기를 거치지 않고 들어간다).</summary>
    private static void AddChar(List<Input> list, char c)
    {
        list.Add(new Input { Type = InputKeyboard, U = new InputUnion { Keyboard = new KeybdInput { Scan = c, Flags = KeyeventfUnicode } } });
        list.Add(new Input { Type = InputKeyboard, U = new InputUnion { Keyboard = new KeybdInput { Scan = c, Flags = KeyeventfUnicode | KeyeventfKeyUp } } });
    }

    // ── 영문 입력 상태 확인 ───────────────────────────────

    private const int WmImeControl = 0x0283;
    private const int ImcGetConversionMode = 0x0001;
    private const int ImeCmodeNative = 0x0001;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    /// <summary>
    /// 앞 창의 입력기가 영문 상태인지. 한글(NATIVE) 상태면 false. 알 수 없으면 영문으로 본다.
    /// 훅 콜백이 오래 걸리면 Windows 가 훅을 떼므로 짧은 제한 시간으로 묻는다.
    /// </summary>
    private static bool IsEnglishInput(IntPtr window)
    {
        var ime = ImmGetDefaultIMEWnd(window);
        if (ime == IntPtr.Zero)
            return true;
        var ok = SendMessageTimeout(ime, WmImeControl, ImcGetConversionMode, IntPtr.Zero, SmtoAbortIfHung, 30, out var mode);
        return ok == IntPtr.Zero || ((long)mode & ImeCmodeNative) == 0;
    }
}
