using System.Drawing.Drawing2D;
using DeskKeeper.Core;

namespace DeskKeeper.Tray;

/// <summary>
/// 런처의 타일 패널. 분류마다 제목 줄과 타일 묶음을 그리고, 타일은 왼쪽부터 채워 줄을 넘긴다.
/// 직접 그리는 컨트롤이라 스크롤·선택·마우스 위치 판정도 여기서 한다.
/// </summary>
sealed class TileView : Control
{
    /// <summary>
    /// 분류 구역 하나. Group 이 null 이면 "분류 없음". ShowHeader 가 false 면 제목 없이 타일만.
    /// Compact 면 큰 타일 대신 한 줄짜리 목록 칸(작은 아이콘 + 이름)을 여러 열로 채운다(최근 파일).
    /// </summary>
    public sealed record Section(string? Group, string Title, Color Hue, IReadOnlyList<LauncherItem> Items, bool ShowHeader, bool Compact = false);

    private sealed record TileBox(LauncherItem Item, Section Section, Rectangle Bounds);
    /// <summary>Bounds 는 제목 줄, Block 은 제목과 타일을 합친 구역 전체(끌어다 놓기 판정용).</summary>
    private sealed record HeaderBox(Section Section, Rectangle Bounds, Rectangle Block);

    private static readonly Color Surface = Color.FromArgb(249, 249, 249);
    private static readonly Color Ink = Color.FromArgb(26, 26, 26);
    private static readonly Color Muted = Color.FromArgb(96, 96, 96);
    private static readonly Color Line = Color.FromArgb(226, 226, 226);

    private readonly ShellIconCache _icons;
    private readonly Font _nameFont = new("Segoe UI", 9.5f);
    private readonly Font _headerFont = new("Segoe UI", 9f, FontStyle.Bold);
    private readonly Font _countFont = new("Segoe UI", 8.5f);
    private List<Section> _sections = [];
    private List<TileBox> _tiles = [];
    private List<HeaderBox> _headers = [];
    private int _contentHeight;
    private int _scrollY;
    private int _selected = -1;
    private int _hover = -1;

    public TileView(ShellIconCache icons)
    {
        _icons = icons;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Surface;
    }

    /// <summary>타일 색. 없으면 구역 색을 쓴다(검색 결과처럼 한 구역에 여러 분류가 섞일 때 필요).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<LauncherItem, Color>? TileHue { get; set; }

    /// <summary>타일을 눌렀을 때(왼쪽 클릭 한 번 = 실행).</summary>
    public event Action<LauncherItem>? TileClicked;

    /// <summary>선택이나 마우스 위치가 바뀌었을 때(아래 안내 줄에 경로를 보이려고).</summary>
    public event Action? FocusChanged;

    public LauncherItem? SelectedItem => _selected >= 0 && _selected < _tiles.Count ? _tiles[_selected].Item : null;

    /// <summary>마우스가 올라간 타일, 없으면 선택된 타일.</summary>
    public LauncherItem? HotItem => _hover >= 0 && _hover < _tiles.Count ? _tiles[_hover].Item : SelectedItem;

    public bool HasTiles => _tiles.Count > 0;

    private int TileWidth => LogicalToDeviceUnits(124);
    private int TileHeight => LogicalToDeviceUnits(100);
    private int Gap => LogicalToDeviceUnits(10);
    private int HeaderHeight => LogicalToDeviceUnits(34);
    private int Inset => LogicalToDeviceUnits(4);
    private int CompactWidth => LogicalToDeviceUnits(240);
    private int CompactHeight => LogicalToDeviceUnits(30);
    private int CompactGapX => LogicalToDeviceUnits(8);
    private int CompactGapY => LogicalToDeviceUnits(2);

    public void SetSections(List<Section> sections)
    {
        _sections = sections;
        _scrollY = 0;
        _hover = -1;
        Relayout();
        _selected = _tiles.Count > 0 ? 0 : -1;
        Invalidate();
        FocusChanged?.Invoke();
    }

    /// <summary>주어진 너비에서 전부 보이는 데 필요한 높이.</summary>
    public int MeasureHeight(int width) => ComputeLayout(width, out _, out _);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Relayout();
    }

    private void Relayout()
    {
        _contentHeight = ComputeLayout(ClientSize.Width, out _tiles, out _headers);
        ClampScroll();
    }

    private int ComputeLayout(int width, out List<TileBox> tiles, out List<HeaderBox> headers)
    {
        tiles = [];
        headers = [];
        int usable = width - 2 * Inset;
        int perRow = Math.Max(1, (usable + Gap) / (TileWidth + Gap));
        int blockGap = LogicalToDeviceUnits(28);

        // 분류 구역을 글자처럼 가로로 흘린다: 구역 너비 = 타일 수만큼(한 줄을 넘으면 전체 너비),
        // 지금 줄에 안 들어가면 다음 띠로 넘긴다. 띠 높이는 그 띠에서 가장 높은 구역.
        int x = Inset, bandTop = 0, bandHeight = 0;
        foreach (var section in _sections)
        {
            if (section.Compact)
            {
                // 목록 칸 구역: 늘 새 띠에서 전체 너비로, 칸을 왼쪽부터 여러 열로 채운다
                if (x > Inset)
                {
                    bandTop += bandHeight + Gap;
                    x = Inset;
                }
                int head = section.ShowHeader ? HeaderHeight : 0;
                int columns = Math.Max(1, (usable + CompactGapX) / (CompactWidth + CompactGapX));
                int cellWidth = (usable - (columns - 1) * CompactGapX) / columns;
                int lines = (section.Items.Count + columns - 1) / columns;
                int height = head + lines * (CompactHeight + CompactGapY) - CompactGapY;
                var area = new Rectangle(Inset, bandTop, usable, height);
                if (section.ShowHeader)
                    headers.Add(new HeaderBox(section, new Rectangle(Inset, bandTop, usable, HeaderHeight), area));
                for (int i = 0; i < section.Items.Count; i++)
                {
                    var rect = new Rectangle(Inset + (i % columns) * (cellWidth + CompactGapX),
                        bandTop + head + (i / columns) * (CompactHeight + CompactGapY), cellWidth, CompactHeight);
                    tiles.Add(new TileBox(section.Items[i], section, rect));
                }
                bandTop += height + Gap;
                bandHeight = 0;
                continue;
            }

            int cols = Math.Min(section.Items.Count, perRow);
            int rows = (section.Items.Count + perRow - 1) / perRow;
            int blockWidth = Math.Max(TileWidth, cols * (TileWidth + Gap) - Gap);
            int header = section.ShowHeader ? HeaderHeight : 0;
            int blockHeight = header + rows * (TileHeight + Gap) - Gap;

            if (x > Inset && x + blockWidth > Inset + usable)
            {
                bandTop += bandHeight + Gap;
                x = Inset;
                bandHeight = 0;
            }

            var block = new Rectangle(x, bandTop, blockWidth, blockHeight);
            if (section.ShowHeader)
                headers.Add(new HeaderBox(section, new Rectangle(x, bandTop, blockWidth, HeaderHeight), block));
            for (int i = 0; i < section.Items.Count; i++)
            {
                int col = i % perRow;
                int row = i / perRow;
                var rect = new Rectangle(x + col * (TileWidth + Gap), bandTop + header + row * (TileHeight + Gap), TileWidth, TileHeight);
                tiles.Add(new TileBox(section.Items[i], section, rect));
            }

            bandHeight = Math.Max(bandHeight, blockHeight);
            x += blockWidth + blockGap;
        }
        if (_sections.Count == 0)
            return 0;
        // 마지막이 목록 칸 구역이면 bandTop 이 이미 그 아래(+Gap)를 가리킨다
        return bandHeight > 0 ? bandTop + bandHeight + Inset : bandTop - Gap + Inset;
    }

    // ── 선택·이동 ──────────────────────────────────────────

    /// <summary>←→ 는 순서대로, ↑↓ 는 위·아래 줄에서 가로 위치가 가장 가까운 타일로.</summary>
    public void MoveSelection(Keys key)
    {
        if (_tiles.Count == 0)
            return;
        if (_selected < 0)
        {
            SelectIndex(0);
            return;
        }
        var current = _tiles[_selected].Bounds;
        int next = key switch
        {
            Keys.Left => Math.Max(0, _selected - 1),
            Keys.Right => Math.Min(_tiles.Count - 1, _selected + 1),
            Keys.Up => NearestInRow(current, below: false),
            Keys.Down => NearestInRow(current, below: true),
            _ => _selected,
        };
        SelectIndex(next);
    }

    private int NearestInRow(Rectangle from, bool below)
    {
        // 바로 위(아래) 줄의 Y 를 찾고, 그 줄에서 가로 중심이 가장 가까운 타일
        var candidates = _tiles.Select((t, i) => (t, i))
            .Where(x => below ? x.t.Bounds.Top > from.Top : x.t.Bounds.Top < from.Top)
            .ToList();
        if (candidates.Count == 0)
            return _selected;
        int rowTop = below ? candidates.Min(x => x.t.Bounds.Top) : candidates.Max(x => x.t.Bounds.Top);
        int cx = from.Left + from.Width / 2;
        return candidates.Where(x => x.t.Bounds.Top == rowTop)
            .OrderBy(x => Math.Abs(x.t.Bounds.Left + x.t.Bounds.Width / 2 - cx))
            .First().i;
    }

    private void SelectIndex(int index)
    {
        if (index == _selected)
            return;
        _selected = index;
        EnsureVisible(index);
        Invalidate();
        FocusChanged?.Invoke();
    }

    private void EnsureVisible(int index)
    {
        if (index < 0)
            return;
        var r = _tiles[index].Bounds;
        if (r.Top - Inset < _scrollY)
            _scrollY = r.Top - Inset;
        else if (r.Bottom + Inset > _scrollY + ClientSize.Height)
            _scrollY = r.Bottom + Inset - ClientSize.Height;
        ClampScroll();
    }

    private void ClampScroll() =>
        _scrollY = Math.Clamp(_scrollY, 0, Math.Max(0, _contentHeight - ClientSize.Height));

    // ── 마우스 위치 판정 ───────────────────────────────────

    private Point ToContent(Point p) => new(p.X, p.Y + _scrollY);

    public LauncherItem? ItemAt(Point clientPoint)
    {
        var p = ToContent(clientPoint);
        return _tiles.FirstOrDefault(t => t.Bounds.Contains(p))?.Item;
    }

    /// <summary>분류 제목 위면 그 구역(Group 이 null 이면 분류 없음 구역).</summary>
    public Section? HeaderAt(Point clientPoint)
    {
        var p = ToContent(clientPoint);
        return _headers.FirstOrDefault(h => h.Bounds.Contains(p))?.Section;
    }

    /// <summary>그 위치가 속한 구역의 분류(제목 줄부터 다음 제목 전까지). 구역 밖이면 null.</summary>
    public string? GroupAt(Point clientPoint)
    {
        var p = ToContent(clientPoint);
        if (!ClientRectangle.Contains(clientPoint))
            return null;
        // 구역들이 가로로도 흐르므로, 놓은 점을 포함하는(구역 사이 틈까지 넉넉히 잡은) 구역을 찾는다
        int slack = LogicalToDeviceUnits(14);
        return _headers.FirstOrDefault(h => Rectangle.Inflate(h.Block, slack, slack).Contains(p))?.Section.Group;
    }

    public void SelectItem(LauncherItem item)
    {
        int index = _tiles.FindIndex(t => ReferenceEquals(t.Item, item));
        if (index >= 0)
            SelectIndex(index);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = ToContent(e.Location);
        int hover = _tiles.FindIndex(t => t.Bounds.Contains(p));
        if (hover != _hover)
        {
            _hover = hover;
            Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
            FocusChanged?.Invoke();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover >= 0)
        {
            _hover = -1;
            Invalidate();
            FocusChanged?.Invoke();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // 오른쪽 클릭한 타일을 선택해 두어야 메뉴가 그 타일을 대상으로 한다
        if (ItemAt(e.Location) is { } item)
            SelectItem(item);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left && ItemAt(e.Location) is { } item)
            TileClicked?.Invoke(item);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scrollY -= e.Delta * (TileHeight + Gap) / 120;
        ClampScroll();
        Invalidate();
    }

    // ── 그리기 ─────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Surface);
        g.TranslateTransform(0, -_scrollY);

        foreach (var h in _headers)
            DrawHeader(g, h);
        for (int i = 0; i < _tiles.Count; i++)
        {
            var t = _tiles[i];
            if (t.Bounds.Bottom < _scrollY || t.Bounds.Top > _scrollY + ClientSize.Height)
                continue;
            DrawTile(g, t, selected: i == _selected, hover: i == _hover);
        }

        g.ResetTransform();
        DrawScrollThumb(g);
    }

    private void DrawHeader(Graphics g, HeaderBox h)
    {
        var b = h.Bounds;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int dot = LogicalToDeviceUnits(8);
        int textY = b.Bottom - LogicalToDeviceUnits(10) - _headerFont.Height;
        int dotY = textY + (_headerFont.Height - dot) / 2;
        using (var brush = new SolidBrush(h.Section.Hue))
            g.FillEllipse(brush, b.Left + LogicalToDeviceUnits(2), dotY, dot, dot);
        g.SmoothingMode = SmoothingMode.None;

        int x = b.Left + LogicalToDeviceUnits(16);
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var titleSize = TextRenderer.MeasureText(g, h.Section.Title, _headerFont, Size.Empty, flags);
        TextRenderer.DrawText(g, h.Section.Title, _headerFont, new Point(x, textY), Ink, flags);
        var count = h.Section.Items.Count.ToString();
        var countSize = TextRenderer.MeasureText(g, count, _countFont, Size.Empty, flags);
        int countX = x + titleSize.Width + LogicalToDeviceUnits(6);
        TextRenderer.DrawText(g, count, _countFont, new Point(countX, textY + (_headerFont.Height - _countFont.Height) / 2), Muted, flags);

        using var pen = new Pen(Line);
        int lineY = textY + _headerFont.Height / 2;
        int lineStart = countX + countSize.Width + LogicalToDeviceUnits(10);
        // 타일 하나짜리 좁은 구역에서는 제목이 구역 너비를 채우므로 선을 생략한다
        if (b.Right - lineStart > LogicalToDeviceUnits(12))
            g.DrawLine(pen, lineStart, lineY, b.Right, lineY);
    }

    private void DrawTile(Graphics g, TileBox t, bool selected, bool hover)
    {
        if (t.Section.Compact)
        {
            DrawCompact(g, t, selected, hover);
            return;
        }
        var hue = TileHue?.Invoke(t.Item) ?? t.Section.Hue;
        // 바탕은 분류 색을 흰색에 옅게 섞는다. 마우스를 올리면 조금, 선택하면 더 진하게
        var fill = Blend(hue, selected ? 0.24 : hover ? 0.17 : 0.11);
        var border = selected ? hue : Blend(hue, hover ? 0.38 : 0.24);
        float borderWidth = selected ? LogicalToDeviceUnits(2) : 1f;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(t.Bounds.X + borderWidth / 2, t.Bounds.Y + borderWidth / 2,
            t.Bounds.Width - borderWidth, t.Bounds.Height - borderWidth);
        using (var path = Rounded(rect, LogicalToDeviceUnits(10)))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(border, borderWidth))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = SmoothingMode.None;

        int iconSize = SystemInformation.IconSize.Width;
        int iconTop = t.Bounds.Top + LogicalToDeviceUnits(14);
        if (_icons.Get(t.Item.Path, large: true) is { } icon)
            g.DrawIcon(icon, new Rectangle(t.Bounds.Left + (t.Bounds.Width - iconSize) / 2, iconTop, iconSize, iconSize));

        int pad = LogicalToDeviceUnits(8);
        var nameRect = new Rectangle(t.Bounds.Left + pad, iconTop + iconSize + LogicalToDeviceUnits(8),
            t.Bounds.Width - 2 * pad, _nameFont.Height * 2);
        TextRenderer.DrawText(g, t.Item.Name, _nameFont, nameRect, Ink,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);
    }

    /// <summary>목록 칸: 평소엔 바탕 없이 작은 아이콘 + 이름 한 줄, 마우스·선택 때만 옅은 바탕.</summary>
    private void DrawCompact(Graphics g, TileBox t, bool selected, bool hover)
    {
        var b = t.Bounds;
        var hue = TileHue?.Invoke(t.Item) ?? t.Section.Hue;
        if (selected || hover)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Rounded(new RectangleF(b.X, b.Y, b.Width - 1, b.Height - 1), LogicalToDeviceUnits(6));
            using var brush = new SolidBrush(Blend(hue, selected ? 0.20 : 0.10));
            g.FillPath(brush, path);
            if (selected)
            {
                using var pen = new Pen(hue, LogicalToDeviceUnits(1));
                g.DrawPath(pen, path);
            }
            g.SmoothingMode = SmoothingMode.None;
        }

        int icon = SystemInformation.SmallIconSize.Width;
        int pad = LogicalToDeviceUnits(8);
        if (_icons.Get(t.Item.Path) is { } small)
            g.DrawIcon(small, new Rectangle(b.X + pad, b.Y + (b.Height - icon) / 2, icon, icon));
        int textLeft = b.X + pad + icon + LogicalToDeviceUnits(8);
        TextRenderer.DrawText(g, t.Item.Name, _nameFont, new Rectangle(textLeft, b.Y, b.Right - textLeft - pad, b.Height), Ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    /// <summary>내용이 넘칠 때만 오른쪽에 얇은 스크롤 위치 표시.</summary>
    private void DrawScrollThumb(Graphics g)
    {
        int view = ClientSize.Height;
        if (_contentHeight <= view || view <= 0)
            return;
        int width = LogicalToDeviceUnits(4);
        int thumb = Math.Max(LogicalToDeviceUnits(24), view * view / _contentHeight);
        int top = (int)((long)(view - thumb) * _scrollY / Math.Max(1, _contentHeight - view));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(new RectangleF(ClientSize.Width - width - 1, top, width, thumb), width / 2f);
        using var brush = new SolidBrush(Color.FromArgb(160, 160, 160));
        g.FillPath(brush, path);
        g.SmoothingMode = SmoothingMode.None;
    }

    /// <summary>색을 흰색 위에 amount(0~1) 만큼 섞는다.</summary>
    public static Color Blend(Color hue, double amount) => Color.FromArgb(
        (int)(255 + (hue.R - 255) * amount),
        (int)(255 + (hue.G - 255) * amount),
        (int)(255 + (hue.B - 255) * amount));

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _nameFont.Dispose();
            _headerFont.Dispose();
            _countFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
