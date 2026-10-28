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
    private readonly System.Windows.Forms.Timer _checkTimer;
    private readonly SynchronizationContext _ui;
    private readonly Icon _baseIcon;
    private Icon? _badgeIcon;
    private IntPtr _badgeHandle;
    private LauncherForm? _launcher;
    private string? _reportedLoadError;
    private int _expiredCount;
    private bool _checking;
    private DateTime _lastBalloonDate;
    /// <summary>풍선 알림을 눌렀을 때 할 일(정리 후보 보기·SessionBoard 열기). 오류 알림이면 null.</summary>
    private Action? _balloonAction;
    private readonly System.Windows.Forms.Timer _memoryTimer;
    private int _memoryPercent = -1;
    /// <summary>90% 를 넘어 알린 상태. 85% 아래로 내려와야 다시 알린다(오르내릴 때 반복 알림 방지).</summary>
    private bool _memoryHigh;
    private Font? _boldMenuFont;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public TrayContext()
    {
        _menu = new ContextMenuStrip();
        // 컨트롤을 하나 만든 뒤라 WinForms 동기화 컨텍스트가 설치돼 있다
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        // 메뉴는 열 때마다 새로 만든다(설정 파일을 직접 고친 내용을 바로 반영)
        _menu.Opening += OnMenuOpening;
        // 메뉴를 닫은 뒤 그리면서 늘어난 메모리를 다시 줄인다
        _menu.Closed += (_, _) => ScheduleTrim();

        _baseIcon = LoadTrayIcon();
        _icon = new NotifyIcon
        {
            Icon = _baseIcon,
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

        _icon.BalloonTipClicked += (_, _) =>
        {
            _balloonAction?.Invoke();
        };
        _icon.BalloonTipClosed += (_, _) => _balloonAction = null;

        // 정리 후보 점검: 시작 1분 뒤, 그 뒤로 하루마다, 그리고 분석 창이 닫힐 때
        _checkTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _checkTimer.Tick += (_, _) =>
        {
            _checkTimer.Interval = 24 * 60 * 60 * 1000;
            StartCheck();
        };
        _checkTimer.Start();
        ItemLauncher.AnalyzerExited += () => _ui.Post(_ => StartCheck(), null);

        // 시스템 메모리: 1분마다 사용률만 읽는다(원인 찾기·끄기는 SessionBoard 메모리 탭)
        _memoryTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _memoryTimer.Tick += (_, _) => CheckMemory();
        _memoryTimer.Start();
        CheckMemory();

        _store.ReloadIfChanged();
        ReportLoadError();
        // 시작 직후 초기화로 커진 작업 메모리를 정리
        ScheduleTrim();
    }

    private async void StartCheck()
    {
        if (_checking)
            return;
        _checking = true;
        try
        {
            _store.ReloadIfChanged();
            ReportLoadError();
            _store.RemoveMissingMarks();
            var settings = _store.Current;
            int count = await Task.Run(() => Expiry.CountExpired(settings, DateTime.Now, CancellationToken.None));
            SetExpiredCount(count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 점검 실패는 다음 점검에서 다시 시도한다
        }
        finally
        {
            _checking = false;
            ScheduleTrim();
        }
    }

    private void CheckMemory()
    {
        if (SystemMemory.Read() is not { } m)
            return;
        _memoryPercent = m.Percent;
        if (!_memoryHigh && m.Percent >= 90)
        {
            _memoryHigh = true;
            _balloonAction = OpenSessionBoard;
            _icon.ShowBalloonTip(8000, $"메모리 {m.Percent}% 사용 중",
                $"{m.UsedGb:0.0} / {m.TotalGb:0.0} GB. 눌러서 SessionBoard 메모리 탭(4)에서 무엇이 쓰는지 확인하세요.", ToolTipIcon.Warning);
        }
        else if (_memoryHigh && m.Percent < 85)
        {
            _memoryHigh = false;
        }
        UpdateTooltip();
    }

    /// <summary>툴팁: 메모리 사용률과 정리할 파일 수(Windows 툴팁 한도 127자 안).</summary>
    private void UpdateTooltip()
    {
        var parts = new List<string> { "DeskKeeper" };
        if (_memoryPercent >= 0)
            parts.Add($"메모리 {_memoryPercent}%");
        if (_expiredCount > 0)
            parts.Add($"정리할 파일 {_expiredCount:N0}개");
        _icon.Text = string.Join(" · ", parts);
    }

    /// <summary>SessionBoard 화면을 연다. 메모리 탭으로 바로 가는 주소가 없어 4 키를 안내한다.</summary>
    private void OpenSessionBoard()
    {
        if (!SystemMemory.IsSessionBoardRunning())
        {
            MessageBox.Show("SessionBoard 가 꺼져 있습니다.\n바탕화면의 '세션 보드'로 켠 뒤 다시 눌러 주세요.",
                "DeskKeeper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(SystemMemory.SessionBoardUrl) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show($"SessionBoard 를 열지 못했습니다.\n{ex.Message}", "DeskKeeper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SetExpiredCount(int count)
    {
        _expiredCount = count;
        _icon.Icon = count > 0 ? GetBadgeIcon() : _baseIcon;
        UpdateTooltip();
        if (count > 0 && _lastBalloonDate != DateTime.Today)
        {
            _lastBalloonDate = DateTime.Today;
            _balloonAction = () => ItemLauncher.StartAnalyzer(null, expired: true);
            _icon.ShowBalloonTip(8000, "DeskKeeper", $"정리할 파일이 {count:N0}개 있습니다. 눌러서 확인하세요.", ToolTipIcon.Info);
        }
    }

    /// <summary>기본 아이콘 오른쪽 아래에 빨간 점을 찍은 아이콘(처음 필요할 때 한 번 만든다).</summary>
    private Icon GetBadgeIcon()
    {
        if (_badgeIcon is not null)
            return _badgeIcon;
        using var bmp = _baseIcon.ToBitmap();
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int d = Math.Max(7, bmp.Width / 2);
            var dot = new Rectangle(bmp.Width - d, bmp.Height - d, d - 1, d - 1);
            using var red = new SolidBrush(Color.FromArgb(220, 38, 38));
            using var ring = new Pen(Color.White);
            g.FillEllipse(red, dot);
            g.DrawEllipse(ring, dot);
        }
        _badgeHandle = bmp.GetHicon();
        _badgeIcon = Icon.FromHandle(_badgeHandle);
        return _badgeIcon;
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

        if (_memoryHigh)
        {
            var memory = new ToolStripMenuItem($"메모리 {_memoryPercent}% — SessionBoard 에서 보기", null, (_, _) => OpenSessionBoard());
            memory.Font = _boldMenuFont ??= new Font(_menu.Font, FontStyle.Bold);
            _menu.Items.Add(memory);
        }
        if (_expiredCount > 0)
        {
            var candidates = new ToolStripMenuItem($"정리할 파일 {_expiredCount:N0}개 보기", null,
                (_, _) => ItemLauncher.StartAnalyzer(null, expired: true));
            // 메뉴를 열 때마다 글꼴(GDI 개체)을 새로 만들지 않게 한 번만 만든다
            candidates.Font = _boldMenuFont ??= new Font(_menu.Font, FontStyle.Bold);
            _menu.Items.Add(candidates);
        }
        if (_memoryHigh || _expiredCount > 0)
            _menu.Items.Add(new ToolStripSeparator());

        var sections = LauncherSection.Build(_store.Current);
        if (sections.Count == 0)
            _menu.Items.Add(new ToolStripMenuItem("(등록된 항목 없음 — 런처에 끌어다 놓아 등록)") { Enabled = false });
        // 하위 메뉴로 숨기지 않고 흐린 분류 제목 아래로 이어서 보인다(한 번에 보기)
        bool showHeaders = LauncherSection.HasGroups(sections);
        foreach (var section in sections)
        {
            if (showHeaders)
                _menu.Items.Add(new ToolStripMenuItem(section.Title) { Enabled = false });
            foreach (var item in section.Items)
            {
                _menu.Items.Add(item.Name, null, (_, _) => ItemLauncher.Start(item)).ToolTipText = item.Path;
            }
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("런처 열기", null, (_, _) => ToggleLauncher()) { ShortcutKeyDisplayString = HotkeyText });
        _menu.Items.Add("폴더 분석…", null, (_, _) => ItemLauncher.StartAnalyzer(null));
        var memoryLabel = _memoryPercent >= 0 ? $"메모리 보기 ({_memoryPercent}%) — SessionBoard 4번 탭" : "메모리 보기 — SessionBoard 4번 탭";
        _menu.Items.Add(memoryLabel, null, (_, _) => OpenSessionBoard());
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
            _balloonAction = null;
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
        _checkTimer.Dispose();
        _memoryTimer.Dispose();
        _boldMenuFont?.Dispose();
        if (_badgeHandle != IntPtr.Zero)
            DestroyIcon(_badgeHandle);
        base.ExitThreadCore();
    }
}
