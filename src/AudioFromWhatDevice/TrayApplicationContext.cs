using System.Text.Json;

namespace AudioFromWhatDevice;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly AudioMonitor monitor = new();
    private readonly TrayLabels labels = new();
    private readonly ContextMenuStrip menu = new();
    private readonly TraySlot primary;

    private readonly System.Windows.Forms.Timer timer = new() { Interval = 200 };
    private readonly DeviceWindow window;
    private TrayBadge? visibleBadge;
    private System.Windows.Forms.Timer? smokeTimer;
    private bool disposed;

    public TrayApplicationContext()
    {
        window = new DeviceWindow(labels);
        window.LabelsChanged += (_, _) => UpdateDisplay();
        primary = new TraySlot(menu, ShowWindow);
        menu.Opening += (_, _) => RebuildMenu();
        timer.Tick += (_, _) => UpdateDisplay();
        UpdateDisplay();
        timer.Start();
    }

    private bool IsStale => (DateTimeOffset.Now - monitor.Latest.CapturedAt).TotalSeconds > 5;

    private void UpdateDisplay() => UpdateDisplay(monitor.Latest);

    private void UpdateDisplay(MonitorSnapshot snapshot)
    {
        var stale = (DateTimeOffset.Now - snapshot.CapturedAt).TotalSeconds > 5;
        visibleBadge = TrayPresentation.Build(snapshot, stale, labels.Resolve(snapshot.Devices), visibleBadge?.Id);
        // Reuse one shell icon for every device and status; never allocate per-device tray slots.
        primary.Show(visibleBadge, TrayBadgeRenderer.IconSize());
        if (window.Visible) window.UpdateSnapshot(snapshot, stale);
    }

    private void RebuildMenu()
    {
        while (menu.Items.Count > 0)
        {
            var item = menu.Items[0];
            menu.Items.RemoveAt(0);
            item.Dispose();
        }
        var snapshot = monitor.Latest;
        var names = labels.Resolve(snapshot.Devices);
        menu.Items.Add(new ToolStripMenuItem("单图标：播放设备优先，空闲显示默认设备") { Enabled = false });
        if (snapshot.Error is not null || IsStale)
            menu.Items.Add(new ToolStripMenuItem(IsStale ? "数据已过期" : snapshot.Error) { Enabled = false });
        foreach (var device in snapshot.Devices.OrderBy(d => d.Name))
        {
            var item = new ToolStripMenuItem($"[{names[device.Id]}] {device.Name} · {device.Status}" + (device.Id == snapshot.DefaultOutputId ? " · 系统默认" : ""))
            {
                Checked = !IsStale && device.IsAvailable && device.Activity is Activity.Signal or Activity.Recent,
                ToolTipText = device.Error ?? device.Id
            };
            item.Click += (_, _) => ShowWindow();
            menu.Items.Add(item);
        }
        if (snapshot.Devices.Length == 0 && snapshot.Error is null)
            menu.Items.Add(new ToolStripMenuItem("没有可用的输出端点") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("设置设备简称 / 查看电平…", null, (_, _) => ShowWindow());
        menu.Items.Add("退出", null, (_, _) => ExitThread());
    }

    private void ShowWindow()
    {
        window.UpdateSnapshot(monitor.Latest, IsStale);
        if (!window.Visible)
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            window.Location = new Point(Math.Max(area.Left, area.Right - window.Width - 12),
                Math.Max(area.Top, area.Bottom - window.Height - 12));
            window.Show();
        }
        window.Activate();
    }

    internal void EnableSmokeTest(string path)
    {
        smokeTimer = new System.Windows.Forms.Timer { Interval = 2500 };
        smokeTimer.Tick += (_, _) =>
        {
            smokeTimer.Stop();
            // Record the exact snapshot used for badges, avoiding races with the monitor thread.
            var snapshot = monitor.Latest;
            UpdateDisplay(snapshot);
            RebuildMenu();
            window.UpdateSnapshot(snapshot, IsStale);
            Program.WriteSnapshot(path, snapshot);
            File.WriteAllText(path + ".badges.json", JsonSerializer.Serialize(new[] { visibleBadge }));
            TrayBadgeRenderer.WritePreview(path + ".png");
            ExitThread();
        };
        smokeTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            timer.Dispose();
            smokeTimer?.Dispose();
            primary.Dispose();

            menu.Dispose();
            window.Dispose();
            monitor.Dispose();
        }
        base.Dispose(disposing);
    }
}
internal sealed class DeviceWindow : Form
{
    private readonly Label summary = new() { Dock = DockStyle.Top, Height = 42, Padding = new Padding(10) };
    private readonly ListView devices = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
        HideSelection = false, ShowItemToolTips = true
    };
    private readonly TextBox details = new()
    {
        Dock = DockStyle.Bottom, Height = 86, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical
    };
    private readonly Dictionary<string, ListViewItem> rows = new(StringComparer.Ordinal);
    private MonitorSnapshot snapshot = MonitorSnapshot.Initial;

    private readonly TrayLabels labels;
    private readonly TextBox aliasInput = new() { Width = 90, MaxLength = 4 };
    private readonly Label aliasHint = new() { AutoSize = true, Padding = new Padding(0, 5, 0, 0) };
    private string? editingId;
    public event EventHandler? LabelsChanged;

    public DeviceWindow(TrayLabels labels)
    {
        this.labels = labels;
        Text = "音频输出监测 · 设备简称";
        ClientSize = new Size(950, 475);
        MinimumSize = new Size(800, 430);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9);
        devices.Columns.Add("输出设备 / 端点标识", 310);
        devices.Columns.Add("托盘文字", 85);
        devices.Columns.Add("状态", 160);
        devices.Columns.Add("电平 dBFS", 90);
        devices.Columns.Add("系统音量", 85);
        devices.Columns.Add("电平表", 100);
        var note = new Label
        {
            Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(10, 5, 10, 5),
            Text = "只显示一个图标：播放时显示输出设备，空闲时显示默认设备。蓝牙宝蓝色，扬声器绿色。\n" +
                "50 ms 采样，600 ms 保持。软件电平表无法确认独占播放或耳机本体是否发声。"
        };
        var aliasBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(10, 4, 0, 0) };
        var save = new Button { Text = "保存简称", AutoSize = true };
        var reset = new Button { Text = "恢复自动", AutoSize = true };
        aliasBar.Controls.Add(new Label { Text = "选中设备简称：", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
        aliasBar.Controls.Add(aliasInput);
        aliasBar.Controls.Add(save);
        aliasBar.Controls.Add(reset);
        aliasBar.Controls.Add(aliasHint);
        save.Click += (_, _) => SaveAlias(aliasInput.Text);
        reset.Click += (_, _) => SaveAlias("");
        Controls.Add(devices);
        Controls.Add(aliasBar);
        Controls.Add(details);
        Controls.Add(note);
        Controls.Add(summary);
        devices.SelectedIndexChanged += (_, _) => UpdateDetails();
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
    }

    public void UpdateSnapshot(MonitorSnapshot value, bool stale)
    {
        snapshot = value;
        summary.Text = stale ? "数据已过期，正在等待音频服务" : value.Error ??
            $"可用输出端点 {value.Devices.Length} 个 · 检测到信号 {value.Devices.Count(d => d.Activity == Activity.Signal)} 个";
        var names = labels.Resolve(value.Devices);
        var ids = value.Devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        devices.BeginUpdate();
        try
        {
            foreach (var id in rows.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                devices.Items.Remove(rows[id]);
                rows.Remove(id);
            }
            foreach (var device in value.Devices.OrderBy(d => d.Name))
            {
                if (!rows.TryGetValue(device.Id, out var row))
                {
                    row = new ListViewItem([device.Label, "", "", "", "", ""]) { Tag = device.Id };
                    rows.Add(device.Id, row);
                    devices.Items.Add(row);
                }
                row.Text = (device.Id == value.DefaultOutputId ? "默认 · " : "") + device.Label;
                row.SubItems[1].Text = names[device.Id];
                row.SubItems[2].Text = stale ? "数据已过期" : device.Status;
                row.SubItems[3].Text = device.Error is not null ? "—" : device.Peak > 0
                    ? $"{20 * Math.Log10(device.Peak):F1}" : "−∞";
                row.SubItems[4].Text = device.Error is null ? $"{device.Volume:P0}" : "—";
                row.SubItems[5].Text = device.HardwareMeter ? "硬件" : "软件";
                row.ForeColor = stale || device.Error is not null ? Color.DarkOrange :
                    device.Activity is Activity.Signal or Activity.Recent ? (device.IsBluetooth ? Color.RoyalBlue : Color.SeaGreen) : SystemColors.GrayText;
                row.ToolTipText = device.Error ?? device.Id;
            }
        }
        finally { devices.EndUpdate(); }
        if (devices.SelectedItems.Count == 0 && devices.Items.Count > 0) devices.Items[0].Selected = true;
        UpdateDetails();
    }

    private void SaveAlias(string value)
    {
        if (editingId is null) return;
        try
        {
            labels.Save(editingId, value);
            aliasInput.Text = labels.Alias(editingId) ?? "";
            UpdateSnapshot(snapshot, (DateTimeOffset.Now - snapshot.CapturedAt).TotalSeconds > 5);
            LabelsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show(this, ex.Message, "简称未保存", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void UpdateDetails()
    {
        var id = devices.SelectedItems.Count > 0 ? devices.SelectedItems[0].Tag as string : null;
        var device = snapshot.Devices.FirstOrDefault(d => d.Id == id);
        if (id != editingId)
        {
            editingId = id;
            aliasInput.Text = id is null ? "" : labels.Alias(id) ?? "";
        }
        aliasInput.Enabled = device is not null;
        aliasHint.Text = device is null ? "先选择设备" : "1–2 个汉字或 1–4 个字母/数字；越短越清晰";
        var text = device is null ? "选择设备可查看完整标识。关闭此窗口后继续驻留托盘。" :
            $"{device.Name}\r\nEndpoint ID: {device.Id}\r\nContainer ID: {device.ContainerId ?? "驱动未提供"}" +
            (device.Error is null ? "" : $"\r\n{device.Error}");
        if (labels.LoadError is not null) text += "\r\n" + labels.LoadError;
        if (details.Text != text) details.Text = text;
    }
}
