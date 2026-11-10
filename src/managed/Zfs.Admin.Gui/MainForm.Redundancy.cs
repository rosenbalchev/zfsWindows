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

        // WMI is slow: ask Windows at most once a minute.
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
        healthList.BeginUpdate();
        healthList.Items.Clear();
        foreach (VdevInfo leaf in Leaves(status))
        {
            string path = leaf.Path ?? leaf.Name;
            string errors = $"{leaf.ReadErrors}/{leaf.WriteErrors}/{leaf.ChecksumErrors}";
            string[] windows;
            Match disk = Regex.Match(path, @"PhysicalDrive(\d+)", RegexOptions.IgnoreCase);
            if (disk.Success && driveHealth.TryGetValue(int.Parse(disk.Groups[1].Value), out DriveHealth? h))
            {
                windows = [$"{h.Health}{(h.Status is { Length: > 0 } && h.Status != "OK" ? $" ({h.Status})" : "")}", h.Media,
                    h.TemperatureC is int t ? $"{t} °C" : "–", h.WearPercent is int w ? $"{w}%" : "–",
                    h.PowerOnHours?.ToString("N0") ?? "–", h.ReadErrorsUncorrected?.ToString("N0") ?? "–"];
                path = $"{path}  ({h.Name})";
            }
            else if (HostVolume(path) is DriveInfo volume)
            {
                windows = [volume.IsReady ? "image file" : "volume not ready", "file",
                    "–", "–", "–", volume.IsReady ? $"host {volume.Name} {Format.Bytes(volume.AvailableFreeSpace)} free" : "–"];
            }
            else
            {
                windows = ["unknown", "–", "–", "–", "–", "–"];
            }
            var item = new ListViewItem([path, leaf.State ?? "", errors, .. windows]);
            if (leaf.State is not ("ONLINE" or null) || leaf.ReadErrors + leaf.WriteErrors + leaf.ChecksumErrors > 0 || windows[0] is "Warning" or "Unhealthy")
                item.ForeColor = Color.Firebrick;
            healthList.Items.Add(item);
        }
        healthList.EndUpdate();

        static IEnumerable<VdevInfo> Leaves(PoolStatusInfo s) =>
            new[] { s.Vdevs, s.Logs, s.Caches, s.Spares, s.Special, s.Dedup }.SelectMany(g => g).SelectMany(Flatten);

        static IEnumerable<VdevInfo> Flatten(VdevInfo v) => v.IsLeaf ? [v] : v.Children.SelectMany(Flatten);
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

    /// <summary>--screenshots: every tab as a PNG (WebView2's own capture for the Overview).</summary>
    async Task TakeScreenshotsAsync(string directory)
    {
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
                // DrawToBitmap cannot see WebView2's content: its own capture goes on top.
                if (page.Text == "Overview" && web?.CoreWebView2 is not null)
                {
                    using var stream = new MemoryStream();
                    await web.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, stream);
                    await File.WriteAllBytesAsync(Path.ChangeExtension(file, ".page.png"), stream.ToArray());
                    stream.Position = 0;
                    using var content = Image.FromStream(stream);
                    Point at = PointToClient(web.PointToScreen(Point.Empty));
                    // Client to window coordinates: DrawToBitmap draws the frame too.
                    Point origin = PointToScreen(Point.Empty);
                    at.Offset(origin.X - Left, origin.Y - Top);
                    using var g = Graphics.FromImage(bitmap);
                    g.DrawImage(content, new Rectangle(at, web.Size));
                }
                bitmap.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        finally
        {
            Close();
        }
    }
}
