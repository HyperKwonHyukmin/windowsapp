namespace DeskKeeper.Tray;

/// <summary>이름 한 줄을 받는 작은 대화상자(분류 만들기·이름 바꾸기). Enter 확인, Esc 취소.</summary>
sealed class NamePrompt : Form
{
    private readonly TextBox _input;

    public NamePrompt(string title, string prompt, string initial)
    {
        Text = title;
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(LogicalToDeviceUnits(14));

        var label = new Label { Text = prompt, AutoSize = true, Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(6)) };
        _input = new TextBox { Text = initial, Width = LogicalToDeviceUnits(320), Margin = new Padding(0, 0, 0, LogicalToDeviceUnits(12)) };
        var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        };
        buttons.Controls.AddRange([cancel, ok]);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(label);
        layout.Controls.Add(_input);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        AcceptButton = ok;
        CancelButton = cancel;
    }

    public string Value => _input.Text;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _input.Focus();
        _input.SelectAll();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Font.Dispose();
        base.Dispose(disposing);
    }
}
