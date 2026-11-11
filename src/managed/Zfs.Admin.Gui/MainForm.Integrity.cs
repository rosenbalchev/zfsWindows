using System.Diagnostics;
using Zfs.Admin;

namespace Zfs.Admin.Gui;

/// <summary>The Integrity tab: files with blocks that failed their checksum, per-device counters, scrub.</summary>
sealed partial class MainForm
{
    readonly ComboBox integrityPool = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    readonly Label integrityExplanation = new() { AutoSize = true, MaximumSize = new Size(1000, 0), Padding = new Padding(4) };
    readonly Label integrityScan = new() { AutoSize = true, Padding = new Padding(4, 8, 4, 4) };
    readonly ProgressBar integrityProgress = new() { Width = 220, Height = 16, Margin = new Padding(4, 8, 4, 4) };
    readonly ListView damaged = NewList(("File or object", 520), ("Dataset", 160), ("Snapshot", 120), ("Kind", 80));
    readonly ListView deviceErrors = NewList(("Device", 360), ("Group", 80), ("State", 90), ("READ", 70), ("WRITE", 70), ("CKSUM", 70));
    PoolErrorsInfo? lastErrors;

    TabPage IntegrityPage()
    {
        var page = new TabPage("Integrity");
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
        top.Controls.Add(new Label { Text = "Pool:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
        top.Controls.Add(integrityPool);
        integrityPool.SelectedIndexChanged += async (_, _) => { if (!applyingWebAction) await ShowIntegrityAsync(); };
        var scrub = new Button { Text = "Start scrub", AutoSize = true };
        scrub.Click += async (_, _) =>
        {
            if (integrityPool.SelectedItem is string pool)
                await RunAsync($"Start scrub on {pool}", () => client.ScrubAsync(pool, "start"));
        };
        var clear = new Button { Text = "Clear error counters…", AutoSize = true };
        clear.Click += async (_, _) =>
        {
            if (integrityPool.SelectedItem is string pool && Confirm($"Reset the READ, WRITE and CKSUM counters of every device in {pool} (zpool clear)?\r\n\r\n" +
                    "The list of damaged files stays until a scrub no longer finds them.  Clear the counters after you have replaced or fixed what caused them."))
                await RunAsync($"Clear errors on {pool}", () => client.ClearErrorsAsync(pool));
        };
        top.Controls.AddRange([scrub, clear, integrityProgress, integrityScan]);

        var fileButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
        var open = new Button { Text = "Open containing folder", AutoSize = true };
        open.Click += (_, _) => OpenContainingFolder();
        var copy = new Button { Text = "Copy list", AutoSize = true };
        copy.Click += (_, _) =>
        {
            if (lastErrors is { Files.Count: > 0 } e)
                Clipboard.SetText(string.Join(Environment.NewLine, e.Files.Select(f => f.Display)));
        };
        fileButtons.Controls.AddRange([open, copy]);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 300 };
        var filesBox = new GroupBox { Text = "Damaged files (permanent errors)", Dock = DockStyle.Fill };
        filesBox.Controls.Add(damaged);
        filesBox.Controls.Add(fileButtons);
        split.Panel1.Controls.Add(filesBox);
        var devicesBox = new GroupBox { Text = "Errors per device", Dock = DockStyle.Fill };
        devicesBox.Controls.Add(deviceErrors);
        split.Panel2.Controls.Add(devicesBox);
        page.Controls.Add(split);
        page.Controls.Add(integrityExplanation);
        page.Controls.Add(top);
        integrityExplanation.Dock = DockStyle.Top;
        return page;
    }

    async Task ShowIntegrityAsync()
    {
        if (integrityPool.SelectedItem is not string pool)
            return;
        PoolErrorsInfo e = await client.PoolErrorsAsync(pool);
        lastErrors = e;
        string checksum = (await client.DatasetPropertiesAsync(pool, ["checksum"])).FirstOrDefault(p => p.Name == "checksum")?.Value ?? "on";
        integrityExplanation.Text =
            $"ZFS checks every block it reads against its checksum ({(checksum == "on" ? "fletcher4" : checksum)} on {pool}; the dataset's checksum property), not a CRC.  " +
            "With a second copy (mirror, raidz or copies=2) a bad block is repaired from it; otherwise the file is listed here.  " +
            "The list covers the blocks read since the last clear; a scrub reads every block." +
            (e.LogError is { Length: > 0 } le ? $"  The error log could not be read: {le}." : "");

        damaged.BeginUpdate();
        damaged.Items.Clear();
        foreach (DamagedFile f in e.Files)
            damaged.Items.Add(new ListViewItem([f.Kind == "file" ? f.Path ?? f.Text : f.Display, f.Dataset ?? "", f.Snapshot ?? "",
                f.Kind == "metadata" ? "pool metadata" : f.Kind]) { Tag = f, ForeColor = Color.Firebrick });
        if (e.Files.Count == 0)
            damaged.Items.Add(new ListViewItem([e.ErrorCount > 0 ? $"{e.ErrorCount} errors are recorded; their files are listed after the next scrub." : "No known data errors.", "", "", ""]));
        damaged.EndUpdate();

        deviceErrors.BeginUpdate();
        deviceErrors.Items.Clear();
        foreach (DeviceErrors d in e.Devices)
        {
            var item = new ListViewItem([d.Path ?? d.Name, d.Group, d.State ?? "", d.ReadErrors.ToString(), d.WriteErrors.ToString(), d.ChecksumErrors.ToString()]);
            if (d.ReadErrors + d.WriteErrors + d.ChecksumErrors > 0 || d.State is not ("ONLINE" or null))
                item.ForeColor = Color.Firebrick;
            deviceErrors.Items.Add(item);
        }
        deviceErrors.EndUpdate();

        ScanInfo? scan = e.Scan;
        bool running = scan is { State: "SCANNING" };
        integrityProgress.Visible = running;
        if (running && scan!.ToExamine is > 0)
            integrityProgress.Value = (int)Math.Clamp(100 * (scan.Examined ?? 0) / scan.ToExamine.Value, 0, 100);
        integrityScan.Text = scan is null ? "No scrub has run." :
            running ? $"{scan.Function?.ToLowerInvariant()} {(scan.Paused ? "paused" : "running")}: {Format.Bytes(scan.Examined)} of {Format.Bytes(scan.ToExamine)} examined" :
            $"Last {scan.Function?.ToLowerInvariant()}: {scan.State?.ToLowerInvariant()}, {scan.Errors ?? 0} errors.";
    }

    void OpenContainingFolder()
    {
        if (damaged.SelectedItems.Count == 0 || damaged.SelectedItems[0].Tag is not DamagedFile f)
        {
            Info("Select a damaged file.");
            return;
        }
        // A path under a mounted dataset; snapshot files and unmounted datasets have none.
        string? path = f.Kind == "file" && f.Dataset is null ? f.Path : null;
        if (path is null || !Path.IsPathFullyQualified(path))
        {
            Info(f.Snapshot is not null
                ? $"The damaged block is only in the snapshot {f.Dataset}@{f.Snapshot}.  Mount the snapshot on the Snapshots tab to look at it, or destroy the snapshot to free the block."
                : $"{f.Display} has no path on a mounted dataset.");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path } });
    }
}
