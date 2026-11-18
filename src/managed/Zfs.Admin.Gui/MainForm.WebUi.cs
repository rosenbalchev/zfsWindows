using System.Globalization;
using System.Text.Json;
using System.Windows.Forms;

namespace Zfs.Admin.Gui;

sealed partial class MainForm
{
    readonly Dictionary<TabPage, Panel> webHosts = [];
    Dictionary<string, Func<string?, Task>> webActions = [];
    string lastWebMarkup = "";
    string lastWebPage = "";
    bool applyingWebAction;

    void RegisterWebHosts()
    {
        foreach (TabPage page in tabs.TabPages)
        {
            Panel host;
            if (page.Text == "Overview")
            {
                host = overviewHost;
            }
            else
            {
                host = new Panel { Dock = DockStyle.Fill };
                page.Controls.Add(host);
            }
            host.Visible = false;
            webHosts.Add(page, host);
        }
    }

    void MoveWebViewToSelectedTab()
    {
        if (web is null || tabs.SelectedTab is not TabPage page || !webHosts.TryGetValue(page, out Panel? host))
            return;

        foreach (Panel panel in webHosts.Values)
            panel.Visible = false;
        if (!ReferenceEquals(web.Parent, host))
        {
            web.Parent?.Controls.Remove(web);
            host.Controls.Add(web);
        }
        web.Dock = DockStyle.Fill;
        host.Visible = true;
        RenderSelectedWebPage();
    }

    void RenderSelectedWebPage()
    {
        if (web?.CoreWebView2 is null || tabs.SelectedTab is not TabPage page)
            return;

        string html;
        string comparable;
        if (page.Text == "Overview")
        {
            if (lastDashboard is null)
                return;
            html = DashboardHtml.Render(lastDashboard);
            comparable = lastHtml;
            webActions = [];
        }
        else
        {
            html = RenderControlPage(page, out Dictionary<string, Func<string?, Task>> actions);
            comparable = html;
            webActions = actions;
        }

        if (lastWebPage == page.Text && lastWebMarkup == comparable)
            return;
        lastWebPage = page.Text;
        lastWebMarkup = comparable;
        web.NavigateToString(html);
    }

    async void OnWebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!e.Source.StartsWith("data:", StringComparison.Ordinal) && !e.Source.StartsWith("about:", StringComparison.Ordinal))
            return;
        try
        {
            using JsonDocument message = JsonDocument.Parse(e.WebMessageAsJson);
            if (message.RootElement.ValueKind != JsonValueKind.Object
                || !message.RootElement.TryGetProperty("id", out JsonElement idNode)
                || idNode.ValueKind != JsonValueKind.String
                || !webActions.TryGetValue(idNode.GetString()!, out Func<string?, Task>? action))
            {
                statusText.Text = "Ignored an invalid WebView action.";
                return;
            }

            string? value = message.RootElement.TryGetProperty("value", out JsonElement valueNode)
                ? valueNode.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => valueNode.ToString(),
                }
                : null;
            await action(value);
            RenderSelectedWebPage();
        }
        catch (JsonException)
        {
            statusText.Text = "Ignored an invalid WebView message.";
        }
        catch (AdminException adminError)
        {
            BackgroundError("Web UI", adminError);
            RenderSelectedWebPage();
        }
        catch (InvalidOperationException invalidAction)
        {
            statusText.Text = $"Web UI action failed: {invalidAction.Message}";
            RenderSelectedWebPage();
        }
    }

    string RenderControlPage(TabPage page, out Dictionary<string, Func<string?, Task>> actions) =>
        ControlHtml.Page(page, webHosts.GetValueOrDefault(page), edition, new ControlHtmlHooks(SelectRowAsync, SetComboAsync), out actions);

    Task SelectRowAsync(ListView list, ListViewItem item)
    {
        bool wasApplyingWebAction = applyingWebAction;
        applyingWebAction = true;
        try
        {
            foreach (ListViewItem old in list.SelectedItems.Cast<ListViewItem>().ToArray())
                old.Selected = false;
            item.Selected = true;
            item.Focused = true;
        }
        finally
        {
            applyingWebAction = wasApplyingWebAction;
        }
        return RefreshAfterSelectionAsync(list);
    }

    async Task SetComboAsync(ComboBox combo, string? value)
    {
        applyingWebAction = true;
        try
        {
            if (combo.DropDownStyle == ComboBoxStyle.DropDownList)
            {
                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                    && index >= 0 && index < combo.Items.Count)
                    combo.SelectedIndex = index;
            }
            else
            {
                combo.Text = value ?? "";
            }
        }
        finally
        {
            applyingWebAction = false;
        }
        await RefreshAfterSelectionAsync(combo);
    }

    async Task RefreshAfterSelectionAsync(Control control)
    {
        if (ReferenceEquals(control, pools))
            await ShowPoolAsync();
        else if (ReferenceEquals(control, datasets))
            await ShowDatasetAsync();
        else if (ReferenceEquals(control, devicePool))
            await ShowDevicesAsync();
        else if (ReferenceEquals(control, integrityPool))
            await ShowIntegrityAsync();
        else if (ReferenceEquals(control, ratesPool))
        {
            ResetRates();
            await SampleRatesAsync();
        }
        else if (ReferenceEquals(control, snapshotDataset))
            await ShowSnapshotsAsync();
    }
}
