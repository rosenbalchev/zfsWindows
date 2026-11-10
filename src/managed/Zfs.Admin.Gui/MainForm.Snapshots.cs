using System.Diagnostics;
using Zfs.Admin;

namespace Zfs.Admin.Gui;

/// <summary>
/// The Snapshots tab: create, roll back, destroy, and mount a snapshot
/// read-only for a while (a temporary clone the broker removes again).
/// Single files are restored by copying them out of a temporary mount.
/// </summary>
sealed partial class MainForm
{
    readonly ComboBox snapshotDataset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly ListView snapshots = NewList(("Snapshot", 260), ("Created", 150), ("Used", 90), ("Referenced", 90), ("Mounted at", 160));
    readonly CheckBox keepMounted = new() { Text = "Keep temporary mounts when this window closes", AutoSize = false, Size = new Size(290, 40) };
    // Mounted from this window: unmounted when it closes unless keepMounted.
    readonly HashSet<string> mountedHere = new(StringComparer.Ordinal);
    IReadOnlyList<SnapshotInfo> snapshotList = [];
    IReadOnlyDictionary<string, string> temporaryMounts = new Dictionary<string, string>();
    bool closingUnmounted;

    TabPage SnapshotsPage()
    {
        var page = new TabPage("Snapshots");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        top.Controls.Add(new Label { Text = "Dataset:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        top.Controls.Add(snapshotDataset);
        snapshotDataset.SelectedIndexChanged += async (_, _) => await ShowSnapshotsAsync();

        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, Width = 320, Padding = new Padding(8) };
        void Action(string text, Func<Task> handler)
        {
            var b = new Button { Text = text, Width = 290, Height = 34 };
            b.Click += async (_, _) => await handler();
            actions.Controls.Add(b);
        }
        Action("Create snapshot…", CreateSnapshotAsync);
        Action("Mount temporarily (read-only)…", MountSnapshotAsync);
        Action("Open in Explorer", () => { OpenMounted(); return Task.CompletedTask; });
        Action("Unmount", UnmountSnapshotAsync);
        Action("Restore dataset (roll back)…", RollbackAsync);
        Action("Destroy snapshot…", DestroySnapshotAsync);
        actions.Controls.Add(keepMounted);
        actions.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(290, 0), Padding = new Padding(0, 8, 0, 0),
            Text = "To restore single files, mount the snapshot temporarily and copy them back in Explorer.  " +
                   "The mount is a read-only clone: it uses no space and is removed when you unmount it, when this window closes, " +
                   "or the next time the broker starts.  Rolling back restores the whole dataset and destroys every newer snapshot.",
        });
        page.Controls.Add(snapshots);
        page.Controls.Add(actions);
        page.Controls.Add(top);
        snapshots.DoubleClick += (_, _) => OpenMounted();
        return page;
    }

    async Task ShowSnapshotDatasetsAsync()
    {
        object? keep = snapshotDataset.SelectedItem;
        var names = (await client.DatasetsAsync()).Where(d => !IsTemporary(d.Name)).Select(d => (object)d.Name).ToArray();
        snapshotDataset.Items.Clear();
        snapshotDataset.Items.AddRange(names);
        if (snapshotDataset.Items.Count > 0)
            snapshotDataset.SelectedItem = keep is not null && snapshotDataset.Items.Contains(keep) ? keep : snapshotDataset.Items[0];
        await ShowSnapshotsAsync();
    }

    async Task ShowSnapshotsAsync()
    {
        if (snapshotDataset.SelectedItem is not string dataset)
            return;
        string? selected = SelectedSnapshot()?.Name;
        snapshotList = await client.SnapshotsAsync(dataset);
        temporaryMounts = await client.TemporaryMountsAsync(snapshotList);
        snapshots.BeginUpdate();
        snapshots.Items.Clear();
        foreach (SnapshotInfo s in snapshotList.Reverse())
        {
            temporaryMounts.TryGetValue(s.Name, out string? at);
            var item = new ListViewItem(["@" + s.Snapshot, s.Creation?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "", Format.Bytes(s.Used),
                Format.Bytes(s.Referenced), at ?? ""]) { Tag = s };
            if (at is not null)
                item.Font = new Font(snapshots.Font, FontStyle.Bold);
            snapshots.Items.Add(item);
            item.Selected = s.Name == selected;
        }
        snapshots.EndUpdate();
    }

    SnapshotInfo? SelectedSnapshot() => snapshots.SelectedItems.Count > 0 ? snapshots.SelectedItems[0].Tag as SnapshotInfo : null;

    SnapshotInfo? RequireSnapshot()
    {
        SnapshotInfo? s = SelectedSnapshot();
        if (s is null)
            Info("Select a snapshot.");
        return s;
    }

    async Task CreateSnapshotAsync()
    {
        if (snapshotDataset.SelectedItem is not string dataset)
            return;
        using var dialog = new SnapshotDialog(dataset);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await RunAsync($"Create {dataset}@{dialog.SnapshotName}", () => client.CreateSnapshotAsync($"{dataset}@{dialog.SnapshotName}", dialog.Recursive));
    }

    async Task MountSnapshotAsync()
    {
        if (RequireSnapshot() is not SnapshotInfo s)
            return;
        if (temporaryMounts.ContainsKey(s.Name))
        {
            OpenMounted();
            return;
        }
        using var dialog = new MountPointDialog(s.Name);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        TemporaryMount? mount = null;
        await RunAsync($"Mount {s.Name}", async () => mount = await client.MountSnapshotAsync(s.Name, dialog.MountPoint));
        if (mount is not null)
        {
            mountedHere.Add(s.Name);
            await ShowSnapshotsAsync();
            OpenFolder(mount.MountPoint);
        }
    }

    void OpenMounted()
    {
        if (SelectedSnapshot() is SnapshotInfo s && temporaryMounts.TryGetValue(s.Name, out string? at))
            OpenFolder(at);
        else
            Info("Mount the snapshot first (Mount temporarily).");
    }

    static void OpenFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path } });

    async Task UnmountSnapshotAsync()
    {
        if (RequireSnapshot() is not SnapshotInfo s)
            return;
        if (!temporaryMounts.ContainsKey(s.Name))
        {
            Info($"{s.Name} is not mounted.");
            return;
        }
        await RunAsync($"Unmount {s.Name}", () => client.UnmountSnapshotAsync(s.Name));
        mountedHere.Remove(s.Name);
        await ShowSnapshotsAsync();
    }

    async Task RollbackAsync()
    {
        if (RequireSnapshot() is not SnapshotInfo s)
            return;
        var newer = snapshotList.Where(x => x.Creation > s.Creation).Select(x => x.Name).ToList();
        if (newer.Any(temporaryMounts.ContainsKey))
        {
            Info($"Unmount the newer snapshots first: {string.Join(", ", newer.Where(temporaryMounts.ContainsKey))}.");
            return;
        }
        using var dialog = new RollbackDialog(s, newer);
        if (dialog.ShowDialog(this) == DialogResult.OK)
            await RunAsync($"Roll back {s.Dataset} to {s.Name}", () => client.RollbackAsync(s.Name, destroyNewer: newer.Count > 0, confirmed: true));
        await ShowSnapshotsAsync();
    }

    async Task DestroySnapshotAsync()
    {
        if (RequireSnapshot() is not SnapshotInfo s)
            return;
        if (temporaryMounts.ContainsKey(s.Name))
        {
            Info($"{s.Name} is mounted temporarily; unmount it first.");
            return;
        }
        if (Confirm($"Destroy the snapshot {s.Name}?\r\n\r\nIts {Format.Bytes(s.Used)} are freed; it cannot be restored from afterwards."))
            await RunAsync($"Destroy {s.Name}", () => client.DestroySnapshotAsync(s.Name, confirmed: true));
        await ShowSnapshotsAsync();
    }

    /// <summary>Unmounts this window's temporary mounts before it closes, unless asked to keep them.</summary>
    async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (closingUnmounted || keepMounted.Checked || mountedHere.Count == 0 || !brokerAvailable)
            return;
        e.Cancel = true;
        closingUnmounted = true;
        Enabled = false;
        statusText.Text = "Unmounting temporary snapshot mounts…";
        foreach (string snapshot in mountedHere.ToList())
        {
            try
            {
                using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await client.UnmountSnapshotAsync(snapshot, limit.Token);
            }
            catch (AdminException)
            {
                // The broker removes it at its next start.
            }
        }
        Close();
    }
}

/// <summary>Names a new snapshot: gui-yyyyMMdd-HHmmss by default.</summary>
sealed class SnapshotDialog : Form
{
    readonly TextBox name = new() { Width = 300 };
    readonly CheckBox recursive = new() { Text = "Include child datasets (-r)", AutoSize = true };

    public string SnapshotName => name.Text.Trim();
    public bool Recursive => recursive.Checked;

    public SnapshotDialog(string dataset)
    {
        Text = $"Create snapshot of {dataset}";
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        Font = SystemFonts.MessageBoxFont;
        name.Text = $"gui-{DateTime.Now:yyyyMMdd-HHmmss}";
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(12) };
        grid.Controls.Add(new Label { Text = $"{dataset}@", AutoSize = true, Anchor = AnchorStyles.Left });
        grid.Controls.Add(name);
        grid.Controls.Add(new Label());
        grid.Controls.Add(recursive);
        var ok = new Button { Text = "Create", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            try
            {
                Validation.SnapshotName($"{dataset}@{SnapshotName}");
                DialogResult = DialogResult.OK;
            }
            catch (AdminException e)
            {
                MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancel, ok]);
        grid.Controls.Add(new Label());
        grid.Controls.Add(buttons);
        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

/// <summary>Where a snapshot is mounted temporarily: a free drive letter or an empty folder.</summary>
sealed class MountPointDialog : Form
{
    readonly ComboBox letter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80 };
    readonly RadioButton useLetter = new() { Text = "Drive letter:", AutoSize = true, Checked = true };
    readonly RadioButton useFolder = new() { Text = "Empty folder:", AutoSize = true };
    readonly TextBox folder = new() { Width = 300 };

    public string? MountPoint => useLetter.Checked ? letter.SelectedItem as string : folder.Text.Trim();

    public MountPointDialog(string snapshot)
    {
        Text = $"Mount {snapshot} read-only";
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        Font = SystemFonts.MessageBoxFont;
        var used = Environment.GetLogicalDrives().Select(d => char.ToUpperInvariant(d[0])).ToHashSet();
        for (char c = 'Z'; c >= 'G'; c--)
            if (!used.Contains(c))
                letter.Items.Add($"{c}:");
        if (letter.Items.Count > 0)
            letter.SelectedIndex = 0;
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var f = new FolderBrowserDialog { Description = "An empty folder for the snapshot" };
            if (f.ShowDialog(this) == DialogResult.OK)
            {
                folder.Text = f.SelectedPath;
                useFolder.Checked = true;
            }
        };
        var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Padding = new Padding(12) };
        grid.Controls.Add(useLetter);
        grid.Controls.Add(letter);
        grid.Controls.Add(new Label());
        grid.Controls.Add(useFolder);
        grid.Controls.Add(folder);
        grid.Controls.Add(browse);
        var ok = new Button { Text = "Mount", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancel, ok]);
        grid.Controls.Add(new Label());
        grid.Controls.Add(buttons);
        grid.SetColumnSpan(buttons, 2);
        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

/// <summary>
/// The rollback confirmation: it names the dataset and every newer snapshot
/// that goes, and stays disabled until the user ticks that they understand.
/// </summary>
sealed class RollbackDialog : Form
{
    public RollbackDialog(SnapshotInfo target, IReadOnlyList<string> newer)
    {
        Text = $"Restore {target.Dataset}";
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        Font = SystemFonts.MessageBoxFont;
        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Padding = new Padding(14) };
        layout.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(520, 0),
            Text = $"{target.Dataset} returns to its state of {target.Creation?.ToLocalTime():yyyy-MM-dd HH:mm:ss} (@{target.Snapshot}).  " +
                   "Every change made since then is lost.  The dataset is unmounted for a moment and mounted again at the same place.",
        });
        if (newer.Count > 0)
        {
            layout.Controls.Add(new Label { AutoSize = true, Padding = new Padding(0, 8, 0, 0), Font = new Font(Font, FontStyle.Bold),
                Text = $"These {newer.Count} newer snapshot(s) are destroyed as well:" });
            var list = new ListBox { Width = 520, Height = Math.Min(160, 18 * newer.Count + 8) };
            list.Items.AddRange(newer.Select(n => (object)n).ToArray());
            layout.Controls.Add(list);
        }
        var understood = new CheckBox { AutoSize = true, Padding = new Padding(0, 8, 0, 0),
            Text = newer.Count > 0 ? "I understand that the changes and the newer snapshots are lost." : "I understand that the changes since then are lost." };
        layout.Controls.Add(understood);
        var ok = new Button { Text = "Restore", AutoSize = true, DialogResult = DialogResult.OK, Enabled = false, BackColor = Color.Firebrick, ForeColor = Color.White };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        understood.CheckedChanged += (_, _) => ok.Enabled = understood.Checked;
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancel, ok]);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        CancelButton = cancel;
    }
}
