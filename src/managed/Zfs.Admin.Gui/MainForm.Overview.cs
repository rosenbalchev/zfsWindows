using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Zfs.Admin;

namespace Zfs.Admin.Gui;

/// <summary>
/// The tabs share WebView2 when available. Without its runtime (Windows LTSC
/// and Server), Overview opens from %LOCALAPPDATA%\OpenZFS\dashboard.html;
/// the WinForms tab controls remain usable.
/// </summary>
sealed partial class MainForm
{
    static readonly TimeSpan AutoCountInterval = TimeSpan.FromHours(1);
    static readonly TimeSpan ObjectCountInterval = TimeSpan.FromMinutes(1);

    readonly Panel overviewHost = new() { Dock = DockStyle.Fill };
    readonly Label overviewFallback = new() { Dock = DockStyle.Fill, Padding = new Padding(12), Visible = false };
    readonly ToolStripLabel countState = new() { Alignment = ToolStripItemAlignment.Right };
    readonly ToolStripButton countNow = new("Count files now") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    readonly ToolStripButton countCancel = new("Cancel count") { DisplayStyle = ToolStripItemDisplayStyle.Text, Enabled = false };
    readonly ToolStripButton openInBrowser = new("Open in browser") { DisplayStyle = ToolStripItemDisplayStyle.Text, Visible = false };
    readonly Dictionary<string, DatasetCountInfo> counts = new(StringComparer.Ordinal);
    readonly Dictionary<string, DateTimeOffset> objectsCountedAt = new(StringComparer.Ordinal);
    WebView2? web;
    string lastHtml = "";
    DashboardData? lastDashboard;
    CancellationTokenSource? counting;
    DateTimeOffset lastWalk = DateTimeOffset.MinValue;

    static string DashboardFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenZFS", "dashboard.html");

    TabPage OverviewPage()
    {
        var page = new TabPage("Overview");
        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top };
        countNow.Click += async (_, _) => await CountFilesAsync();
        countCancel.Click += (_, _) => counting?.Cancel();
        openInBrowser.Click += (_, _) => OpenDashboardInBrowser();
        bar.Items.AddRange([countNow, countCancel, openInBrowser, countState]);
        overviewFallback.Text = "The WebView2 runtime is not installed (Windows LTSC and Server editions do not include it). " +
            "The Overview dashboard opens in your browser instead: Open in browser. The other tabs retain their native controls.";
        overviewHost.Controls.Add(overviewFallback);
        page.Controls.Add(overviewHost);
        page.Controls.Add(bar);
        return page;
    }

    async Task InitializeWebViewAsync()
    {
        try
        {
            CoreWebView2Environment.GetAvailableBrowserVersionString();
            // The GUI runs elevated from Program Files: keep WebView2's data in the user's profile.
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenZFS", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, data);
            web = new WebView2 { Dock = DockStyle.Fill };
            await web.EnsureCoreWebView2Async(environment);
            web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            web.CoreWebView2.Settings.IsScriptEnabled = true;
            web.CoreWebView2.Settings.IsStatusBarEnabled = false;
            web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            // Only our local data pages are allowed; Web UI actions use a validated message allowlist.
            web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!e.Uri.StartsWith("data:", StringComparison.Ordinal) && !e.Uri.StartsWith("about:", StringComparison.Ordinal))
                    e.Cancel = true;
            };
            overviewFallback.Visible = false;
            MoveWebViewToSelectedTab();
            RenderSelectedWebPage();
        }
        catch (Exception e) when (e is WebView2RuntimeNotFoundException or COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            web?.Dispose();
            web = null;
            overviewHost.Visible = true;
            overviewFallback.Visible = true;
            openInBrowser.Visible = true;
        }
    }

    async Task ShowOverviewAsync()
    {
        DashboardData data = await BuildDashboardAsync(refreshSeconds: web is null ? 5 : null);
        lastDashboard = data;
        string html = DashboardHtml.Render(data);
        // Unchanged figures: no reload, so the page keeps its scroll position.
        string comparable = Regex.Replace(html, "Updated [^<]*", "");
        if (comparable == lastHtml)
            return;
        lastHtml = comparable;
        if (web is null)
            WriteDashboardFile(html);
        else if (tabs.SelectedTab?.Text == "Overview")
            RenderSelectedWebPage();
        if (DateTimeOffset.UtcNow - lastWalk > AutoCountInterval && counting is null)
            _ = CountFilesAsync();
    }

    async Task<DashboardData> BuildDashboardAsync(int? refreshSeconds)
    {
        var pools = new List<DashboardPool>();
        var allDatasets = (await client.DatasetsAsync()).Where(d => !IsTemporary(d.Name)).ToList();
        foreach (PoolInfo p in await client.PoolsAsync())
        {
            PoolStatusInfo? status = await client.StatusAsync(p.Name);
            long? cloneSaved = null;
            try
            {
                cloneSaved = (await client.PoolPropertiesAsync(p.Name, ["bclonesaved"])).FirstOrDefault(x => x.Name == "bclonesaved")?.Value is string v && long.TryParse(v, out long l) ? l : null;
            }
            catch (AdminException)
            {
            }
            var datasets = allDatasets.Where(d => d.Name == p.Name || d.Name.StartsWith(p.Name + "/", StringComparison.Ordinal)).ToList();
            foreach (DatasetInfo d in datasets.Where(d => d.Mounted))
                await RefreshObjectCountAsync(d.Name);
            RedundancyInfo? redundancy = status is null ? null : Redundancy.Copies(status, datasets.FirstOrDefault(d => d.Name == p.Name)?.Copies ?? 1);
            pools.Add(new DashboardPool(p, status, status?.ErrorCount ?? 0, cloneSaved, datasets,
                counts.Where(c => datasets.Any(d => d.Name == c.Key)).ToDictionary(c => c.Key, c => c.Value), redundancy));
        }
        MemoryInfo? memory = null;
        try
        {
            memory = await client.MemoryAsync();
        }
        catch (AdminException)
        {
        }
        return new DashboardData(edition, DateTimeOffset.UtcNow, pools, memory, Dark: IsDarkMode(), RefreshSeconds: refreshSeconds);
    }

    /// <summary>The instant object total (zfs userspace), at most once a minute per dataset.</summary>
    async Task RefreshObjectCountAsync(string dataset)
    {
        if (objectsCountedAt.TryGetValue(dataset, out DateTimeOffset at) && DateTimeOffset.UtcNow - at < ObjectCountInterval)
            return;
        objectsCountedAt[dataset] = DateTimeOffset.UtcNow;
        try
        {
            DatasetCountInfo objects = await client.CountAsync(dataset, objectsOnly: true);
            counts[dataset] = counts.TryGetValue(dataset, out DatasetCountInfo? walked) && walked.Files is not null
                ? walked with { Objects = objects.Objects }
                : objects;
        }
        catch (AdminException)
        {
        }
    }

    /// <summary>Walks every mounted dataset in the background; Cancel count stops it.</summary>
    async Task CountFilesAsync()
    {
        if (counting is not null || !brokerAvailable)
            return;
        counting = new CancellationTokenSource();
        lastWalk = DateTimeOffset.UtcNow;
        countNow.Enabled = false;
        countCancel.Enabled = true;
        try
        {
            var mounted = (await client.DatasetsAsync()).Where(d => d.Mounted && !IsTemporary(d.Name)).ToList();
            foreach (var (d, i) in mounted.Select((d, i) => (d, i)))
            {
                countState.Text = $"Counting {d.Name} ({i + 1} of {mounted.Count})…";
                counts[d.Name] = await client.CountAsync(d.Name, ct: counting.Token);
            }
            countState.Text = $"Files and folders counted {DateTime.Now:HH:mm}.";
        }
        catch (AdminException e) when (e.Code == ErrorCode.Cancelled)
        {
            countState.Text = "Count cancelled.";
        }
        catch (AdminException e)
        {
            BackgroundError("dataset.count", e);
            countState.Text = "Count failed.";
        }
        finally
        {
            counting.Dispose();
            counting = null;
            countNow.Enabled = true;
            countCancel.Enabled = false;
        }
        lastHtml = "";
        await ShowOverviewAsync();
    }

    void WriteDashboardFile(string html)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DashboardFile)!);
            File.WriteAllText(DashboardFile, html);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            statusText.Text = $"Cannot write {DashboardFile}: {e.Message}";
        }
    }

    void OpenDashboardInBrowser()
    {
        if (!File.Exists(DashboardFile))
            WriteDashboardFile(lastDashboard is null ? "" : DashboardHtml.Render(lastDashboard with { RefreshSeconds = 5 }));
        // Through Explorer, so the browser does not run elevated like this window.
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { DashboardFile } });
    }

    async Task SaveReportAsync()
    {
        DashboardData data;
        try
        {
            data = await BuildDashboardAsync(refreshSeconds: null) with { Dark = false };
        }
        catch (AdminException e)
        {
            Error("Save report", e);
            return;
        }
        using var dialog = new SaveFileDialog
        {
            Title = "Save report", Filter = "Web page (*.html)|*.html", FileName = $"openzfs-report-{DateTime.Now:yyyyMMdd-HHmm}.html",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
        File.WriteAllText(dialog.FileName, DashboardHtml.Render(data));
        statusText.Text = $"Saved {dialog.FileName}.";
    }

    static bool IsTemporary(string dataset) => dataset.Contains($"/{Operations.TemporaryParent}", StringComparison.Ordinal);

    /// <summary>Windows' app colour setting (Settings → Personalization → Colors).</summary>
    static bool IsDarkMode()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }
}
