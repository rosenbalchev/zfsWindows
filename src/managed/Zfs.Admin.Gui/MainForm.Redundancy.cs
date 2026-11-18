using System.Text.RegularExpressions;
using Zfs.Admin;

namespace Zfs.Admin.Gui;

/// <summary>
/// The Redundancy tab's lower half: how many copies of each dataset exist,
/// each device's ZFS state and Windows' drive health, and resilver progress.
/// </summary>
sealed partial class MainForm
{
    readonly Label resilverLabel = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4) };
    readonly ListView copiesList = NewList(("Dataset", 220), ("copies", 60), ("Copies of the data", 520));
    readonly ListView healthList = NewList(("Device", 300), ("ZFS state", 80), ("Errors r/w/c", 90), ("Windows health", 110),
        ("Media", 70), ("Temperature", 85), ("Wear", 60), ("Power-on hours", 100), ("Uncorrected reads", 115));
    IReadOnlyDictionary<int, DriveHealth>? driveHealth;
    DateTimeOffset driveHealthAt;

    Control RedundancyPanel()
    {
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        // Sized once the window is laid out: a third for the copies, the rest for the drives.
        Shown += (_, _) => split.SplitterDistance = Math.Max(split.Panel1MinSize, split.Height / 3);
        split.Panel1.Controls.Add(new GroupBox { Text = "Copies of your data", Dock = DockStyle.Fill, Controls = { copiesList } });
        split.Panel2.Controls.Add(new GroupBox { Text = "Drive health", Dock = DockStyle.Fill, Controls = { healthList } });
        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(split);
        panel.Controls.Add(resilverLabel);
        return panel;
    }

    async Task ShowRedundancyAsync(string pool, PoolStatusInfo? status)
    {
        if (status is null)
            return;
        ScanInfo? scan = status.Scan;
        resilverLabel.Visible = scan is { Function: "RESILVER", State: "SCANNING" };
        if (resilverLabel.Visible && scan!.ToExamine is > 0)
            resilverLabel.Text = $"Resilvering: {100.0 * (scan.Examined ?? 0) / scan.ToExamine.Value:0.0}% ({Format.Bytes(scan.Examined)} of {Format.Bytes(scan.ToExamine)})";

        var datasets = (await client.DatasetsAsync(pool)).Where(d => !IsTemporary(d.Name)).ToList();
        copiesList.BeginUpdate();
        copiesList.Items.Clear();
        foreach (DatasetInfo d in datasets)
        {
            RedundancyInfo r = Redundancy.Copies(status, d.Copies);
            copiesList.Items.Add(new ListViewItem([d.Name, d.Copies.ToString(), r.Text]) { ForeColor = r.Warning ? Color.DarkOrange : SystemColors.WindowText });
        }
        copiesList.EndUpdate();

        IReadOnlyDictionary<int, DriveHealth> drives = await DriveHealthAsync();
        healthList.BeginUpdate();
        healthList.Items.Clear();
        foreach (LeafHealth h in LeafHealth.Of(status, drives))
        {
            var item = new ListViewItem([h.Name is null ? h.Device : $"{h.Device}  ({h.Name})", h.State ?? "", $"{h.ReadErrors}/{h.WriteErrors}/{h.ChecksumErrors}",
                h.Windows, h.Media ?? "–", h.TemperatureC is int t ? $"{t} °C" : "–", h.WearPercent is int w ? $"{w}%" : "–",
                h.PowerOnHours?.ToString("N0") ?? "–", h.Note ?? h.ReadErrorsUncorrected?.ToString("N0") ?? "–"]);
            if (h.Problem)
                item.ForeColor = Color.Firebrick;
            healthList.Items.Add(item);
        }
        healthList.EndUpdate();
    }

    /// <summary>Windows' drive health; WMI is slow, so at most once a minute.</summary>
    async Task<IReadOnlyDictionary<int, DriveHealth>> DriveHealthAsync()
    {
        if (driveHealth is null || DateTimeOffset.UtcNow - driveHealthAt > TimeSpan.FromMinutes(1))
        {
            try
            {
                driveHealth = await Task.Run(DriveHealthInfo.List);
            }
            catch (Exception e) when (e is System.Management.ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                driveHealth = new Dictionary<int, DriveHealth>();
                statusText.Text = $"Drive health: {e.Message}";
            }
            driveHealthAt = DateTimeOffset.UtcNow;
        }
        return driveHealth;
    }

    /// <summary>--screenshots: every view as a PNG (the web app's views when WebView2 runs it).</summary>
    async Task TakeScreenshotsAsync(string directory)
    {
        if (WebActive)
        {
            await TakeWebScreenshotsAsync(directory);
            return;
        }
        Directory.CreateDirectory(directory);
        try
        {
            foreach (TabPage page in tabs.TabPages)
            {
                tabs.SelectedTab = page;
                await RefreshVisibleAsync();
                if (page.Text == "Rates")
                {
                    // Two samples make a rate; a few more make a line.
                    for (int i = 0; i < 12; i++)
                    {
                        await SampleRatesAsync();
                        await Task.Delay(1000);
                    }
                }
                await Task.Delay(1500);
                string file = Path.Combine(directory, $"{tabs.SelectedIndex + 1:00}-{page.Text.Replace(' ', '-').ToLowerInvariant()}.png");
                using var bitmap = new Bitmap(Width, Height);
                DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        finally
        {
            Close();
        }
    }
}

/// <summary>
/// One leaf device as the Redundancy tab and the Devices view show it: its
/// ZFS state and counters, and what Windows says about the drive (or, for an
/// image file, the volume holding it).
/// </summary>
sealed record LeafHealth(string Device, string? Name, string Role, string? State, long ReadErrors, long WriteErrors, long ChecksumErrors,
    string Windows, string? Media, int? TemperatureC, int? WearPercent, long? PowerOnHours, long? ReadErrorsUncorrected, string? Note)
{
    /// <summary>Not ONLINE, any error counted, or Windows warns about the drive.</summary>
    public bool Problem => State is not ("ONLINE" or null) || ReadErrors + WriteErrors + ChecksumErrors > 0 || Windows.StartsWith("Warning", StringComparison.Ordinal) || Windows.StartsWith("Unhealthy", StringComparison.Ordinal);

    public static IReadOnlyList<LeafHealth> Of(PoolStatusInfo s, IReadOnlyDictionary<int, DriveHealth> drives) =>
        new[] { ("data", s.Vdevs), ("log", s.Logs), ("cache", s.Caches), ("spare", s.Spares), ("special", s.Special), ("dedup", s.Dedup) }
            .SelectMany(g => g.Item2.SelectMany(Flatten).Select(v => Describe(v, g.Item1, drives))).ToList();

    static IEnumerable<VdevInfo> Flatten(VdevInfo v) => v.IsLeaf ? [v] : v.Children.SelectMany(Flatten);

    static LeafHealth Describe(VdevInfo leaf, string role, IReadOnlyDictionary<int, DriveHealth> drives)
    {
        string path = leaf.Path ?? leaf.Name;
        Match disk = Regex.Match(path, @"PhysicalDrive(\d+)", RegexOptions.IgnoreCase);
        if (disk.Success && drives.TryGetValue(int.Parse(disk.Groups[1].Value), out DriveHealth? h))
            return new(path, h.Name, role, leaf.State, leaf.ReadErrors, leaf.WriteErrors, leaf.ChecksumErrors,
                $"{h.Health}{(h.Status is { Length: > 0 } && h.Status != "OK" ? $" ({h.Status})" : "")}", h.Media,
                h.TemperatureC, h.WearPercent, h.PowerOnHours, h.ReadErrorsUncorrected, null);
        if (HostVolume(path) is DriveInfo volume)
            return new(path, null, role, leaf.State, leaf.ReadErrors, leaf.WriteErrors, leaf.ChecksumErrors,
                volume.IsReady ? "image file" : "volume not ready", "file", null, null, null, null,
                volume.IsReady ? $"host {volume.Name} {Format.Bytes(volume.AvailableFreeSpace)} free" : null);
        return new(path, null, role, leaf.State, leaf.ReadErrors, leaf.WriteErrors, leaf.ChecksumErrors, "unknown", null, null, null, null, null, null);
    }

    /// <summary>The volume holding an image-file vdev ("\\?\D:\pools\a.img" → D:\).</summary>
    static DriveInfo? HostVolume(string path)
    {
        string p = path.Replace('/', '\\');
        foreach (string prefix in new[] { @"\\?\", @"\\.\" })
            if (p.StartsWith(prefix, StringComparison.Ordinal))
                p = p[prefix.Length..];
        return p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' ? new DriveInfo(p[..1]) : null;
    }
}
