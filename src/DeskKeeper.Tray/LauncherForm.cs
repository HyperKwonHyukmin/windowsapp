using DeskKeeper.Core;

namespace DeskKeeper.Tray;

/// <summary>
/// 단축키로 띄우는 런처. 분류별 색 타일을 넓게 펼쳐 보이고, 타일을 한 번 누르면 실행한다.
/// 다른 곳을 누르면 닫히고, 닫히면 Dispose 되어 메모리를 남기지 않는다.
/// </summary>
sealed class LauncherForm : Form
{
    public const string WindowTitle = "DeskKeeper 런처";

    /// <summary>창 최대 높이 = 모니터 작업 영역의 이 비율. 이보다 길면 타일 패널을 스크롤한다.</summary>
    private const double MaxScreenFraction = 0.8;
    /// <summary>창 너비 = 모니터 작업 영역 너비의 이 비율(최소·최대 픽셀 안에서).</summary>
    private const double WidthFraction = 0.55;

    // 분류에 순서대로 붙이는 색(밝은 배경 위 진한 글자와 함께 써도 구분되는 채도). 분류 없음은 회색
    private static readonly Color[] GroupHues =
    [
        Color.FromArgb(13, 148, 136),   // 청록
        Color.FromArgb(79, 70, 229),    // 남색
        Color.FromArgb(217, 119, 6),    // 호박
        Color.FromArgb(225, 29, 72),    // 장미
        Color.FromArgb(22, 163, 74),    // 초록
        Color.FromArgb(147, 51, 234),   // 보라
        Color.FromArgb(2, 132, 199),    // 하늘
        Color.FromArgb(202, 138, 4),    // 겨자
    ];
    private static readonly Color UngroupedHue = Color.FromArgb(100, 116, 139);
    // 최근 파일은 등록한 것과 구분되게 차분한 청회색
    private static readonly Color RecentHue = Color.FromArgb(71, 85, 105);
    private const int RecentCount = 12;
    private const string RecentTitle = "최근 파일";

    private static readonly Color Surface = Color.FromArgb(249, 249, 249);
    private static readonly Color Field = Color.White;
    private static readonly Color Border = Color.FromArgb(222, 222, 222);
    private static readonly Color Accent = Color.FromArgb(13, 148, 136);
    private static readonly Color Ink = Color.FromArgb(26, 26, 26);
    // 밝은 배경에서 대비 약 6:1(Color.Gray 는 3.9:1 로 작은 글자에 부족)
    private static readonly Color Muted = Color.FromArgb(96, 96, 96);

    /// <summary>런처를 띄운 모니터의 작업 영역(작업 표시줄 제외). 열 때 마우스가 있던 모니터.</summary>
    private readonly Rectangle _workArea = Screen.FromPoint(Cursor.Position).WorkingArea;

    private readonly SettingsStore _store;
    private readonly Panel _field;
    private readonly TextBox _query;
    private readonly TileView _tiles;
    private readonly NotesView _notes;
    private readonly Panel _footer;
    // Segoe UI: 맑은 고딕은 \ 를 ₩ 로 그린다. 한글은 글꼴 연결로 맑은 고딕이 자동으로 쓰인다
    private readonly Font _queryFont = new("Segoe UI", 13f);
    private readonly Font _smallFont = new("Segoe UI", 8.5f);
    private readonly Font? _iconFont = CreateIconFont();
    private readonly ShellIconCache _icons = new();
    private readonly System.Windows.Forms.Timer _dragWatch = new() { Interval = 100 };
    private readonly ContextMenuStrip _itemMenu;
    private readonly ContextMenuStrip _groupMenu;
    private bool _droppedWhileInactive;
    private TileView.Section? _menuSection;
    /// <summary>이름 입력 창을 띄운 동안은 비활성이 돼도 닫지 않는다.</summary>
    private bool _modalOpen;
    /// <summary>Tab 으로 바꾸는 메모 화면을 보고 있는지(아니면 런처 타일).</summary>
    private bool _notesMode;
    /// <summary>메모 화면 높이(100% 기준). 작업 영역 80% 안에서.</summary>
    private const int NotesHeight = 480;
    /// <summary>검색 칸 오른쪽 "런처 · 메모" 글자의 누르는 영역(그릴 때 정한다). [0]=런처, [1]=메모.</summary>
    private readonly Rectangle[] _modeRects = new Rectangle[2];
    /// <summary>마우스가 올라간 화면 표시(-1 없음, 0 런처, 1 메모).</summary>
    private int _modeHover = -1;
    /// <summary>검색 중(분류 구역 없이 맞는 순서)인지.</summary>
    private bool _searching;
    /// <summary>Windows 최근 항목(창을 열 때 한 번 읽는다). 등록하지 않은 임시 항목이라 메뉴가 다르다.</summary>
    private List<LauncherItem>? _recentAll;
    private readonly HashSet<LauncherItem> _recent = new(ReferenceEqualityComparer.Instance);
    /// <summary>아래 안내 줄 왼쪽 문구(상태·안내). 오른쪽에는 가리킨 타일의 경로를 그린다.</summary>
    private string _footerText = "";
    private string _countText = "";

    public LauncherForm(SettingsStore store)
    {
        _store = store;

        Text = WindowTitle;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        AllowDrop = true;
        BackColor = Surface;
        // 글꼴(pt)은 화면 배율을 따라 커지므로 픽셀로 정한 크기도 같이 키운다
        Padding = new Padding(S(18), S(16), S(18), 0);
        Width = Math.Clamp((int)(_workArea.Width * WidthFraction), S(720), S(1500));

        _query = new TextBox { Font = _queryFont, BorderStyle = BorderStyle.None, BackColor = Field, ForeColor = Ink };
        // 검색 칸: 둥근 흰 입력 칸 + 돋보기 + 개수 + 포커스 때 아래 강조선(Windows 11 입력 칸과 같은 문법)
        _field = new Panel { Dock = DockStyle.Top, Height = S(46), BackColor = Surface };
        _field.Controls.Add(_query);
        _field.Paint += OnPaintField;
        _field.Resize += (_, _) => LayoutQuery();
        _field.MouseClick += (_, e) =>
        {
            int hit = ModeAt(e.Location);
            if (hit >= 0 && (hit == 1) != _notesMode)
                SetNotesMode(hit == 1);
            else
                _query.Focus();
        };
        _field.MouseMove += (_, e) => SetModeHover(ModeAt(e.Location));
        _field.MouseLeave += (_, _) => SetModeHover(-1);
        _query.GotFocus += (_, _) => _field.Invalidate();
        _query.LostFocus += (_, _) => _field.Invalidate();
        _query.TextChanged += (_, _) =>
        {
            if (_notesMode)
                FilterNotes();
            else
                RefreshTiles();
        };

        _tiles = new TileView(_icons) { Dock = DockStyle.Top, TabStop = false };
        _tiles.TileClicked += Launch;
        _tiles.FocusChanged += () => _footer?.Invalidate();
        _tiles.MouseUp += OnTilesMouseUp;

        _notes = new NotesView { Dock = DockStyle.Top, Visible = false, TabStop = false };
        _notes.OpenInNotepadRequested += OpenNote;
        _notes.NewNoteRequested += NewNote;
        _notes.SelectionChanged += () => _footer?.Invalidate();
        _notes.EditingChanged += UpdateNotesStatus;
        _notes.LeaveRequested += () => _query.Focus();
        _notes.Message += ShowFooterMessage;

        _footer = new Panel { Dock = DockStyle.Top, Height = S(38), BackColor = Surface };
        _footer.Paint += OnPaintFooter;

        _itemMenu = BuildItemMenu();
        _groupMenu = BuildGroupMenu();

        // Dock=Top 은 나중에 넣은 것이 위로 간다: 검색 칸 → 간격 → 타일 → 안내 줄
        var gapBelowField = new Panel { Dock = DockStyle.Top, Height = S(10), BackColor = Surface };
        Controls.Add(_footer);
        Controls.Add(_notes);
        Controls.Add(_tiles);
        Controls.Add(gapBelowField);
        Controls.Add(_field);

        // 끌어다 놓기는 자식 컨트롤로 전달되지 않으므로 모두에 건다
        foreach (Control c in new Control[] { this, _field, _query, _tiles, _notes, _footer, gapBelowField })
        {
            c.AllowDrop = true;
            c.DragEnter += OnDragEnter;
            c.DragDrop += OnDragDrop;
        }

        _dragWatch.Tick += OnDragWatchTick;

        RefreshTiles();
        PlaceOnCursorScreen();
    }

    /// <summary>테두리 없는 창에 시스템 그림자를 준다.</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            const int CsDropShadow = 0x00020000;
            var cp = base.CreateParams;
            cp.ClassStyle |= CsDropShadow;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        WindowChrome.ApplyRoundedCorners(Handle, Border);
        // 입력 중에도 보이는 안내 문구(PlaceholderText 는 포커스를 받으면 사라진다)
        UpdateCueBanner();
        LayoutQuery();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _query.Focus();
    }

    /// <summary>Tab 은 대화상자 키라서 KeyDown 까지 오지 않는다. 여기서 화면 전환과 메모 단축키를 받는다.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            // 편집 칸 안의 키는 NotesView 가 먼저 받아 여기까지 오지 않는다
            case Keys.F2 when _notesMode:
                _notes.BeginEdit();
                return true;
            case Keys.Tab or (Keys.Tab | Keys.Shift):
                SetNotesMode(!_notesMode);
                return true;
            case Keys.Control | Keys.N when _notesMode:
                NewNote();
                return true;
            // 검색 칸에서 글자를 고르지 않았으면 메모 내용을 복사한다
            case Keys.Control | Keys.C when _notesMode && _query.Focused && _query.SelectionLength == 0:
                CopyNote();
                return true;
            case Keys.PageUp or Keys.PageDown when _notesMode:
                _notes.ScrollPreview(keyData == Keys.PageDown);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // 편집 칸의 Enter·방향키는 그대로 글쓰기에 쓴다(KeyPreview 라 여기를 먼저 지난다)
        if (_notesMode && _notes.EditorFocused)
            return;
        if (_notesMode)
        {
            switch (e.KeyCode)
            {
                case Keys.Escape:
                    Close();
                    break;
                case Keys.Enter:
                    _notes.BeginEdit();
                    break;
                case Keys.Up or Keys.Down:
                    _notes.MoveSelection(e.KeyCode == Keys.Down ? 1 : -1);
                    break;
                default:
                    // ←→ 는 검색 칸의 커서 이동으로 둔다
                    return;
            }
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }
        switch (e.KeyCode)
        {
            case Keys.Escape:
                Close();
                break;
            case Keys.Enter:
                if (_tiles.SelectedItem is { } item)
                    Launch(item);
                break;
            case Keys.Left or Keys.Right or Keys.Up or Keys.Down:
                // 검색 칸의 커서 이동 대신 타일 사이를 이동한다
                _tiles.MoveSelection(e.KeyCode);
                break;
            default:
                return;
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    // ── 목록 만들기·크기 ────────────────────────────────────

    private void RefreshTiles()
    {
        _store.ReloadIfChanged();
        var all = _store.Current.Launcher;
        var q = _query.Text.Trim();
        _searching = q.Length > 0;

        var built = LauncherSection.Build(_store.Current);
        bool useGroups = LauncherSection.HasGroups(built);
        // 최근 파일: 창을 열 때 한 번 읽고, 그사이 등록한 것은 뺀다
        _recentAll ??= RecentFiles.Read(RecentCount * 2, all.Select(i => i.Path));
        var registered = new HashSet<string>(all.Select(i => i.Path.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase);
        var recent = _recentAll.Where(r => !registered.Contains(r.Path)).Take(RecentCount).ToList();
        _recent.Clear();
        _recent.UnionWith(recent);
        // 타일 색은 늘 그 항목의 분류 색(분류를 안 쓰면 모두 기본 강조색). 검색 중에도 어느 분류인지 보인다
        _tiles.TileHue = item => _recent.Contains(item) ? RecentHue : useGroups ? HueOf(item.Group) : Accent;

        var sections = new List<TileView.Section>();
        if (_searching)
        {
            // 검색 중: 분류 구역 없이 맞는 순서(이름이 검색어로 시작하는 항목 먼저, 그다음 등록 순서).
            // 최근 파일도 함께 찾되 등록한 항목 뒤에 둔다
            static int Rank(LauncherItem i, string q) => i.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            var hits = all.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).OrderBy(i => Rank(i, q))
                .Concat(recent.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).OrderBy(i => Rank(i, q)))
                .ToList();
            if (hits.Count > 0)
                sections.Add(new TileView.Section(null, "", Accent, hits, false));
        }
        else
        {
            foreach (var s in built)
                sections.Add(new TileView.Section(s.Group, s.Title, useGroups ? HueOf(s.Group) : Accent, s.Items, useGroups));
            if (recent.Count > 0)
                sections.Add(new TileView.Section(null, RecentTitle, RecentHue, recent, true, Compact: true));
        }
        _tiles.SetSections(sections);

        int matches = sections.Sum(s => s.Items.Count(i => !_recent.Contains(i)));
        _countText = _searching ? $"{matches}/{all.Count}" : all.Count > 0 ? $"{all.Count}개" : "";
        _footerText = _store.LoadError is not null
            ? "settings.json 에 오류가 있습니다. 트레이 메뉴의 '설정 파일 열기'로 고쳐 주세요."
            : all.Count == 0
                ? "등록된 항목이 없습니다. 파일이나 폴더를 이 창에 끌어다 놓으세요."
                : matches == 0
                    ? $"'{q}'에 맞는 항목이 없습니다. 이름 일부만 입력해 보세요."
                    : "방향키 이동 · Enter 실행 · Esc 닫기 · 끌어다 놓으면 등록 · 우클릭으로 분류";
        _field.Invalidate();
        _footer.Invalidate();
        FitHeight();
    }

    private Color HueOf(string? group)
    {
        if (group is null)
            return UngroupedHue;
        int index = _store.Current.LauncherGroups.FindIndex(g => string.Equals(g, group, StringComparison.CurrentCultureIgnoreCase));
        return index < 0 ? UngroupedHue : GroupHues[index % GroupHues.Length];
    }

    /// <summary>
    /// 내용 높이에 맞춰 창 높이를 정한다. 최대는 모니터 작업 영역의 80%이고, 넘치면 타일 패널만 스크롤한다.
    /// 창이 화면 아래로 넘치면 위로 올린다. 타일이 없으면 패널을 숨긴다.
    /// </summary>
    private void FitHeight()
    {
        int chrome = Padding.Top + _field.Height + S(10) + _footer.Height;
        int maxTiles = Math.Max(S(110), (int)(_workArea.Height * MaxScreenFraction) - chrome);
        if (_notesMode)
        {
            _notes.Height = Math.Min(S(NotesHeight), maxTiles);
            ClientSize = new Size(ClientSize.Width, chrome + _notes.Height);
            KeepOnScreen();
            return;
        }
        int needed = _tiles.MeasureHeight(ClientSize.Width - Padding.Horizontal);
        int height = Math.Min(needed, maxTiles);
        _tiles.Visible = height > 0;
        _tiles.Height = height;
        ClientSize = new Size(ClientSize.Width, chrome + height);
        KeepOnScreen();
    }

    /// <summary>창 아래가 작업 영역을 넘으면 넘친 만큼 위로 올린다(위쪽 여백은 남긴다).</summary>
    private void KeepOnScreen()
    {
        int margin = S(24);
        int overflow = Bottom - (_workArea.Bottom - margin);
        if (overflow > 0)
            Top = Math.Max(_workArea.Top + margin, Top - overflow);
    }

    /// <summary>마우스가 있는 모니터의 가운데, 위에서 1/5 지점에 띄운다. 길면 화면 안으로 올린다.</summary>
    private void PlaceOnCursorScreen()
    {
        Location = new Point(_workArea.Left + (_workArea.Width - Width) / 2, _workArea.Top + _workArea.Height / 5);
        KeepOnScreen();
    }

    // ── 검색 칸·안내 줄 그리기 ──────────────────────────────

    private void LayoutQuery()
    {
        int left = S(46);
        int right = S(210);
        int h = _query.PreferredHeight;
        _query.SetBounds(left, (_field.Height - h) / 2, Math.Max(10, _field.Width - left - right), h);
    }

    private void OnPaintField(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, _field.Width - 1, _field.Height - 1);
        using (var path = RoundedRect(rect, S(8)))
        using (var fill = new SolidBrush(Field))
        using (var pen = new Pen(Border))
        {
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
        if (_query.Focused)
        {
            using var accent = new SolidBrush(Accent);
            g.FillRectangle(accent, S(8), _field.Height - S(2), _field.Width - S(16), S(2));
        }
        if (_iconFont is not null)
        {
            TextRenderer.DrawText(g, "", _iconFont, new Rectangle(S(14), 0, S(24), _field.Height), Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }
        // 오른쪽 끝: 화면 표시(런처 · 메모, 지금 것만 진하게). 그 왼쪽에 개수
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        int x = _field.Width - S(16);
        foreach (var (label, index) in new[] { ("메모", 1), ("런처", 0) })
        {
            bool active = (index == 1) == _notesMode;
            using var font = active ? new Font(_smallFont, FontStyle.Bold) : null;
            var f = font ?? _smallFont;
            int w = TextRenderer.MeasureText(g, label, f, Size.Empty, flags).Width;
            x -= w;
            var labelRect = new Rectangle(x, 0, w, _field.Height);
            // 누르는 영역은 글자보다 조금 넓게(작은 글자를 정확히 맞히지 않아도 되게)
            _modeRects[index] = Rectangle.Inflate(labelRect, S(6), 0);
            TextRenderer.DrawText(g, label, f, labelRect, active ? Accent : _modeHover == index ? Ink : Muted, flags);
            if (active)
            {
                using var bar = new SolidBrush(Accent);
                g.FillRectangle(bar, x, _field.Height / 2 + S(10), w, S(2));
            }
            x -= S(12);
        }
        x -= S(4);
        TextRenderer.DrawText(g, "Tab", _smallFont, new Rectangle(0, 0, x, _field.Height), Muted, flags | TextFormatFlags.Right);
        x -= TextRenderer.MeasureText(g, "Tab", _smallFont, Size.Empty, flags).Width + S(16);
        TextRenderer.DrawText(g, _countText, _smallFont, new Rectangle(0, 0, Math.Max(0, x), _field.Height), Muted,
            flags | TextFormatFlags.Right);
    }

    private void OnPaintFooter(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var pen = new Pen(Border))
            g.DrawLine(pen, 0, 0, _footer.Width, 0);
        const TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var left = TextRenderer.MeasureText(g, _footerText, _smallFont, Size.Empty, flags);
        TextRenderer.DrawText(g, _footerText, _smallFont, new Rectangle(S(2), 0, left.Width, _footer.Height), Muted, flags);
        // 오른쪽: 가리킨(또는 선택한) 타일의 경로. 타일에는 경로를 쓰지 않으므로 여기서 확인한다
        var hotPath = _notesMode ? _notes.SelectedNote?.Path : _tiles.HotItem?.Path;
        if (hotPath is not null)
        {
            int x = S(2) + left.Width + S(24);
            TextRenderer.DrawText(g, hotPath, _smallFont, new Rectangle(x, 0, Math.Max(0, _footer.Width - x), _footer.Height), Muted,
                flags | TextFormatFlags.Right | TextFormatFlags.PathEllipsis);
        }
    }

    /// <summary>Windows 11 의 아이콘 글꼴(없으면 Windows 10 의 것). 둘 다 없으면 돋보기를 그리지 않는다.</summary>
    private static Font? CreateIconFont()
    {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            var font = new Font(name, 11f);
            if (font.Name == name)
                return font;
            font.Dispose();
        }
        return null;
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ── 닫기(비활성)·끌어다 놓기 ────────────────────────────

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (IsDisposed || _modalOpen)
            return;
        // 탐색기에서 파일을 끌어오려고 누른 경우일 수 있으니 버튼을 뗄 때까지 기다렸다가 판단한다
        if (IsMouseButtonDown())
        {
            _droppedWhileInactive = false;
            _dragWatch.Start();
            return;
        }
        BeginInvoke(Close);
    }

    private void OnDragWatchTick(object? sender, EventArgs e)
    {
        if (IsMouseButtonDown())
            return;
        _dragWatch.Stop();
        if (_droppedWhileInactive)
        {
            Activate();
            _query.Focus();
        }
        else if (!ContainsFocus)
        {
            Close();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>
    /// 실제 마우스 버튼 상태. Control.MouseButtons 는 이 스레드가 받은 입력만 반영해서
    /// 탐색기에서 누른 버튼은 보이지 않는다.
    /// </summary>
    private static bool IsMouseButtonDown()
    {
        const int VkLButton = 0x01, VkRButton = 0x02, VkMButton = 0x04;
        // 최상위 비트(음수)가 눌림
        return GetAsyncKeyState(VkLButton) < 0 || GetAsyncKeyState(VkRButton) < 0 || GetAsyncKeyState(VkMButton) < 0;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        // 메모 화면에서는 메모 폴더로 옮긴다
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) != true ? DragDropEffects.None
            : _notesMode ? DragDropEffects.Move : DragDropEffects.Link;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths)
            return;
        _droppedWhileInactive = true;
        if (_notesMode)
        {
            DropNotes(paths);
            return;
        }
        try
        {
            // 분류별 보기에서 어느 구역 위에 놓았는지로 분류를 정한다(빈 곳·검색 중이면 분류 없음)
            var group = _searching ? null : _tiles.GroupAt(_tiles.PointToClient(new Point(e.X, e.Y)));
            int added = _store.AddLauncherItems(paths, group);
            _query.Clear();
            RefreshTiles();
            _footerText = added == 0 ? "이미 등록된 항목입니다."
                : group is null ? $"{added}개를 등록했습니다."
                : $"{added}개를 '{group}'에 등록했습니다.";
            _footer.Invalidate();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowFooterMessage(ex.Message);
        }
    }

    // ── 메모 화면 ───────────────────────────────────────────

    /// <summary>런처 ↔ 메모. 검색어는 그대로 두고 바뀐 화면에서 다시 찾는다. 메모는 바꿀 때마다 폴더를 다시 읽는다.</summary>
    private void SetNotesMode(bool on)
    {
        _notesMode = on;
        _tiles.Visible = !on && _tiles.Height > 0;
        _notes.Visible = on;
        UpdateCueBanner();
        if (on)
            ReloadNotes();
        else
            RefreshTiles();
        // 창 높이가 바뀌면서 새로 드러난 패널이 그려지지 않는 경우가 있어 전체를 다시 그린다
        Invalidate(true);
        _query.Focus();
        _query.SelectAll();
    }

    /// <summary>검색 칸 위 점이 "런처"(0)·"메모"(1) 글자 위인지. 아니면 -1.</summary>
    private int ModeAt(Point p) => _modeRects[0].Contains(p) ? 0 : _modeRects[1].Contains(p) ? 1 : -1;

    private void SetModeHover(int hover)
    {
        if (hover == _modeHover)
            return;
        _modeHover = hover;
        _field.Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
        _field.Invalidate();
    }

    private void UpdateCueBanner() =>
        WindowChrome.SetCueBanner(_query, _notesMode ? "메모 제목·내용으로 찾기" : "프로그램·폴더 이름으로 찾기");

    /// <summary>메모 폴더 경로. settings.json 의 notesFolder 가 잘못된 경로면 null 과 안내.</summary>
    private string? NotesFolder()
    {
        _store.ReloadIfChanged();
        try
        {
            return Notes.FolderOf(_store.Current);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowFooterMessage($"settings.json 의 notesFolder 경로가 잘못됐습니다: {ex.Message}");
            return null;
        }
    }

    private void ReloadNotes(string? keepPath = null)
    {
        if (NotesFolder() is not { } folder)
            return;
        try
        {
            _notes.Reload(folder, keepPath);
            _notes.Filter(_query.Text, keepPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowFooterMessage($"메모 폴더를 읽지 못했습니다: {ex.Message}");
            return;
        }
        UpdateNotesStatus();
        FitHeight();
    }

    private void FilterNotes()
    {
        _notes.Filter(_query.Text);
        UpdateNotesStatus();
    }

    private void UpdateNotesStatus()
    {
        bool searching = _query.Text.Trim().Length > 0;
        _countText = searching ? $"{_notes.VisibleCount}/{_notes.TotalCount}" : _notes.TotalCount > 0 ? $"{_notes.TotalCount}개" : "";
        _footerText = _notes.IsEditing
            ? "편집 중 · 자동 저장 · Esc 보기로 · Ctrl+B 굵게 · Ctrl+1/2 제목 · Ctrl+L 목록 · Ctrl+T 할 일 · Tab 들여쓰기"
            : _notes.TotalCount == 0
            ? "메모가 없습니다. Ctrl+N 으로 만들거나 바탕화면의 .txt·.md 를 끌어다 놓으면 이 폴더로 옮깁니다."
            : searching && _notes.VisibleCount == 0
                ? $"'{_query.Text.Trim()}'이(가) 든 메모가 없습니다."
                : "↑↓ 선택 · Enter·F2 편집 · Ctrl+N 새 메모 · Ctrl+C 내용 복사 · PgUp/PgDn 넘기기";
        _field.Invalidate();
        _footer.Invalidate();
    }

    private void OpenNote(NoteFile note)
    {
        Close();
        OpenInNotepad(note.Path);
    }

    /// <summary>메모장으로 연다(.md 연결 프로그램이 무거운 편집기일 수 있어 고정).</summary>
    private static void OpenInNotepad(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show($"메모장을 열지 못했습니다.\n{path}\n\n{ex.Message}", "DeskKeeper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>제목을 물어 빈 메모(.md)를 만들고 그 자리에서 편집을 시작한다. 검색어가 있으면 제목 기본값으로 쓴다.</summary>
    private void NewNote()
    {
        if (NotesFolder() is not { } folder)
            return;
        var title = AskName("새 메모", "제목 (파일 이름이 됩니다)", _query.Text.Trim());
        if (title is null)
            return;
        try
        {
            var path = Notes.Create(folder, title);
            // 새 메모가 검색에 걸러지지 않게 검색어를 비우고, 만든 메모를 골라 바로 편집한다
            _query.Clear();
            ReloadNotes(path);
            _notes.BeginEdit();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowFooterMessage($"메모를 만들지 못했습니다: {ex.Message}");
        }
    }

    private void CopyNote()
    {
        if (_notes.SelectedText is not { Length: > 0 } text)
            return;
        try
        {
            Clipboard.SetText(text.ReplaceLineEndings("\r\n"));
            ShowFooterMessage($"'{_notes.SelectedNote?.Title}' 내용을 복사했습니다.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            ShowFooterMessage("클립보드를 다른 프로그램이 쓰고 있습니다. 다시 눌러 보세요.");
        }
    }

    /// <summary>끌어다 놓은 .txt/.md 를 메모 폴더로 옮긴다. 나머지 파일은 건너뛴다.</summary>
    private void DropNotes(string[] paths)
    {
        if (NotesFolder() is not { } folder)
            return;
        try
        {
            int moved = Notes.MoveIn(folder, paths);
            int skipped = paths.Length - moved;
            ReloadNotes();
            ShowFooterMessage(moved == 0
                ? "옮긴 메모가 없습니다. .txt·.md 파일만 받으며, 이미 메모 폴더에 있는 것은 그대로 둡니다."
                : skipped > 0 ? $"{moved}개를 메모 폴더로 옮겼습니다({skipped}개는 건너뜀)." : $"{moved}개를 메모 폴더로 옮겼습니다.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReloadNotes();
            ShowFooterMessage($"옮기지 못했습니다: {ex.Message}");
        }
    }

    // ── 실행·메뉴 ───────────────────────────────────────────

    private void Launch(LauncherItem item)
    {
        // 오류 대화상자가 런처 뒤에 깔리지 않게 먼저 닫는다
        Close();
        ItemLauncher.Start(item);
    }

    private void OnTilesMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
            return;
        // 타일이면 항목 메뉴, 분류 제목이면 분류 메뉴
        if (_tiles.ItemAt(e.Location) is not null)
        {
            _itemMenu.Show(_tiles, e.Location);
        }
        else if (_tiles.HeaderAt(e.Location) is { Group: not null } section)
        {
            _menuSection = section;
            _groupMenu.Show(_tiles, e.Location);
        }
    }

    private ContextMenuStrip BuildItemMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("실행", null, (_, _) =>
        {
            if (_tiles.SelectedItem is { } item)
                Launch(item);
        });
        menu.Items.Add("파일 위치 열기", null, (_, _) =>
        {
            if (_tiles.SelectedItem is { } item)
            {
                Close();
                ItemLauncher.RevealInExplorer(item.Path);
            }
        });
        var analyze = menu.Items.Add("이 폴더 분석", null, (_, _) =>
        {
            if (_tiles.SelectedItem is { } item)
            {
                Close();
                ItemLauncher.StartAnalyzer(item.Path);
            }
        });
        var groupSeparator = new ToolStripSeparator();
        menu.Items.Add(groupSeparator);
        var groupMenu = new ToolStripMenuItem("분류");
        menu.Items.Add(groupMenu);
        var pin = new ToolStripMenuItem("런처에 등록", null, (_, _) =>
        {
            // 최근 파일 → 등록 항목(분류 없음). 그다음부터는 최근 구역에서 빠지고 위 구역에 보인다
            if (_tiles.SelectedItem is { } item)
                RunStoreAction(() => _store.AddLauncherItems([item.Path]));
        });
        menu.Items.Add(pin);
        var removeSeparator = new ToolStripSeparator();
        menu.Items.Add(removeSeparator);
        var remove = menu.Items.Add("목록에서 제거 (파일은 그대로)", null, (_, _) =>
        {
            if (_tiles.SelectedItem is { } item)
                RunStoreAction(() => _store.RemoveLauncherItem(item.Path));
        });
        menu.Opening += (_, e) =>
        {
            if (_tiles.SelectedItem is not { } item)
            {
                e.Cancel = true;
                return;
            }
            bool isRecent = _recent.Contains(item);
            // 폴더 항목에서만 분석 메뉴를 보인다
            analyze.Visible = Directory.Exists(item.Path);
            // 최근 파일은 아직 등록 전이라 분류·제거 대신 "런처에 등록"
            groupMenu.Visible = !isRecent;
            removeSeparator.Visible = !isRecent;
            remove.Visible = !isRecent;
            pin.Visible = isRecent;
            if (!isRecent)
                FillGroupChoices(groupMenu, item);
        };
        return menu;
    }

    /// <summary>"분류" 하위 메뉴: 기존 분류(현재 것에 체크) · 분류 없음 · 새 분류….</summary>
    private void FillGroupChoices(ToolStripMenuItem parent, LauncherItem item)
    {
        foreach (var old in parent.DropDownItems.Cast<ToolStripItem>().ToList())
            old.Dispose();
        parent.DropDownItems.Clear();

        foreach (var group in _store.Current.LauncherGroups)
        {
            var name = group;
            parent.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) =>
                RunStoreAction(() => _store.SetLauncherGroup(item.Path, name)))
            {
                Checked = string.Equals(item.Group, name, StringComparison.CurrentCultureIgnoreCase),
            });
        }
        if (_store.Current.LauncherGroups.Count > 0)
            parent.DropDownItems.Add(new ToolStripSeparator());
        parent.DropDownItems.Add(new ToolStripMenuItem(LauncherSection.UngroupedName, null, (_, _) =>
            RunStoreAction(() => _store.SetLauncherGroup(item.Path, null)))
        {
            Checked = item.Group is null,
        });
        parent.DropDownItems.Add("새 분류…", null, (_, _) =>
        {
            var name = AskName("새 분류", "분류 이름 (예: 회사, 개발)", "");
            if (name is not null)
                RunStoreAction(() => _store.SetLauncherGroup(item.Path, name));
        });
    }

    /// <summary>분류 제목 우클릭 메뉴: 위로 · 아래로 · 이름 바꾸기.</summary>
    private ContextMenuStrip BuildGroupMenu()
    {
        var menu = new ContextMenuStrip();
        var up = menu.Items.Add("위로", null, (_, _) =>
        {
            if (_menuSection?.Group is { } g)
                RunStoreAction(() => _store.MoveLauncherGroup(g, -1));
        });
        var down = menu.Items.Add("아래로", null, (_, _) =>
        {
            if (_menuSection?.Group is { } g)
                RunStoreAction(() => _store.MoveLauncherGroup(g, +1));
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("이름 바꾸기…", null, (_, _) =>
        {
            if (_menuSection?.Group is not { } g)
                return;
            var name = AskName("분류 이름 바꾸기", "새 이름 (이미 있는 이름이면 그 분류로 합칩니다)", g);
            if (name is not null)
                RunStoreAction(() => _store.RenameLauncherGroup(g, name));
        });
        menu.Opening += (_, e) =>
        {
            if (_menuSection?.Group is not { } g)
            {
                e.Cancel = true;
                return;
            }
            var groups = _store.Current.LauncherGroups;
            int index = groups.FindIndex(x => string.Equals(x, g, StringComparison.CurrentCultureIgnoreCase));
            up.Enabled = index > 0;
            down.Enabled = index >= 0 && index < groups.Count - 1;
        };
        return menu;
    }

    /// <summary>설정 변경 후 타일을 다시 그린다. 설정 파일 오류는 안내 줄에 보인다.</summary>
    private void RunStoreAction(Action action)
    {
        try
        {
            action();
            RefreshTiles();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowFooterMessage(ex.Message);
        }
    }

    private void ShowFooterMessage(string message)
    {
        _footerText = message;
        _footer.Invalidate();
    }

    /// <summary>이름 한 줄을 묻는다. 취소하거나 비우면 null. 그동안 런처가 닫히지 않게 막는다.</summary>
    private string? AskName(string title, string prompt, string initial)
    {
        _modalOpen = true;
        try
        {
            using var dialog = new NamePrompt(title, prompt, initial);
            var result = dialog.ShowDialog(this);
            return result == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.Value) ? dialog.Value.Trim() : null;
        }
        finally
        {
            _modalOpen = false;
            Activate();
            _query.Focus();
        }
    }

    /// <summary>100% 기준 픽셀 값을 현재 화면 배율에 맞춘다.</summary>
    private int S(int logicalPixels) => LogicalToDeviceUnits(logicalPixels);

    /// <summary>다른 곳을 눌러 닫혀도 편집하던 메모는 저장한다.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _notes.SaveOnClose();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dragWatch.Dispose();
            _itemMenu.Dispose();
            _groupMenu.Dispose();
            _queryFont.Dispose();
            _smallFont.Dispose();
            _iconFont?.Dispose();
            _icons.Dispose();
        }
        base.Dispose(disposing);
    }
}
