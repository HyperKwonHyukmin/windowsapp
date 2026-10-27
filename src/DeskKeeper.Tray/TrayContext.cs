using System.Diagnostics;
using DeskKeeper.Core;

namespace DeskKeeper.Tray;

/// <summary>창 없이 트레이 아이콘만 띄우는 상주 컨텍스트.</summary>
sealed class TrayContext : ApplicationContext
{
    private const string HotkeyText = "Ctrl+Alt+Space";

    private readonly SettingsStore _store = new();
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly HotkeyWindow _hotkey;
    private readonly System.Windows.Forms.Timer _trimTimer;
    private LauncherForm? _launcher;
    private string? _reportedLoadError;

    public TrayContext()
    {
        _menu = new ContextMenuStrip();
        // 메뉴는 열 때마다 새로 만든다(설정 파일을 직접 고친 내용을 바로 반영)
        _menu.Opening += OnMenuOpening;
        // 메뉴를 닫은 뒤 그리면서 늘어난 메모리를 다시 줄인다
        _menu.Closed += (_, _) => ScheduleTrim();

        _icon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "DeskKeeper",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ToggleLauncher();
        };

        _hotkey = new HotkeyWindow(HotkeyWindow.ModControl | HotkeyWindow.ModAlt, Keys.Space);
        _hotkey.Pressed += ToggleLauncher;
        if (!_hotkey.IsRegistered)
        {
            _icon.ShowBalloonTip(5000, "DeskKeeper",
                $"단축키 {HotkeyText}를 다른 프로그램이 쓰고 있어 등록하지 못했습니다. 트레이 아이콘을 눌러 런처를 여세요.",
                ToolTipIcon.Warning);
        }

        _trimTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _trimTimer.Tick += (_, _) =>
        {
            _trimTimer.Stop();
            MemoryTrimmer.Trim();
        };

        _store.ReloadIfChanged();
        ReportLoadError();
        // 시작 직후 초기화로 커진 작업 메모리를 정리
        ScheduleTrim();
    }

    private void ToggleLauncher()
    {
        if (_launcher is { IsDisposed: false })
        {
            _launcher.Close();
            return;
        }
        _launcher = new LauncherForm(_store);
        _launcher.FormClosed += (_, _) =>
        {
            _launcher = null;
            ReportLoadError();
            ScheduleTrim();
        };
        _launcher.Show();
    }

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _store.ReloadIfChanged();
        ReportLoadError();

        foreach (var old in _menu.Items.Cast<ToolStripItem>().ToList())
            old.Dispose();
        _menu.Items.Clear();

        var items = _store.Current.Launcher;
        if (items.Count == 0)
            _menu.Items.Add(new ToolStripMenuItem("(등록된 항목 없음)") { Enabled = false });
        foreach (var item in items)
            _menu.Items.Add(item.Name, null, (_, _) => ItemLauncher.Start(item)).ToolTipText = item.Path;

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("런처 열기", null, (_, _) => ToggleLauncher()) { ShortcutKeyDisplayString = HotkeyText });
        _menu.Items.Add("설정 파일 열기", null, (_, _) => OpenSettingsFile());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Windows 시작 시 실행", null, OnToggleAutoStart) { Checked = AutoStart.IsEnabled() });
        _menu.Items.Add("종료", null, (_, _) => ExitThread());

        // 처음에 항목이 비어 있으면 WinForms 가 열기를 취소해 두므로 되돌린다
        e.Cancel = false;
    }

    private void OpenSettingsFile()
    {
        try
        {
            _store.EnsureFileExists();
            // .json 연결 프로그램이 무거운 편집기일 수 있어 메모장으로 고정
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_store.FilePath}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show($"설정 파일을 열지 못했습니다.\n{_store.FilePath}\n\n{ex.Message}",
                "DeskKeeper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>settings.json 을 잘못 고쳤을 때 같은 오류는 한 번만 알린다.</summary>
    private void ReportLoadError()
    {
        var error = _store.LoadError;
        if (error is not null && error != _reportedLoadError)
        {
            _icon.ShowBalloonTip(8000, "settings.json 오류",
                $"이전 설정으로 계속 동작합니다. 파일을 고쳐 주세요.\n{error}", ToolTipIcon.Warning);
        }
        _reportedLoadError = error;
    }

    /// <summary>트레이 크기(보통 16px, 배율에 따라 20·24px)에 맞는 항목을 골라 흐려지지 않게 한다.</summary>
    private static Icon LoadTrayIcon()
    {
        using var stream = typeof(TrayContext).Assembly.GetManifestResourceStream("DeskKeeper.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream, SystemInformation.SmallIconSize);
    }

    private void ScheduleTrim()
    {
        _trimTimer.Stop();
        _trimTimer.Start();
    }

    private void OnToggleAutoStart(object? sender, EventArgs e)
    {
        AutoStart.Set(!AutoStart.IsEnabled());
    }

    protected override void ExitThreadCore()
    {
        _launcher?.Close();
        _hotkey.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _trimTimer.Dispose();
        base.ExitThreadCore();
    }
}
