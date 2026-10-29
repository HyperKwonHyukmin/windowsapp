using DeskKeeper.Core;

namespace DeskKeeper.Tray;

/// <summary>
/// 런처의 메모 화면: 왼쪽 "+ 새 메모"와 제목 목록(최근 수정순), 오른쪽 서식 도구줄과 본문.
/// 본문은 보기(마크다운을 서식 입혀 그림, RichTextBox)와 편집(마크다운 원문, TextBox)을 오간다.
/// 편집은 멈추고 1.5초 뒤·다른 메모로 옮길 때·보기로 돌아갈 때·창을 닫을 때 저장한다.
/// 검색 칸·목록 키는 런처가 받아서 이쪽 메서드를 부르고, 편집 칸에 포커스가 있으면 런처는 키를 건드리지 않는다.
/// </summary>
sealed class NotesView : Panel
{
    private static readonly Color Surface = Color.FromArgb(249, 249, 249);
    private static readonly Color Border = Color.FromArgb(222, 222, 222);
    private static readonly Color Selected = Color.FromArgb(214, 239, 236);
    private static readonly Color Accent = Color.FromArgb(13, 148, 136);
    private static readonly Color Ink = Color.FromArgb(26, 26, 26);
    private static readonly Color Muted = Color.FromArgb(96, 96, 96);

    private readonly ListBox _list;
    private readonly Button _newButton;
    private readonly ToolStrip _bar;
    private readonly ToolStripButton _modeButton;
    private readonly ToolStripLabel _status;
    private readonly List<ToolStripItem> _formatItems = [];
    private readonly Panel _frame;
    private readonly RichTextBox _view;
    private readonly TextBox _editor;
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 1500 };
    private readonly Font _titleFont = new("Segoe UI", 10f, FontStyle.Bold);
    private readonly Font _smallFont = new("Segoe UI", 8.5f);
    private readonly Font _bodyFont = new("Segoe UI", 10f);
    private readonly Font _barFont = new("Segoe UI", 9f);
    private readonly Font _boldBarFont = new("Segoe UI", 9f, FontStyle.Bold);
    private readonly Font _italicBarFont = new("Segoe UI", 9f, FontStyle.Italic);

    private List<NoteFile> _all = [];
    /// <summary>읽은 내용(창이 닫히면 같이 버린다). 검색할 때 한 번씩 읽는다.</summary>
    private readonly Dictionary<string, string> _content = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>검색어가 내용에서 맞은 줄(목록 둘째 줄에 보인다).</summary>
    private readonly Dictionary<string, string> _hitLine = new(StringComparer.OrdinalIgnoreCase);
    private string _query = "";

    /// <summary>편집 중인 메모와 읽었을 때의 수정 시각(그사이 다른 프로그램이 고쳤는지 본다).</summary>
    private NoteFile? _editing;
    private DateTime _editingStamp;
    private bool _dirty;
    /// <summary>편집 칸 Text 를 코드로 바꾸는 중(변경 표시를 하지 않는다).</summary>
    private bool _loadingEditor;

    public event Action<NoteFile>? OpenInNotepadRequested;
    public event Action? NewNoteRequested;
    public event Action? SelectionChanged;
    /// <summary>보기/편집이 바뀌었다(런처 안내 줄 문구가 달라진다).</summary>
    public event Action? EditingChanged;
    /// <summary>편집 칸에서 Esc: 검색 칸으로 돌아간다.</summary>
    public event Action? LeaveRequested;
    /// <summary>저장 실패·충돌 같은 알림(런처 안내 줄에 보인다).</summary>
    public event Action<string>? Message;

    public NotesView()
    {
        BackColor = Surface;

        // ── 왼쪽: 새 메모 + 목록
        _newButton = new Button
        {
            Dock = DockStyle.Top,
            Text = "+ 새 메모   Ctrl+N",
            TextAlign = ContentAlignment.MiddleLeft,
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface,
            ForeColor = Accent,
            Font = _barFont,
            TabStop = false,
            Cursor = Cursors.Hand,
        };
        _newButton.FlatAppearance.BorderColor = Border;
        _newButton.FlatAppearance.MouseOverBackColor = Selected;
        _newButton.Click += (_, _) => NewNoteRequested?.Invoke();

        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Surface,
            DrawMode = DrawMode.OwnerDrawFixed,
            IntegralHeight = false,
            TabStop = false,
        };
        _list.DrawItem += OnDrawItem;
        _list.SelectedIndexChanged += (_, _) => ShowSelected();
        _list.DoubleClick += (_, _) => BeginEdit();

        var listGap = new Panel { Dock = DockStyle.Top, BackColor = Surface };
        var left = new Panel { Dock = DockStyle.Left, BackColor = Surface };
        left.Controls.Add(_list);
        left.Controls.Add(listGap);
        left.Controls.Add(_newButton);

        // ── 오른쪽: 도구줄 + 본문(보기/편집)
        _bar = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.System,
            BackColor = Surface,
            Font = _barFont,
            CanOverflow = false,
            TabStop = false,
        };
        _modeButton = new ToolStripButton("편집  F2") { ForeColor = Accent, ToolTipText = "보기 ↔ 편집 (F2, 두 번 클릭)" };
        _modeButton.Click += (_, _) => { if (IsEditing) EndEdit(); else BeginEdit(); };
        _bar.Items.Add(_modeButton);
        _bar.Items.Add(new ToolStripSeparator());
        AddFormat("B", "굵게 (Ctrl+B)", () => Wrap("**", "**"), _boldBarFont);
        AddFormat("I", "기울임 (Ctrl+I)", () => Wrap("*", "*"), _italicBarFont);
        AddFormat("제목1", "큰 제목 (Ctrl+1)", () => SetLinePrefix("# "));
        AddFormat("제목2", "작은 제목 (Ctrl+2)", () => SetLinePrefix("## "));
        AddFormat("• 목록", "글머리 목록 (Ctrl+L)", () => SetLinePrefix("- "));
        AddFormat("1. 번호", "번호 목록 (Ctrl+Shift+L)", () => SetLinePrefix("1. "));
        AddFormat("☐ 할 일", "할 일 (Ctrl+T)", () => SetLinePrefix("- [ ] "));
        AddFormat("코드", "코드 (Ctrl+E)", WrapCode);
        var notepad = new ToolStripButton("메모장에서 열기") { Alignment = ToolStripItemAlignment.Right, ToolTipText = "메모장으로 열기(런처는 닫힘)" };
        notepad.Click += (_, _) =>
        {
            if (SelectedNote is { } note && SaveNow())
                OpenInNotepadRequested?.Invoke(note);
        };
        _bar.Items.Add(notepad);
        _status = new ToolStripLabel { Alignment = ToolStripItemAlignment.Right, ForeColor = Muted };
        _bar.Items.Add(_status);

        _view = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ForeColor = Ink,
            Font = _bodyFont,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = true,
            // 검색어 위치를 포커스 없이도 보이게
            HideSelection = false,
            TabStop = false,
        };
        _view.LinkClicked += (_, e) =>
        {
            if (e.LinkText is { } url)
                ItemLauncher.Start(new LauncherItem { Path = url });
        };
        _view.DoubleClick += (_, _) => BeginEdit();

        _editor = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ForeColor = Ink,
            Font = _bodyFont,
            HideSelection = false,
            Visible = false,
            TabStop = false,
        };
        _editor.TextChanged += (_, _) =>
        {
            if (_loadingEditor)
                return;
            _dirty = true;
            _status.Text = "고치는 중…";
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        _editor.KeyDown += OnEditorKeyDown;
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveNow();
        };

        _frame = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        _frame.Controls.Add(_view);
        _frame.Controls.Add(_editor);
        _frame.Paint += (_, e) =>
        {
            using var pen = new Pen(IsEditing ? Accent : Border);
            e.Graphics.DrawRectangle(pen, 0, 0, _frame.Width - 1, _frame.Height - 1);
        };
        var right = new Panel { Dock = DockStyle.Fill, BackColor = Surface };
        right.Controls.Add(_frame);
        right.Controls.Add(_bar);

        var gap = new Panel { Dock = DockStyle.Left, BackColor = Surface };
        Controls.Add(right);
        Controls.Add(gap);
        Controls.Add(left);

        Resize += (_, _) =>
        {
            left.Width = Math.Clamp(Width / 3, S(220), S(380));
            _newButton.Height = S(34);
            listGap.Height = S(6);
            _list.ItemHeight = S(46);
            gap.Width = S(12);
            _frame.Padding = new Padding(S(14), S(10), S(6), S(10));
        };
        UpdateBar();
    }

    private void AddFormat(string text, string tip, Action apply, Font? font = null)
    {
        var button = new ToolStripButton(text) { ToolTipText = tip };
        if (font is not null)
            button.Font = font;
        button.Click += (_, _) =>
        {
            // 보기 중에 누르면 편집으로 들어가서 적용한다
            if (!IsEditing && !BeginEdit())
                return;
            apply();
            _editor.Focus();
        };
        _bar.Items.Add(button);
        _formatItems.Add(button);
    }

    public NoteFile? SelectedNote => _list.SelectedItem as NoteFile;
    public int TotalCount => _all.Count;
    public int VisibleCount => _list.Items.Count;
    public string? SelectedText => SelectedNote is { } n ? (IsEditing ? _editor.Text : ContentOf(n)) : null;
    public bool IsEditing => _editing is not null;
    public bool EditorFocused => _editor.Focused;

    /// <summary>폴더를 다시 읽는다(메모 화면으로 바꿀 때, 새로 만들거나 옮긴 뒤). keepPath 가 있으면 그 메모를 고른다.</summary>
    public void Reload(string folder, string? keepPath = null)
    {
        SaveNow();
        keepPath ??= SelectedNote?.Path;
        _all = Notes.List(folder);
        _content.Clear();
        Filter(_query, keepPath);
    }

    /// <summary>제목에 맞는 메모를 먼저, 그다음 내용에 맞는 메모(각각 최근 수정순).</summary>
    public void Filter(string query, string? keepPath = null)
    {
        _query = query.Trim();
        _hitLine.Clear();
        IEnumerable<NoteFile> hits = _all;
        if (_query.Length > 0)
        {
            var byTitle = _all.Where(n => n.Title.Contains(_query, StringComparison.OrdinalIgnoreCase)).ToList();
            var byContent = new List<NoteFile>();
            foreach (var n in _all.Except(byTitle))
            {
                var line = FindLine(ContentOf(n), _query);
                if (line is null)
                    continue;
                _hitLine[n.Path] = line;
                byContent.Add(n);
            }
            hits = byTitle.Concat(byContent);
        }

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var n in hits)
            _list.Items.Add(n);
        _list.EndUpdate();

        int index = keepPath is null ? -1 : _list.Items.Cast<NoteFile>().ToList().FindIndex(n => n.Path == keepPath);
        // 검색어를 바꾸면(keepPath 없음) 가장 잘 맞는 첫 메모를 고른다
        if (index < 0)
            index = _list.Items.Count > 0 ? 0 : -1;
        // 같은 줄이 다시 골라지면 이벤트가 없으므로 검색어 강조를 위해 직접 다시 그린다
        _list.SelectedIndex = index;
        ShowSelected();
    }

    public void MoveSelection(int delta)
    {
        if (_list.Items.Count == 0)
            return;
        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + delta, 0, _list.Items.Count - 1);
    }

    /// <summary>본문을 위·아래로 한 화면씩 넘긴다(PageUp/PageDown).</summary>
    public void ScrollPreview(bool down)
    {
        const int WmVScroll = 0x0115, SbPageUp = 2, SbPageDown = 3;
        SendMessage((IsEditing ? (Control)_editor : _view).Handle, WmVScroll, down ? SbPageDown : SbPageUp, 0);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    // ── 보기 ────────────────────────────────────────────────

    private void ShowSelected()
    {
        // 다른 메모로 옮기면 하던 편집을 저장하고 보기로 돌아간다
        if (IsEditing && _editing?.Path != SelectedNote?.Path)
            EndEdit();
        if (IsEditing)
            return;
        if (SelectedNote is not { } note)
        {
            _view.Clear();
            UpdateBar();
            SelectionChanged?.Invoke();
            return;
        }
        var text = ContentOf(note);
        if (IsMarkdown(note.Path))
        {
            _view.Rtf = MarkdownRtf.Render(text);
        }
        else
        {
            _view.Clear();
            _view.SelectionFont = _bodyFont;
            _view.SelectionColor = Ink;
            _view.Text = text;
        }
        // 검색어가 있으면 그 위치를 골라 보인다(서식 입힌 글자에서 찾는다)
        int at = _query.Length > 0 ? _view.Find(_query, RichTextBoxFinds.None) : -1;
        if (at < 0)
            _view.Select(0, 0);
        _view.ScrollToCaret();
        UpdateBar();
        SelectionChanged?.Invoke();
    }

    private static bool IsMarkdown(string path) =>
        string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase);

    private string ContentOf(NoteFile note)
    {
        if (_content.TryGetValue(note.Path, out var text))
            return text;
        try
        {
            text = Notes.Read(note.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text = $"(읽지 못했습니다: {ex.Message})";
        }
        _content[note.Path] = text;
        return text;
    }

    private static string? FindLine(string text, string query)
    {
        int at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return null;
        int start = text.LastIndexOf('\n', at) + 1;
        int end = text.IndexOf('\n', at);
        return text[start..(end < 0 ? text.Length : end)].Trim();
    }

    // ── 편집·저장 ───────────────────────────────────────────

    /// <summary>고른 메모를 편집 칸으로 연다. 1MB 보다 큰 파일은 일부만 읽었으므로 막는다.</summary>
    public bool BeginEdit()
    {
        if (SelectedNote is not { } note)
            return false;
        if (IsEditing)
        {
            _editor.Focus();
            return true;
        }
        DateTime stamp;
        try
        {
            var info = new FileInfo(note.Path);
            if (info.Length > Notes.MaxReadBytes)
            {
                Message?.Invoke("1MB 보다 큰 메모는 여기서 고치지 않습니다(앞부분만 읽음). '메모장에서 열기'를 쓰세요.");
                return false;
            }
            stamp = info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message?.Invoke($"메모를 열지 못했습니다: {ex.Message}");
            return false;
        }
        // 편집 직전에 다시 읽는다(목록을 연 뒤 메모장에서 고쳤을 수 있다)
        _content.Remove(note.Path);
        _loadingEditor = true;
        _editor.Text = ContentOf(note).ReplaceLineEndings("\r\n");
        _loadingEditor = false;
        _editing = note;
        _editingStamp = stamp;
        _dirty = false;
        _view.Visible = false;
        _editor.Visible = true;
        _editor.Select(_editor.TextLength, 0);
        _editor.Focus();
        _status.Text = "";
        UpdateBar();
        EditingChanged?.Invoke();
        return true;
    }

    /// <summary>저장하고 보기로 돌아간다.</summary>
    public void EndEdit()
    {
        if (!IsEditing)
            return;
        SaveNow();
        _editing = null;
        _editor.Visible = false;
        _view.Visible = true;
        UpdateBar();
        ShowSelected();
        EditingChanged?.Invoke();
    }

    /// <summary>
    /// 고친 내용이 있으면 저장한다. 그사이 다른 프로그램이 파일을 고쳤으면 덮어쓰지 않고 "(충돌)" 사본으로 저장한다.
    /// 저장하지 못하면 false.
    /// </summary>
    public bool SaveNow()
    {
        _saveTimer.Stop();
        if (_editing is not { } note || !_dirty)
            return true;
        var text = _editor.Text;
        try
        {
            var path = note.Path;
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) != _editingStamp)
            {
                path = Notes.SiblingPath(path, " (충돌)");
                Message?.Invoke($"다른 곳에서 고친 파일이라 덮어쓰지 않고 '{Path.GetFileName(path)}'로 저장했습니다.");
            }
            Notes.Save(path, text);
            if (path == note.Path)
            {
                _editingStamp = File.GetLastWriteTimeUtc(path);
                _content[path] = text.ReplaceLineEndings("\n");
            }
            _dirty = false;
            _status.Text = $"저장됨 {DateTime.Now:HH:mm}";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = "저장 실패";
            Message?.Invoke($"저장하지 못했습니다: {ex.Message}");
            return false;
        }
    }

    /// <summary>창이 닫힐 때: 저장에 실패하면 고친 내용을 클립보드에 남기고 알린다(잃지 않게).</summary>
    public void SaveOnClose()
    {
        if (SaveNow() || _editing is null)
            return;
        try
        {
            Clipboard.SetText(_editor.Text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
        }
        MessageBox.Show($"'{_editing.Title}' 메모를 저장하지 못했습니다. 고친 내용은 클립보드에 복사해 두었습니다.",
            "DeskKeeper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void UpdateBar()
    {
        bool has = SelectedNote is not null;
        _modeButton.Text = IsEditing ? "보기  F2" : "편집  F2";
        _modeButton.Enabled = has;
        foreach (var item in _formatItems)
            item.Enabled = has;
        if (!IsEditing)
            _status.Text = "";
        _frame.Invalidate();
    }

    /// <summary>편집 칸 단축키. 편집 칸이 포커스를 가진 동안 런처보다 먼저 받는다.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F2)
        {
            if (IsEditing)
            {
                EndEdit();
                LeaveRequested?.Invoke();
            }
            else
            {
                BeginEdit();
            }
            return true;
        }
        if (!_editor.Focused)
            return base.ProcessCmdKey(ref msg, keyData);
        switch (keyData)
        {
            case Keys.Escape:
                EndEdit();
                LeaveRequested?.Invoke();
                return true;
            case Keys.Control | Keys.B: Wrap("**", "**"); return true;
            case Keys.Control | Keys.I: Wrap("*", "*"); return true;
            case Keys.Control | Keys.E: WrapCode(); return true;
            case Keys.Control | Keys.D1: SetLinePrefix("# "); return true;
            case Keys.Control | Keys.D2: SetLinePrefix("## "); return true;
            case Keys.Control | Keys.D3: SetLinePrefix("### "); return true;
            case Keys.Control | Keys.L: SetLinePrefix("- "); return true;
            case Keys.Control | Keys.Shift | Keys.L: SetLinePrefix("1. "); return true;
            case Keys.Control | Keys.T: SetLinePrefix("- [ ] "); return true;
            case Keys.Control | Keys.S: SaveNow(); return true;
            case Keys.Control | Keys.N: NewNoteRequested?.Invoke(); return true;
            case Keys.Control | Keys.A: _editor.SelectAll(); return true;
            case Keys.Tab: Indent(+1); return true;
            case Keys.Shift | Keys.Tab: Indent(-1); return true;
        }
        // Tab 등이 런처(화면 전환)까지 가지 않게, 나머지는 편집 칸이 처리한다
        return false;
    }

    /// <summary>목록 줄에서 Enter: 다음 줄에 같은 기호를 이어 준다. 빈 항목에서 Enter 면 기호를 지운다.</summary>
    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter || e.Modifiers != Keys.None || _editor.SelectionLength > 0)
            return;
        var (start, end) = LineBounds(_editor.SelectionStart, _editor.SelectionStart);
        var line = _editor.Text[start..end];
        var m = System.Text.RegularExpressions.Regex.Match(line, @"^(\s*)([-*+] \[[ xX]\] |[-*+] |(\d+)[.)] )(.*)$");
        if (!m.Success)
            return;
        e.SuppressKeyPress = true;
        if (m.Groups[4].Value.Trim().Length == 0)
        {
            // 빈 항목: 기호만 지우고 목록을 끝낸다
            _editor.Select(start, end - start);
            _editor.SelectedText = "";
            return;
        }
        var marker = m.Groups[2].Value;
        if (m.Groups[3].Success)
            marker = $"{int.Parse(m.Groups[3].Value) + 1}{marker[m.Groups[3].Length..]}";
        else if (marker.Contains('['))
            marker = marker[0] + " [ ] ";
        _editor.SelectedText = "\r\n" + m.Groups[1].Value + marker;
    }

    /// <summary>고른 글자를 앞뒤 기호로 감싼다. 이미 감싸져 있으면 푼다. 고른 게 없으면 기호 사이에 커서를 둔다.</summary>
    private void Wrap(string before, string after)
    {
        int s = _editor.SelectionStart, n = _editor.SelectionLength;
        var text = _editor.Text;
        if (n > 0 && s >= before.Length && s + n + after.Length <= text.Length
            && text.AsSpan(s - before.Length, before.Length).SequenceEqual(before)
            && text.AsSpan(s + n, after.Length).SequenceEqual(after))
        {
            _editor.Select(s - before.Length, n + before.Length + after.Length);
            _editor.SelectedText = text.Substring(s, n);
            _editor.Select(s - before.Length, n);
            return;
        }
        var selected = _editor.SelectedText;
        _editor.SelectedText = before + selected + after;
        _editor.Select(s + before.Length, n);
    }

    /// <summary>여러 줄을 골랐으면 ``` 코드 블록, 아니면 `코드`.</summary>
    private void WrapCode()
    {
        if (_editor.SelectedText.Contains('\n'))
        {
            var (start, end) = LineBounds(_editor.SelectionStart, _editor.SelectionStart + _editor.SelectionLength);
            var block = _editor.Text[start..end];
            _editor.Select(start, end - start);
            _editor.SelectedText = "```\r\n" + block + "\r\n```";
        }
        else
        {
            Wrap("`", "`");
        }
    }

    /// <summary>
    /// 고른 줄들의 머리 기호를 바꾼다(제목·목록·번호·할 일). 모든 줄이 이미 그 기호면 지운다.
    /// 다른 머리 기호가 있으면 바꿔 끼우고, 들여쓰기는 남긴다. 번호는 1. 2. 3. 으로 다시 매긴다.
    /// </summary>
    private void SetLinePrefix(string prefix)
    {
        var (start, end) = LineBounds(_editor.SelectionStart, _editor.SelectionStart + _editor.SelectionLength);
        var lines = _editor.Text[start..end].Split("\r\n");
        var head = new System.Text.RegularExpressions.Regex(@"^(\s*)(#{1,3} |[-*+] \[[ xX]\] |[-*+] |\d+[.)] )?(.*)$");
        bool numbered = prefix == "1. ";
        // 여러 줄이면 빈 줄은 기호를 붙이지도, "이미 있음" 판단에 넣지도 않는다
        bool allHave = lines.Where(l => lines.Length == 1 || l.Trim().Length > 0).All(l =>
        {
            var m = head.Match(l);
            var current = m.Groups[2].Value;
            return numbered ? System.Text.RegularExpressions.Regex.IsMatch(current, @"^\d+[.)] $") : current == prefix;
        });
        int number = 1;
        var result = lines.Select(l =>
        {
            var m = head.Match(l);
            if (l.Trim().Length == 0 && lines.Length > 1)
                return l;
            var body = m.Groups[3].Value;
            // 제목은 들여쓰기 없이
            var indent = prefix.StartsWith('#') ? "" : m.Groups[1].Value;
            if (allHave)
                return indent + body;
            return indent + (numbered ? $"{number++}. " : prefix) + body;
        });
        var replaced = string.Join("\r\n", result);
        _editor.Select(start, end - start);
        _editor.SelectedText = replaced;
        _editor.Select(lines.Length == 1 ? start + replaced.Length : start, lines.Length == 1 ? 0 : replaced.Length);
    }

    /// <summary>고른 줄들을 2칸 들여쓰기(+1) 또는 내어쓰기(-1). 목록 단계를 바꿀 때 쓴다.</summary>
    private void Indent(int direction)
    {
        var (start, end) = LineBounds(_editor.SelectionStart, _editor.SelectionStart + _editor.SelectionLength);
        var lines = _editor.Text[start..end].Split("\r\n");
        var result = lines.Select(l => direction > 0 ? "  " + l : l.StartsWith("  ") ? l[2..] : l.TrimStart(' '));
        var replaced = string.Join("\r\n", result);
        int caret = _editor.SelectionStart;
        _editor.Select(start, end - start);
        _editor.SelectedText = replaced;
        if (lines.Length == 1)
            _editor.Select(Math.Max(start, caret + (replaced.Length - (end - start))), 0);
        else
            _editor.Select(start, replaced.Length);
    }

    /// <summary>위치 a~b 가 걸친 줄 전체의 시작·끝(줄바꿈 제외).</summary>
    private (int Start, int End) LineBounds(int a, int b)
    {
        var text = _editor.Text;
        int start = a == 0 ? 0 : text.LastIndexOf('\n', Math.Max(0, a - 1)) + 1;
        // 고른 끝이 다음 줄 맨 앞이면 그 줄은 넣지 않는다
        if (b > a && b > 0 && text[b - 1] == '\n')
            b--;
        int end = text.IndexOf('\r', b);
        if (end < 0)
            end = text.Length;
        return (start, Math.Max(start, end));
    }

    // ── 목록 그리기 ─────────────────────────────────────────

    private void OnDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count)
            return;
        var note = (NoteFile)_list.Items[e.Index];
        var g = e.Graphics;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? Selected : Surface))
            g.FillRectangle(back, e.Bounds);

        const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        int pad = S(10);
        var titleRect = new Rectangle(e.Bounds.X + pad, e.Bounds.Y + S(6), e.Bounds.Width - pad * 2, S(20));
        TextRenderer.DrawText(g, note.Title, _titleFont, titleRect, Ink, flags);
        // 둘째 줄: 내용에서 맞은 줄, 아니면 수정일
        var sub = _hitLine.TryGetValue(note.Path, out var line) ? line : note.Modified.ToString("yyyy-MM-dd HH:mm");
        var subRect = new Rectangle(titleRect.X, e.Bounds.Y + S(26), titleRect.Width, S(16));
        TextRenderer.DrawText(g, sub, _smallFont, subRect, Muted, flags);
    }

    private int S(int logicalPixels) => LogicalToDeviceUnits(logicalPixels);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _saveTimer.Dispose();
            _titleFont.Dispose();
            _smallFont.Dispose();
            _bodyFont.Dispose();
            _barFont.Dispose();
            _boldBarFont.Dispose();
            _italicBarFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
