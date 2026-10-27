using DeskKeeper.Core;

namespace DeskKeeper.Tray;

/// <summary>
/// 단축키로 띄우는 검색 창. 다른 곳을 누르면 닫히고, 닫히면 Dispose 되어 메모리를 남기지 않는다.
/// </summary>
sealed class LauncherForm : Form
{
    public const string WindowTitle = "DeskKeeper 런처";

    private static readonly Color Accent = Color.FromArgb(15, 118, 110);
    private static readonly Color Border = Color.FromArgb(200, 200, 200);

    private readonly SettingsStore _store;
    private readonly TextBox _query;
    private readonly ListBox _list;
    private readonly Label _hint;
    private readonly Font _queryFont = new("Malgun Gothic", 14f);
    private readonly Font _itemFont = new("Malgun Gothic", 10.5f);
    private readonly Font _pathFont = new("Malgun Gothic", 8.5f);
    private readonly System.Windows.Forms.Timer _dragWatch = new() { Interval = 100 };
    private bool _droppedWhileInactive;

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
        BackColor = Color.White;
        Padding = new Padding(12);
        Size = new Size(600, 400);

        _query = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = _queryFont,
            BorderStyle = BorderStyle.None,
            PlaceholderText = "이름으로 찾기",
        };
        var queryPanel = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(4, 4, 4, 10) };
        queryPanel.Controls.Add(_query);

        _list = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 30,
            Font = _itemFont,
            IntegralHeight = false,
        };
        _list.DrawItem += OnDrawItem;
        _list.DoubleClick += (_, _) => LaunchSelected();
        _list.MouseDown += OnListMouseDown;
        _list.ContextMenuStrip = BuildItemMenu();

        _hint = new Label { Dock = DockStyle.Bottom, Height = 24, ForeColor = Color.Gray, TextAlign = ContentAlignment.BottomLeft };

        // Fill 을 먼저 넣어야 Top·Bottom 이 가장자리를 먼저 차지한다
        Controls.Add(_list);
        Controls.Add(queryPanel);
        Controls.Add(_hint);

        _query.TextChanged += (_, _) => RefreshList();

        // 끌어다 놓기는 자식 컨트롤로 전달되지 않으므로 모두에 건다
        foreach (Control c in new Control[] { this, queryPanel, _query, _list, _hint })
        {
            c.AllowDrop = true;
            c.DragEnter += OnDragEnter;
            c.DragDrop += OnDragDrop;
        }

        _dragWatch.Tick += OnDragWatchTick;

        PlaceOnCursorScreen();
        RefreshList();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _query.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Escape:
                Close();
                break;
            case Keys.Enter:
                LaunchSelected();
                break;
            case Keys.Down:
                MoveSelection(+1);
                break;
            case Keys.Up:
                MoveSelection(-1);
                break;
            default:
                return;
        }
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (IsDisposed)
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
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Link : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths)
            return;
        _droppedWhileInactive = true;
        try
        {
            int added = _store.AddLauncherItems(paths);
            _query.Clear();
            RefreshList();
            _hint.Text = added > 0 ? $"{added}개를 등록했습니다." : "이미 등록된 항목입니다.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _hint.Text = ex.Message;
        }
    }

    private void RefreshList()
    {
        _store.ReloadIfChanged();
        var all = _store.Current.Launcher;
        var q = _query.Text.Trim();
        // 이름에 포함되면 찾고, 이름이 검색어로 시작하는 항목을 앞에 둔다(등록 순서는 유지)
        var hits = q.Length == 0
            ? all
            : all.Where(i => i.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                 .OrderBy(i => i.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                 .ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var item in hits)
            _list.Items.Add(item);
        _list.EndUpdate();
        if (_list.Items.Count > 0)
            _list.SelectedIndex = 0;

        _hint.Text = _store.LoadError is not null
            ? "settings.json 에 오류가 있습니다. 트레이 메뉴의 '설정 파일 열기'로 고쳐 주세요."
            : all.Count == 0
                ? "등록된 항목이 없습니다. 파일·폴더를 이 창에 끌어다 놓으세요."
                : "Enter 실행 · Esc 닫기 · 끌어다 놓아 등록 · 우클릭 메뉴";
    }

    private void MoveSelection(int delta)
    {
        if (_list.Items.Count == 0)
            return;
        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + delta, 0, _list.Items.Count - 1);
    }

    private void LaunchSelected()
    {
        if (_list.SelectedItem is not LauncherItem item)
            return;
        // 오류 대화상자가 런처 뒤에 깔리지 않게 먼저 닫는다
        Close();
        ItemLauncher.Start(item);
    }

    private void OnListMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
            return;
        int index = _list.IndexFromPoint(e.Location);
        if (index >= 0)
            _list.SelectedIndex = index;
    }

    private ContextMenuStrip BuildItemMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("실행", null, (_, _) => LaunchSelected());
        menu.Items.Add("파일 위치 열기", null, (_, _) =>
        {
            if (_list.SelectedItem is LauncherItem item)
            {
                Close();
                ItemLauncher.RevealInExplorer(item.Path);
            }
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("목록에서 제거 (파일은 그대로)", null, (_, _) =>
        {
            if (_list.SelectedItem is not LauncherItem item)
                return;
            try
            {
                _store.RemoveLauncherItem(item.Path);
                RefreshList();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                _hint.Text = ex.Message;
            }
        });
        menu.Opening += (_, e) => e.Cancel = _list.SelectedItem is null;
        return menu;
    }

    private void OnDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0)
            return;
        var item = (LauncherItem)_list.Items[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;

        using (var bg = new SolidBrush(selected ? Accent : _list.BackColor))
            e.Graphics.FillRectangle(bg, e.Bounds);

        var nameColor = selected ? Color.White : Color.FromArgb(30, 30, 30);
        var pathColor = selected ? Color.FromArgb(210, 235, 232) : Color.Gray;
        var b = e.Bounds;
        var nameRect = new Rectangle(b.X + 8, b.Y, b.Width * 2 / 5, b.Height);
        var pathRect = new Rectangle(nameRect.Right + 8, b.Y, b.Right - nameRect.Right - 16, b.Height);
        const TextFormatFlags common = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(e.Graphics, item.Name, _itemFont, nameRect, nameColor, common | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, item.Path, _pathFont, pathRect, pathColor, common | TextFormatFlags.PathEllipsis);
    }

    /// <summary>마우스가 있는 모니터의 가운데, 위에서 1/4 지점에 띄운다.</summary>
    private void PlaceOnCursorScreen()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + area.Height / 4);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _dragWatch.Dispose();
            _list.ContextMenuStrip?.Dispose();
            _queryFont.Dispose();
            _itemFont.Dispose();
            _pathFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
