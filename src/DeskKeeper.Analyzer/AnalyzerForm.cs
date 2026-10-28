using System.Diagnostics;
using System.Text;
using DeskKeeper.Core;

namespace DeskKeeper.Analyzer;

/// <summary>
/// 폴더 분석 창. 왼쪽은 폴더별 용량 트리, 오른쪽은 파일 표(가상 모드).
/// 체크한 파일만, 확인을 거쳐, 휴지통으로 보낸다.
/// </summary>
sealed class AnalyzerForm : Form
{
    private const int ColCheck = 0, ColName = 1, ColSize = 2, ColDate = 3, ColCategory = 4, ColFolder = 5;

    private static readonly (string Label, long Bytes)[] SizeFilters =
    [
        ("전체", 0), ("1 MB 이상", 1L << 20), ("10 MB 이상", 10L << 20), ("100 MB 이상", 100L << 20), ("1 GB 이상", 1L << 30),
    ];

    private static readonly (string Label, int Days)[] AgeFilters =
    [
        ("전체", 0), ("30일 넘음", 30), ("90일 넘음", 90), ("180일 넘음", 180), ("1년 넘음", 365), ("2년 넘음", 730),
    ];

    private static readonly StringComparer NameComparer = StringComparer.CurrentCultureIgnoreCase;

    private readonly SettingsStore _store = new();
    private readonly ToolStripLabel _pathLabel = new();
    private readonly ComboBox _categoryFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _ageFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _sizeFilter = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _nameFilter = new() { PlaceholderText = "이름에 포함 (Ctrl+F)" };
    /// <summary>표가 비었을 때(스캔 중·결과 없음) 표 대신 보이는 안내.</summary>
    private readonly Label _empty = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(80, 80, 80), BackColor = SystemColors.Window, Visible = false };
    private readonly System.Windows.Forms.Timer _nameDebounce = new() { Interval = 250 };
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, BorderStyle = BorderStyle.None };
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _btnDelete = new() { Text = "휴지통으로 보내기", AutoSize = true, Enabled = false };
    private readonly Button _btnCheckVisible = new() { Text = "보이는 항목 모두 체크", AutoSize = true };
    private readonly Button _btnUncheckAll = new() { Text = "체크 모두 해제", AutoSize = true };
    private readonly ToolStripButton _btnRescan = new("다시 스캔 (F5)");

    /// <summary>null 이면 정리 후보 모드(감시 폴더 기한·임시 표시 기준).</summary>
    private string? _rootPath;
    private Categorizer _categorizer = new(Categorizer.Defaults());
    private ScanResult? _scan;
    private List<FileEntry> _view = [];
    private DirNode? _scope;
    private CancellationTokenSource? _scanCts;
    private int _sortColumn = ColSize;
    private bool _sortDescending = true;
    private string? _lastAction;

    public AnalyzerForm(string? rootPath)
    {
        _rootPath = rootPath is null ? null : Path.GetFullPath(rootPath);

        // Segoe UI: 맑은 고딕은 경로의 \ 를 ₩ 로 그린다. 한글은 글꼴 연결로 맑은 고딕이 자동으로 쓰인다
        Font = new Font("Segoe UI", 9f);
        // 글꼴(pt)은 화면 배율을 따라 커지므로 픽셀로 정한 크기도 같이 키운다(125~200% 화면에서 잘림 방지)
        Size = new Size(S(1200), S(760));
        MinimumSize = new Size(S(960), S(480));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        _categoryFilter.Width = S(120);
        _ageFilter.Width = S(100);
        _sizeFilter.Width = S(100);
        _nameFilter.Width = S(180);
        _btnDelete.Font = new Font(Font, FontStyle.Bold);

        var tool = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        tool.Items.Add(new ToolStripButton("폴더 바꾸기…", null, (_, _) => ChangeFolder()));
        tool.Items.Add(_btnRescan);
        tool.Items.Add(new ToolStripButton("분류 규칙 편집", null, (_, _) => EditCategories()));
        tool.Items.Add(new ToolStripSeparator());
        // 경로·설명은 버튼이 아니므로 흐린 글자로 구분한다
        _pathLabel.ForeColor = Color.FromArgb(96, 96, 96);
        tool.Items.Add(_pathLabel);
        _btnRescan.Click += (_, _) => StartScan();

        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, Height = S(36), Padding = new Padding(S(6), S(6), S(6), 0), WrapContents = false };
        filters.Controls.AddRange(
        [
            FilterLabel("분류"), _categoryFilter,
            FilterLabel("수정한 지"), _ageFilter,
            FilterLabel("크기"), _sizeFilter,
            FilterLabel("이름"), _nameFilter,
        ]);
        foreach (var (label, _) in SizeFilters)
            _sizeFilter.Items.Add(label);
        _sizeFilter.SelectedIndex = 0;
        foreach (var (label, _) in AgeFilters)
            _ageFilter.Items.Add(label);
        _ageFilter.SelectedIndex = 0;
        _categoryFilter.SelectedIndexChanged += (_, _) => ApplyFilter();
        _sizeFilter.SelectedIndexChanged += (_, _) => ApplyFilter();
        _ageFilter.SelectedIndexChanged += (_, _) => ApplyFilter();
        _nameFilter.TextChanged += (_, _) => { _nameDebounce.Stop(); _nameDebounce.Start(); };
        _nameDebounce.Tick += (_, _) => { _nameDebounce.Stop(); ApplyFilter(); };

        SetupTree();
        SetupGrid();

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };
        // 창 크기가 정해지기 전에 SplitterDistance 를 주면 작게 잘리므로 처음 표시할 때 정한다
        Load += (_, _) => split.SplitterDistance = S(320);
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(_grid);
        split.Panel2.Controls.Add(_empty);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        buttons.Controls.AddRange([_btnDelete, _btnUncheckAll, _btnCheckVisible]);
        _btnDelete.Click += (_, _) => SendCheckedToRecycleBin();
        _btnCheckVisible.Click += (_, _) => SetChecked(_view, true);
        _btnUncheckAll.Click += (_, _) => SetChecked(_scan?.Files ?? [], false);
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(40), Padding = new Padding(S(8), S(4), S(8), S(4)) };
        bottom.Controls.Add(_status);
        bottom.Controls.Add(buttons);

        // Fill 을 먼저 넣어야 나머지가 가장자리를 먼저 차지한다
        Controls.Add(split);
        Controls.Add(filters);
        Controls.Add(tool);
        Controls.Add(bottom);

        UpdateTitle();
    }

    /// <summary>폴더 선택 창. 취소하면 null.</summary>
    public static string? PickFolder(IWin32Window? owner, string? initial)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "분석할 폴더를 고르세요",
            UseDescriptionForTitle = true,
            InitialDirectory = initial is not null && Directory.Exists(initial)
                ? initial
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        return result == DialogResult.OK ? dialog.SelectedPath : null;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        StartScan();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.F5 && _btnRescan.Enabled)
            StartScan();
        else if (e.KeyCode == Keys.F && e.Control)
        {
            _nameFilter.Focus();
            _nameFilter.SelectAll();
        }
        // 이름 필터에 글자를 지우는 중일 때는 가로채지 않는다. 휴지통 확인 창은 그대로 뜬다
        else if (e.KeyCode == Keys.Delete && !_nameFilter.Focused && _btnDelete.Enabled)
            SendCheckedToRecycleBin();
        else
            return;
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    /// <summary>100% 기준 픽셀 값을 현재 화면 배율에 맞춘다.</summary>
    private int S(int logicalPixels) => LogicalToDeviceUnits(logicalPixels);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _scanCts?.Cancel();
        base.OnFormClosing(e);
    }

    private Label FilterLabel(string text) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(S(10), S(6), S(2), 0) };

    // ── 스캔 ─────────────────────────────────────────────

    private async void StartScan()
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();

        _store.ReloadIfChanged();
        _categorizer = Categorizer.FromSettings(_store.Current);
        RebuildCategoryFilter();

        SetBusy(true);
        _status.Text = "스캔 중…";
        ShowEmpty("파일 정보를 읽는 중입니다…");
        var progress = new Progress<int>(n =>
        {
            if (!cts.IsCancellationRequested)
                _status.Text = $"스캔 중… 파일 {n:N0}개";
        });
        try
        {
            var root = _rootPath;
            var categorizer = _categorizer;
            var settings = _store.Current;
            var result = await Task.Run(() => root is null
                ? Expiry.ScanCandidates(settings, categorizer, progress, cts.Token)
                : FolderScanner.Scan(root, categorizer, progress, cts.Token));
            if (cts.IsCancellationRequested || IsDisposed)
                return;
            _scan = result;
            _lastAction = null;
            ShowScan();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"스캔하지 못했습니다: {ex.Message}";
            ShowEmpty($"폴더를 읽지 못했습니다.\n{ex.Message}\n\n[폴더 바꾸기…]로 다른 폴더를 고르거나 F5 로 다시 시도하세요.");
        }
        finally
        {
            if (_scanCts == cts && !IsDisposed)
                SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _btnRescan.Enabled = !busy;
        _grid.Enabled = !busy;
        _tree.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void ShowScan()
    {
        if (_scan is null)
            return;
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        var rootNode = CreateTreeNode(_scan.Root);
        _tree.Nodes.Add(rootNode);
        rootNode.Expand();
        _tree.EndUpdate();
        _scope = _scan.Root;
        _tree.SelectedNode = rootNode;
        ApplyFilter();
    }

    private void ChangeFolder()
    {
        var picked = PickFolder(this, _rootPath);
        if (picked is null)
            return;
        _rootPath = Path.GetFullPath(picked);
        UpdateTitle();
        StartScan();
    }

    private void UpdateTitle()
    {
        Text = _rootPath is null ? "DeskKeeper 분석 — 정리 후보" : $"DeskKeeper 분석 — {_rootPath}";
        _pathLabel.Text = _rootPath ?? "감시 폴더의 기한을 넘긴 파일 + 날짜가 지난 임시 표시 파일";
    }

    private void EditCategories()
    {
        try
        {
            _store.EnsureCategoriesWritten();
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_store.FilePath}\"") { UseShellExecute = true })?.Dispose();
            MessageBox.Show(this, "settings.json 의 \"categories\" 를 고쳐 저장한 뒤 [다시 스캔]을 누르세요.\n위에 있는 규칙이 먼저 적용됩니다.",
                "분류 규칙 편집", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, ex.Message, "분류 규칙 편집", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RebuildCategoryFilter()
    {
        var previous = _categoryFilter.SelectedItem as string;
        _categoryFilter.BeginUpdate();
        _categoryFilter.Items.Clear();
        _categoryFilter.Items.Add("전체 분류");
        foreach (var name in _categorizer.Names)
            _categoryFilter.Items.Add(name);
        _categoryFilter.EndUpdate();
        int index = previous is null ? 0 : _categoryFilter.Items.IndexOf(previous);
        _categoryFilter.SelectedIndex = index < 0 ? 0 : index;
    }

    // ── 폴더 트리 ────────────────────────────────────────

    private void SetupTree()
    {
        // 하위 폴더는 펼칠 때 용량 큰 순으로 채운다(수만 개 폴더를 한 번에 만들지 않음)
        _tree.BeforeExpand += (_, e) =>
        {
            var node = e.Node!;
            if (node.Nodes.Count != 1 || node.Nodes[0].Tag is not null)
                return;
            var dir = (DirNode)node.Tag!;
            _tree.BeginUpdate();
            node.Nodes.Clear();
            foreach (var child in dir.Children.OrderByDescending(c => c.TotalSize))
                node.Nodes.Add(CreateTreeNode(child));
            _tree.EndUpdate();
        };
        _tree.AfterSelect += (_, e) =>
        {
            if (e.Node?.Tag is DirNode dir && dir != _scope)
            {
                _scope = dir;
                ApplyFilter();
            }
        };
        _tree.NodeMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                _tree.SelectedNode = e.Node;
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("탐색기에서 열기", null, (_, _) =>
        {
            // 정리 후보 모드의 가상 노드("정리 후보", "임시 표시")는 실제 폴더가 없다
            if (_tree.SelectedNode?.Tag is DirNode { FullPath.Length: > 0 } dir)
                OpenInExplorer(dir.FullPath, select: false);
        });
        _tree.ContextMenuStrip = menu;
    }

    private static TreeNode CreateTreeNode(DirNode dir)
    {
        var node = new TreeNode(TreeText(dir)) { Tag = dir };
        if (dir.Children.Count > 0)
            node.Nodes.Add(new TreeNode("…"));   // 펼침 표시용 자리 표시자(Tag 없음)
        return node;
    }

    private static string TreeText(DirNode dir) =>
        $"{dir.Name}   {SizeText.Format(dir.TotalSize)} · {dir.TotalFiles:N0}개";

    private void RefreshTreeTexts(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            if (node.Tag is DirNode dir)
            {
                node.Text = TreeText(dir);
                RefreshTreeTexts(node.Nodes);
            }
        }
    }

    // ── 파일 표 ──────────────────────────────────────────

    private void SetupGrid()
    {
        _grid.VirtualMode = true;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = true;
        _grid.BorderStyle = BorderStyle.None;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.AutoGenerateColumns = false;
        _grid.RowTemplate.Height = S(24);
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(222, 240, 238);
        _grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(26, 26, 26);
        _grid.DefaultCellStyle.Padding = new Padding(S(4), 0, S(4), 0);
        // 칸마다 검은 격자 대신 줄 사이 옅은 가로선만. 머리글은 평평하게(왼쪽 위 칸 강조 제거)
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.GridColor = Color.FromArgb(232, 232, 232);
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(246, 246, 246);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(70, 70, 70);
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(246, 246, 246);
        _grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(S(4), S(6), S(4), S(6));

        _grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "", Width = S(32), SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "이름", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 45 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "크기", Width = S(90), DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight } });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "수정한 날", Width = S(130) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "분류", Width = S(90) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "폴더", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = 30 });
        foreach (DataGridViewColumn col in _grid.Columns)
        {
            if (col.Index != ColCheck)
                col.SortMode = DataGridViewColumnSortMode.Programmatic;
        }

        _grid.CellValueNeeded += OnCellValueNeeded;
        _grid.ColumnHeaderMouseClick += (_, e) =>
        {
            if (e.ColumnIndex == ColCheck)
                return;
            _sortDescending = e.ColumnIndex == _sortColumn ? !_sortDescending : e.ColumnIndex is ColSize or ColDate;
            _sortColumn = e.ColumnIndex;
            SortView();
            _grid.Invalidate();
        };
        // 체크 칸 클릭은 그 줄만 전환
        _grid.CellMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && e.ColumnIndex == ColCheck && e.RowIndex >= 0 && e.RowIndex < _view.Count)
            {
                var f = _view[e.RowIndex];
                f.Checked = !f.Checked;
                _grid.InvalidateRow(e.RowIndex);
                UpdateStatus();
            }
        };
        // 오른쪽 클릭한 줄이 선택에 없으면 그 줄만 선택
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0)
                return;
            if (!_grid.Rows[e.RowIndex].Selected)
            {
                _grid.ClearSelection();
                _grid.Rows[e.RowIndex].Selected = true;
            }
        };
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Space)
            {
                ToggleSelected();
                e.Handled = true;
            }
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("파일 열기", null, (_, _) =>
        {
            if (SelectedFiles().FirstOrDefault() is { } f)
                OpenFile(f.FullPath);
        });
        menu.Items.Add("파일 위치 열기", null, (_, _) =>
        {
            if (SelectedFiles().FirstOrDefault() is { } f)
                OpenInExplorer(f.FullPath, select: true);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("선택한 줄 체크 전환", null, (_, _) => ToggleSelected()) { ShortcutKeyDisplayString = "Space" });
        menu.Items.Add(new ToolStripSeparator());
        var markItem = new ToolStripMenuItem("", null, (_, _) => MarkSelected());
        menu.Items.Add(markItem);
        // 같은 동작(날짜 붙이기)이지만 모드에 따라 뜻이 달라 문구를 바꾼다
        menu.Opening += (_, _) => markItem.Text = _rootPath is null
            ? $"보류: {_store.Current.MarkDays}일 뒤 다시 보기"
            : $"임시 표시: {_store.Current.MarkDays}일 뒤 정리 후보로";
        _grid.ContextMenuStrip = menu;
    }

    private void OnCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _view.Count)
            return;
        var f = _view[e.RowIndex];
        e.Value = e.ColumnIndex switch
        {
            ColCheck => f.Checked,
            ColName => f.Name,
            ColSize => SizeText.Format(f.Size),
            ColDate => f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
            ColCategory => f.Category,
            ColFolder => f.Dir.RelativePath,
            _ => null,
        };
    }

    private List<FileEntry> SelectedFiles()
    {
        var list = new List<FileEntry>();
        foreach (DataGridViewRow row in _grid.SelectedRows)
        {
            if (row.Index >= 0 && row.Index < _view.Count)
                list.Add(_view[row.Index]);
        }
        return list;
    }

    /// <summary>선택한 줄 중 하나라도 체크 안 됐으면 모두 체크, 모두 체크돼 있으면 모두 해제.</summary>
    private void ToggleSelected()
    {
        var selected = SelectedFiles();
        if (selected.Count == 0)
            return;
        SetChecked(selected, selected.Any(f => !f.Checked));
    }

    private void SetChecked(IEnumerable<FileEntry> files, bool value)
    {
        foreach (var f in files)
            f.Checked = value;
        _grid.Invalidate();
        UpdateStatus();
    }

    /// <summary>선택한 파일에 "오늘 + markDays" 날짜를 붙인다. 정리 후보 모드에서는 목록에서 뺀다(보류).</summary>
    private void MarkSelected()
    {
        var selected = SelectedFiles();
        if (_scan is null || selected.Count == 0)
            return;
        try
        {
            _store.ReloadIfChanged();
            var until = DateTime.Today.AddDays(_store.Current.MarkDays);
            _store.SetMarks(selected.Select(f => f.FullPath), until);
            if (_rootPath is null)
            {
                _scan.Remove(selected.ToHashSet());
                RefreshTreeTexts(_tree.Nodes);
                _lastAction = $"→ {selected.Count:N0}개 보류({until:yyyy-MM-dd}까지)";
            }
            else
            {
                _lastAction = $"→ {selected.Count:N0}개 임시 표시({until:yyyy-MM-dd} 이후 알림)";
            }
            ApplyFilter();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, ex.Message, "DeskKeeper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ── 필터·정렬 ────────────────────────────────────────

    private void ApplyFilter()
    {
        if (_scan is null)
            return;

        var category = _categoryFilter.SelectedIndex > 0 ? _categoryFilter.SelectedItem as string : null;
        int days = AgeFilters[Math.Max(0, _ageFilter.SelectedIndex)].Days;
        var cutoff = days > 0 ? DateTime.Now.AddDays(-days) : DateTime.MaxValue;
        long minSize = SizeFilters[Math.Max(0, _sizeFilter.SelectedIndex)].Bytes;
        var name = _nameFilter.Text.Trim();
        var scope = _scope is not null && _scope != _scan.Root ? _scope : null;

        _view = _scan.Files.Where(f =>
                (category is null || f.Category == category) &&
                f.LastWriteTime < cutoff &&
                f.Size >= minSize &&
                (name.Length == 0 || f.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) &&
                (scope is null || f.Dir.IsSameOrUnder(scope)))
            .ToList();
        SortView();

        _grid.RowCount = 0;
        _grid.RowCount = _view.Count;
        _grid.Invalidate();
        UpdateStatus();
        UpdateEmptyState();
    }

    /// <summary>결과가 없을 때 빈 표 대신 이유와 다음 행동을 보여 준다.</summary>
    private void UpdateEmptyState()
    {
        if (_scan is null || _view.Count > 0)
        {
            ShowEmpty(null);
            return;
        }
        if (_scan.Files.Count > 0)
        {
            ShowEmpty("조건에 맞는 파일이 없습니다.\n위의 필터를 바꾸거나 왼쪽 트리에서 위쪽 폴더를 고르세요.");
        }
        else if (_rootPath is null)
        {
            var watched = string.Join(", ", Expiry.EffectiveWatchedFolders(_store.Current)
                .Select(w => $"{Path.GetFileName(w.Path)} {w.Days}일"));
            ShowEmpty($"정리할 파일이 없습니다.\n\n감시 폴더: {watched}\n기간을 넘긴 파일이나 날짜가 지난 임시 표시 파일이 생기면 트레이 아이콘에 빨간 점이 찍힙니다.");
        }
        else
        {
            ShowEmpty("이 폴더에는 파일이 없습니다.");
        }
    }

    /// <summary>message 가 null 이면 표를, 아니면 안내를 보인다.</summary>
    private void ShowEmpty(string? message)
    {
        _empty.Text = message ?? "";
        _empty.Visible = message is not null;
        _grid.Visible = message is null;
    }

    private void SortView()
    {
        Comparison<FileEntry> compare = _sortColumn switch
        {
            ColName => (a, b) => NameComparer.Compare(a.Name, b.Name),
            ColDate => (a, b) => a.LastWriteTime.CompareTo(b.LastWriteTime),
            ColCategory => (a, b) => string.CompareOrdinal(a.Category, b.Category),
            ColFolder => (a, b) => NameComparer.Compare(a.Dir.RelativePath, b.Dir.RelativePath),
            _ => (a, b) => a.Size.CompareTo(b.Size),
        };
        _view.Sort(_sortDescending ? (a, b) => compare(b, a) : compare);

        foreach (DataGridViewColumn col in _grid.Columns)
        {
            if (col.SortMode == DataGridViewColumnSortMode.Programmatic)
            {
                col.HeaderCell.SortGlyphDirection = col.Index != _sortColumn ? SortOrder.None
                    : _sortDescending ? SortOrder.Descending : SortOrder.Ascending;
            }
        }
    }

    private void UpdateStatus()
    {
        if (_scan is null)
            return;
        long visibleSize = 0;
        foreach (var f in _view)
            visibleSize += f.Size;
        int checkedCount = 0;
        long checkedSize = 0;
        foreach (var f in _scan.Files)
        {
            if (f.Checked)
            {
                checkedCount++;
                checkedSize += f.Size;
            }
        }

        var sb = new StringBuilder();
        // 체크 합계는 주 행동 버튼에 보이므로 상태 표시줄에는 다시 쓰지 않는다
        sb.Append($"표시 {_view.Count:N0}개 · {SizeText.Format(visibleSize)}");
        if (_scan.SkippedDirs > 0)
            sb.Append($"     읽지 못한 폴더 {_scan.SkippedDirs:N0}개");
        if (_lastAction is not null)
            sb.Append($"     {_lastAction}");
        _status.Text = sb.ToString();
        _btnDelete.Enabled = checkedCount > 0;
        _btnDelete.Text = checkedCount > 0
            ? $"휴지통으로 보내기 ({checkedCount:N0}개 · {SizeText.Format(checkedSize)})"
            : "휴지통으로 보내기";
    }

    // ── 휴지통 ───────────────────────────────────────────

    private void SendCheckedToRecycleBin()
    {
        if (_scan is null)
            return;
        var targets = _scan.Files.Where(f => f.Checked).ToList();
        if (targets.Count == 0)
            return;

        long total = targets.Sum(f => f.Size);
        int hidden = targets.Count - _view.Count(f => f.Checked);
        var sb = new StringBuilder();
        sb.AppendLine($"체크한 파일 {targets.Count:N0}개({SizeText.Format(total)})를 휴지통으로 보냅니다.");
        if (hidden > 0)
            sb.AppendLine($"이 중 {hidden:N0}개는 지금 필터 때문에 화면에 보이지 않는 파일입니다.");
        sb.AppendLine();
        foreach (var f in targets.Take(8))
            sb.AppendLine("· " + (f.Dir.Parent is null ? f.Name : Path.Combine(f.Dir.RelativePath, f.Name)));
        if (targets.Count > 8)
            sb.AppendLine($"… 외 {targets.Count - 8:N0}개");
        sb.AppendLine();
        sb.Append("휴지통에 들어가지 못하는 파일(네트워크 드라이브, 아주 큰 파일)은 Windows 가 영구 삭제 전에 한 번 더 묻습니다.");

        var answer = MessageBox.Show(this, sb.ToString(), "휴지통으로 보내기",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.OK)
            return;

        UseWaitCursor = true;
        try
        {
            RecycleBin.Send(this, targets.Select(f => f.FullPath));
        }
        finally
        {
            UseWaitCursor = false;
        }

        // 실제로 사라진 파일만 목록에서 뺀다(취소·실패한 파일은 체크를 유지)
        var gone = targets.Where(f => !File.Exists(f.FullPath)).ToHashSet();
        _scan.Remove(gone);
        try
        {
            // 지운 파일에 붙어 있던 임시 표시·보류 기록도 정리
            _store.RemoveMarks(gone.Select(f => f.FullPath));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // 남은 기록은 상주 프로그램이 다음 점검 때 정리한다
        }
        int left = targets.Count - gone.Count;
        _lastAction = left == 0
            ? $"→ {gone.Count:N0}개를 휴지통으로 보냈습니다."
            : $"→ {gone.Count:N0}개 보냄, {left:N0}개는 취소·실패로 남음";
        RefreshTreeTexts(_tree.Nodes);
        ApplyFilter();
    }

    // ── 셸 ───────────────────────────────────────────────

    private void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(this, $"열지 못했습니다.\n{path}\n\n{ex.Message}", "DeskKeeper", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void OpenInExplorer(string path, bool select)
    {
        var args = select ? $"/select,\"{path}\"" : $"\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true })?.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _nameDebounce.Dispose();
            _scanCts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
