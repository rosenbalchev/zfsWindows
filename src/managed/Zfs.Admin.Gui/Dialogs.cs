using Zfs.Admin;

namespace Zfs.Admin.Gui;

/// <summary>
/// Picks devices for a mirror, cache or log: image files, disks, partitions,
/// or unallocated ranges. Windows boot and system disks are offered only by
/// their unallocated ranges.
/// </summary>
sealed class DeviceDialog : Form
{
    readonly CheckedListBox disks = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    readonly ListBox files = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly CheckBox mirror = new() { Text = "Mirror the write log across the selected devices", AutoSize = true };
    readonly int maxDevices;

    public IReadOnlyList<string> Devices { get; private set; } = [];
    public string SelectionSummary { get; private set; } = "";
    public bool Mirror => mirror.Checked;

    public DeviceDialog(string title, string explanation, int maxDevices, bool offerMirror, IReadOnlyCollection<string> inUsePaths)
    {
        this.maxDevices = maxDevices;
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(640, 520);
        MinimizeBox = MaximizeBox = false;
        Font = SystemFonts.MessageBoxFont;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        layout.Controls.Add(new Label { Text = explanation + "\r\nSelected devices and partitions are overwritten. Unallocated ranges are reserved for ZFS; do not select storage containing data.", AutoSize = true, MaximumSize = new Size(600, 0) });
        layout.Controls.Add(new Label { Text = "Disks, partitions, and unallocated ranges:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        layout.Controls.Add(disks);
        layout.Controls.Add(new Label { Text = "Image files:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
        layout.Controls.Add(files);
        var fileButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var add = new Button { Text = "Add image file…", AutoSize = true };
        var remove = new Button { Text = "Remove", AutoSize = true };
        add.Click += (_, _) => AddFile();
        remove.Click += (_, _) => { if (files.SelectedItem is not null) files.Items.Remove(files.SelectedItem); };
        fileButtons.Controls.AddRange([add, remove]);
        layout.Controls.Add(fileButtons);
        if (offerMirror)
            layout.Controls.Add(mirror);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        var ok = new Button { Text = "OK", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Accept();
        buttons.Controls.AddRange([cancel, ok]);
        layout.Controls.Add(buttons);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;

        try
        {
            foreach (DeviceChoice d in Disks.AvailableDevices(inUsePaths))
                disks.Items.Add(d);
            if (disks.Items.Count == 0)
            {
                disks.Items.Add("(no eligible devices or unallocated ranges found)");
                disks.Enabled = false;
            }
        }
        catch (Exception e) when (e is System.Management.ManagementException or UnauthorizedAccessException)
        {
            disks.Items.Add($"(device inventory unavailable: {e.Message})");
            disks.Enabled = false;
        }
    }

    void AddFile()
    {
        using var open = new OpenFileDialog { Title = "Image file (create it first, e.g. fsutil file createnew D:\\pools\\cache.img 1073741824)", CheckFileExists = true, Filter = "Image files (*.img;*.raw;*.bin)|*.img;*.raw;*.bin|All files|*.*" };
        if (open.ShowDialog(this) == DialogResult.OK && !files.Items.Contains(open.FileName))
            files.Items.Add(open.FileName);
    }

    void Accept()
    {
        List<DeviceChoice> selectedDisks = disks.CheckedItems.OfType<DeviceChoice>().ToList();
        if (selectedDisks.GroupBy(d => d.DiskNumber).Any(g => g.Count() > 1))
        {
            MessageBox.Show(this, "Select at most one disk, partition, or unallocated range from each physical disk.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var chosen = selectedDisks.Select(d => d.Device).Concat(files.Items.Cast<string>()).ToList();
        if (chosen.Count == 0)
        {
            MessageBox.Show(this, "Select at least one disk or image file.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (chosen.Count > maxDevices)
        {
            MessageBox.Show(this, $"Select at most {maxDevices} device(s).", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (mirror.Checked && chosen.Count < 2)
        {
            MessageBox.Show(this, "A mirrored log needs at least two devices.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Devices = chosen;
        SelectionSummary = string.Join("\r\n  ", selectedDisks.Select(d => d.Description).Concat(files.Items.Cast<string>()));
        DialogResult = DialogResult.OK;
    }
}

/// <summary>Adds or edits one user-mode mount (a registry key the service reads).</summary>
sealed class MountDialog : Form
{
    readonly TextBox name = new() { Width = 360 };
    readonly TextBox source = new() { Width = 360 };
    readonly TextBox pool = new() { Width = 360 };
    readonly TextBox dataset = new() { Width = 360 };
    readonly TextBox mountPoint = new() { Width = 360 };
    readonly CheckBox readOnly = new() { Text = "Read-only (nothing on the pool changes)", AutoSize = true };

    public UserModeMount? Mount { get; private set; }

    public MountDialog(UserModeMount? existing)
    {
        Text = existing is null ? "Add mount" : $"Edit mount {existing.Name}";
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        Font = SystemFonts.MessageBoxFont;
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(12) };
        void Row(string label, Control c) { grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }); grid.Controls.Add(c); }
        Row("Name:", name);
        var browse = new Button { Text = "Folder…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var f = new FolderBrowserDialog { Description = "Folder that contains the pool's image files" };
            if (f.ShowDialog(this) == DialogResult.OK)
                source.Text = f.SelectedPath;
        };
        var sourceRow = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        sourceRow.Controls.AddRange([source, browse]);
        Row("Source:", sourceRow);
        Row("Pool:", pool);
        Row("Dataset:", dataset);
        Row("Mount point:", mountPoint);
        grid.Controls.Add(new Label());
        grid.Controls.Add(readOnly);
        grid.Controls.Add(new Label());
        grid.Controls.Add(new Label { Text = "Source: the folder with the pool's image files.  Mount point: a free drive letter (Z:) or a folder path that does not exist yet.  The pool must have been exported where it was last used.", AutoSize = true, MaximumSize = new Size(380, 0) });
        var ok = new Button { Text = "OK", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Accept();
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancel, ok]);
        grid.Controls.Add(new Label());
        grid.Controls.Add(buttons);
        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;
        if (existing is not null)
        {
            name.Text = existing.Name; name.ReadOnly = true;
            source.Text = existing.Source; pool.Text = existing.Pool; dataset.Text = existing.Dataset;
            mountPoint.Text = existing.MountPoint; readOnly.Checked = existing.ReadOnly;
        }
        else
        {
            mountPoint.Text = "Z:";
        }
    }

    void Accept()
    {
        var m = new UserModeMount(name.Text.Trim(), source.Text.Trim(), pool.Text.Trim(),
            dataset.Text.Trim() is { Length: > 0 } ds ? ds : pool.Text.Trim(), mountPoint.Text.Trim(), readOnly.Checked);
        try
        {
            UserModeMounts.Validate(m);
        }
        catch (AdminException e)
        {
            MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Mount = m;
        DialogResult = DialogResult.OK;
    }
}
