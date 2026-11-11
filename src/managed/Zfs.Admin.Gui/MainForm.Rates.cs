using System.Drawing.Drawing2D;
using Zfs.Admin;

namespace Zfs.Admin.Gui;

/// <summary>
/// The Rates tab: IOPS and MB/s per pool and device from the deltas of
/// pool.iostat's cumulative counters, the last 10 minutes as a chart, and
/// the ZFS memory figures.  It polls only while it is the tab in view.
/// </summary>
sealed partial class MainForm
{
    readonly ComboBox ratesPool = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly NumericUpDown ratesInterval = new() { Minimum = 1, Maximum = 60, Value = 5, Width = 60 };
    readonly CheckBox ratesPause = new() { Text = "Pause", AutoSize = true, Padding = new Padding(8, 4, 0, 0) };
    readonly RateChart throughputChart = new("MB/s", v => (v / 1048576).ToString("0.#")) { Dock = DockStyle.Fill };
    readonly RateChart iopsChart = new("IOPS", v => v.ToString("0")) { Dock = DockStyle.Fill };
    readonly ListView deviceRates = NewList(("Device", 360), ("Read IOPS", 90), ("Write IOPS", 90), ("Read MB/s", 90), ("Write MB/s", 90));
    readonly Label memoryText = new() { Dock = DockStyle.Fill, Padding = new Padding(6) };
    readonly System.Windows.Forms.Timer ratesTimer = new() { Interval = 5000 };
    IostatSample? lastSample;
    bool sampling;

    TabPage RatesPage()
    {
        var page = new TabPage("Rates");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        top.Controls.Add(new Label { Text = "Pool:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        top.Controls.Add(ratesPool);
        top.Controls.Add(new Label { Text = "Every", AutoSize = true, Padding = new Padding(12, 6, 0, 0) });
        top.Controls.Add(ratesInterval);
        top.Controls.Add(new Label { Text = "s", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        top.Controls.Add(ratesPause);
        ratesPool.SelectedIndexChanged += (_, _) => { if (!applyingWebAction) ResetRates(); };
        ratesInterval.ValueChanged += (_, _) => ratesTimer.Interval = (int)ratesInterval.Value * 1000;
        ratesTimer.Tick += async (_, _) => await SampleRatesAsync();
        tabs.SelectedIndexChanged += (_, _) => UpdateRatesTimer();
        ratesPause.CheckedChanged += (_, _) => UpdateRatesTimer();
        Resize += (_, _) => UpdateRatesTimer();

        var charts = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        charts.Controls.Add(throughputChart, 0, 0);
        charts.Controls.Add(iopsChart, 1, 0);
        var lower = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 640 };
        lower.Panel1.Controls.Add(new GroupBox { Text = "Devices", Dock = DockStyle.Fill, Controls = { deviceRates } });
        lower.Panel2.Controls.Add(new GroupBox { Text = "ZFS memory", Dock = DockStyle.Fill, Controls = { memoryText } });
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 260 };
        split.Panel1.Controls.Add(charts);
        split.Panel2.Controls.Add(lower);
        page.Controls.Add(split);
        page.Controls.Add(top);
        return page;
    }

    void UpdateRatesTimer()
    {
        bool run = tabs.SelectedTab?.Text == "Rates" && WindowState != FormWindowState.Minimized && !ratesPause.Checked && ScreenshotDirectory is null;
        if (run && !ratesTimer.Enabled)
        {
            ratesTimer.Start();
            _ = SampleRatesAsync();
        }
        else if (!run)
        {
            ratesTimer.Stop();
        }
    }

    void ResetRates()
    {
        lastSample = null;
        throughputChart.Clear();
        iopsChart.Clear();
        deviceRates.Items.Clear();
    }

    async Task SampleRatesAsync()
    {
        if (sampling || !brokerAvailable || ratesPool.SelectedItem is not string pool)
            return;
        sampling = true;
        try
        {
            IostatSample sample = await client.IostatAsync(pool);
            if (lastSample is { } before && before.Pool == sample.Pool)
            {
                IoRates total = sample.TotalRatesSince(before);
                throughputChart.Add(total.ReadBytesPerSecond, total.WriteBytesPerSecond);
                iopsChart.Add(total.ReadIops, total.WriteIops);
                deviceRates.BeginUpdate();
                deviceRates.Items.Clear();
                AddRow($"{pool} (pool)", total);
                foreach (string device in sample.Vdevs.Keys)
                    AddRow(device, sample.VdevRatesSince(before, device));
                deviceRates.EndUpdate();
            }
            lastSample = sample;
            ShowMemory(await client.MemoryAsync());
        }
        catch (AdminException e)
        {
            BackgroundError("pool.iostat", e);
        }
        finally
        {
            sampling = false;
        }

        void AddRow(string name, IoRates r) => deviceRates.Items.Add(new ListViewItem([name, r.ReadIops.ToString("0"), r.WriteIops.ToString("0"),
            (r.ReadBytesPerSecond / 1048576).ToString("0.0"), (r.WriteBytesPerSecond / 1048576).ToString("0.0")]));
    }

    void ShowMemory(MemoryInfo m)
    {
        if (m.Arc is not ArcInfo a)
        {
            memoryText.Text = "ARC statistics are not available: neither the kernel driver (kstat) nor the user-mode service answered.";
            return;
        }
        var lines = new List<string>
        {
            $"ARC size:  {Format.Bytes(a.Size)}   (target {Format.Bytes(a.Target)}, min {Format.Bytes(a.Min)}, max {Format.Bytes(a.Max)})",
            $"Recently used (MRU):  {Format.Bytes(a.MruSize)}     frequently used (MFU):  {Format.Bytes(a.MfuSize)}",
            $"Hit ratio:  {(a.HitRatio is double h ? $"{h:P1}" : "–")}   ({a.Hits:N0} hits, {a.Misses:N0} misses)",
            a.L2Size is > 0 ? $"L2ARC:  {Format.Bytes(a.L2Size)}, hit ratio {(a.L2HitRatio is double l ? $"{l:P1}" : "–")}" : "L2ARC:  none",
        };
        foreach (var (pool, bytes) in m.DedupTables.Where(t => t.Bytes is > 0))
            lines.Add($"Dedup table ({pool}):  {Format.Bytes(bytes)}");
        if (m.UserModeServicePrivateBytes is long svc)
            lines.Add($"ZfsWinUserMode process:  {Format.Bytes(svc)} private (the ARC lives in this process in the user-mode edition)");
        memoryText.Text = string.Join(Environment.NewLine, lines);
    }
}

/// <summary>Two series (read, write) over the last 120 samples, drawn in GDI+.</summary>
sealed class RateChart : Control
{
    readonly string title;
    readonly Func<double, string> label;
    readonly List<(double Read, double Write)> points = [];
    static readonly Color ReadColor = Color.FromArgb(47, 111, 208), WriteColor = Color.FromArgb(208, 138, 47);

    public RateChart(string title, Func<double, string> label)
    {
        this.title = title;
        this.label = label;
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = SystemColors.Window;
    }

    public string Title => title;
    public IReadOnlyList<(double Read, double Write)> Readings => points;
    public string FormatValue(double value) => label(value);

    public void Add(double read, double write)
    {
        points.Add((read, write));
        if (points.Count > Samples)
            points.RemoveAt(0);
        Invalidate();
    }

    public void Clear()
    {
        points.Clear();
        Invalidate();
    }

    // 10 minutes at the default 5 s.
    const int Samples = 120;

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // The title line, then half a line so the top axis label clears it.
        int top = 4 + Font.Height * 2;
        var area = new Rectangle(48, top, Math.Max(10, Width - 60), Math.Max(10, Height - top - Font.Height));
        double max = Math.Max(1, points.Count == 0 ? 1 : points.Max(p => Math.Max(p.Read, p.Write))) * 1.1;
        using var grid = new Pen(SystemColors.ControlLight);
        using var text = new SolidBrush(SystemColors.GrayText);
        var last = points.Count > 0 ? points[^1] : (0, 0);
        g.DrawString($"{title}   read {label(last.Item1)}   write {label(last.Item2)}   (last {Samples} samples)", Font, SystemBrushes.WindowText, 4, 4);
        for (int i = 0; i <= 4; i++)
        {
            int y = area.Bottom - i * area.Height / 4;
            g.DrawLine(grid, area.Left, y, area.Right, y);
            g.DrawString(label(max * i / 4), Font, text, 2, y - Font.Height / 2);
        }
        Series(p => p.Read, ReadColor);
        Series(p => p.Write, WriteColor);

        void Series(Func<(double Read, double Write), double> value, Color color)
        {
            if (points.Count < 2)
                return;
            float step = (float)area.Width / (Samples - 1);
            int offset = Samples - points.Count;
            PointF[] line = points.Select((p, i) => new PointF(area.Left + (offset + i) * step, (float)(area.Bottom - value(p) / max * area.Height))).ToArray();
            using var pen = new Pen(color, 2f);
            g.DrawLines(pen, line);
        }
    }
}
