using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Zfs.Admin;

namespace Zfs.Admin.Gui;

/// <summary>
/// With WebView2, the whole window is one web app (web/*.html, css, js,
/// embedded): it loads once and asks for data and changes through
/// <see cref="WebCommands"/>, so refreshes never reload the page.  Without
/// WebView2 the native tabs below stay in charge.
/// </summary>
sealed partial class MainForm : IWebHost
{
    WebCommands? commands;
    readonly TaskCompletionSource webReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    bool WebActive => commands is not null && web?.CoreWebView2 is not null;

    void StartWebApp(CoreWebView2Environment environment)
    {
        CoreWebView2 core = web!.CoreWebView2;
        core.AddWebResourceRequestedFilter(WebShell.Origin + "/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            e.Response = WebShell.Resolve(e.Request.Uri) is ({ } content, { } type)
                ? environment.CreateWebResourceResponse(new MemoryStream(content), 200, "OK", WebShell.Headers(type))
                : environment.CreateWebResourceResponse(null, 404, "Not Found", "");
        };
        // Only the app's own pages; links never open a window or another site.
        core.NavigationStarting += (_, e) =>
        {
            if (!WebShell.IsOwnOrigin(e.Uri))
                e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += OnWebMessageReceived;
        commands = new WebCommands(client, this);

        web.Dock = DockStyle.Fill;
        Controls.Add(web);
        web.BringToFront();
        tabs.Visible = false;
        toolbar.Visible = false;
        statusBar.Visible = false;
        Resize += (_, _) => PostToPage(new JsonObject { ["type"] = "window", ["minimized"] = WindowState == FormWindowState.Minimized });
        core.Navigate(WebShell.StartPage);
    }

    async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (commands is null || !WebShell.IsOwnOrigin(e.Source))
            return;
        string message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch (ArgumentException)
        {
            return;
        }
        if (message == """{"type":"ready"}""")
        {
            webReady.TrySetResult();
            return;
        }
        PostToPage(await commands.HandleAsync(message));
    }

    void PostToPage(JsonObject message)
    {
        if (web?.CoreWebView2 is CoreWebView2 core)
            core.PostWebMessageAsJson(message.ToJsonString());
    }

    /// <summary>--screenshots with WebView2: every view of the app, captured by WebView2 itself.</summary>
    async Task TakeWebScreenshotsAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            await Task.WhenAny(webReady.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            string[] views = ["overview", "datasets", "snapshots", "devices", "health", "performance", "service"];
            for (int i = 0; i < views.Length; i++)
            {
                PostToPage(new JsonObject { ["type"] = "show", ["view"] = views[i] });
                // Performance needs a few samples to draw a line.
                await Task.Delay(views[i] == "performance" ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(3));
                await using var file = File.Create(Path.Combine(directory, $"{i + 1:00}-{views[i]}.png"));
                await web!.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
            }
        }
        finally
        {
            Close();
        }
    }

    // --- IWebHost ---------------------------------------------------------------

    string IWebHost.Edition => edition;

    async Task<DashboardData> IWebHost.DashboardAsync()
    {
        DashboardData data = await BuildDashboardAsync(refreshSeconds: null);
        lastDashboard = data;
        if (DateTimeOffset.UtcNow - lastWalk > AutoCountInterval && counting is null)
            _ = CountFilesAsync();
        return data;
    }

    (bool Running, string Text) IWebHost.CountState => (counting is not null, countState.Text ?? "");

    void IWebHost.StartCount() => _ = CountFilesAsync();

    void IWebHost.CancelCount() => counting?.Cancel();

    Task<IReadOnlyDictionary<int, DriveHealth>> IWebHost.DriveHealthAsync() => DriveHealthAsync();

    DevicePick? IWebHost.PickDevices(string title, string explanation, int maxDevices, bool offerMirror, IReadOnlyList<string> inUse)
    {
        using var dialog = new DeviceDialog(title, explanation, maxDevices, offerMirror, inUse);
        return dialog.ShowDialog(this) == DialogResult.OK ? new DevicePick(dialog.Devices, dialog.SelectionSummary, dialog.Mirror) : null;
    }

    string? IWebHost.PickFolder(string description)
    {
        using var dialog = new FolderBrowserDialog { Description = description, UseDescriptionForTitle = true };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
    }

    void IWebHost.OpenFolder(string path) => OpenFolder(path);

    void IWebHost.RevealFile(string path) => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path } });

    void IWebHost.SetClipboard(string text) => Clipboard.SetText(text);

    Task<string?> IWebHost.SaveReportAsync() => SaveReportAsync();

    IReadOnlyList<string> IWebHost.FreeDriveLetters() => MountPointDialog.FreeDriveLetters();

    ISet<string> IWebHost.MountedHere => mountedHere;

    bool IWebHost.KeepMounted
    {
        get => keepMounted.Checked;
        set => keepMounted.Checked = value;
    }

    string IWebHost.ServiceState() => UserModeService.State();

    Task IWebHost.ControlServiceAsync(string action) => UserModeService.ControlAsync(action);

    UserModeMounts IWebHost.Mounts => UserModeMounts.Machine();
}
