using System.Globalization;
using System.Net;
using System.Text;
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

    string RenderControlPage(TabPage page, out Dictionary<string, Func<string?, Task>> actions)
    {
        actions = [];
        var body = new StringBuilder();
        var ids = new IdGenerator();
        foreach (Control control in page.Controls)
        {
            if (webHosts.TryGetValue(page, out Panel? host) && ReferenceEquals(control, host))
                continue;
            body.Append(RenderControl(control, actions, ids));
        }

        return $$$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{{Encode(page.Text)}}}</title><style>
            :root{color-scheme:light dark;font:14px "Segoe UI",system-ui,sans-serif;--bg:#f4f7fb;--card:#fff;--fg:#1b2430;--mut:#5e6a79;--line:#dce3ec;--accent:#1769aa;--bad:#b42318}
            @media(prefers-color-scheme:dark){:root{--bg:#171b22;--card:#222a35;--fg:#edf2f8;--mut:#a4b0bf;--line:#3a4655;--accent:#78b9f1;--bad:#ff8b82}}
            *{box-sizing:border-box}body{margin:0;padding:16px;background:var(--bg);color:var(--fg)}header{display:flex;align-items:center;justify-content:space-between;margin-bottom:12px}
            h1{font-size:20px;margin:0}.layout{display:flex;gap:12px;flex-wrap:wrap;align-items:stretch}.flow{align-items:center}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:12px}
            .stack{display:flex;flex-direction:column;gap:8px}.split{display:grid;gap:12px}.split.vertical{grid-template-columns:repeat(2,minmax(0,1fr))}.split.horizontal{grid-template-columns:1fr}
            section.card{min-width:0;background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px;flex:1}
            h2{margin:0 0 8px;font-size:12px;text-transform:uppercase;letter-spacing:.06em;color:var(--mut)}.note{color:var(--mut);margin:4px 0;max-width:1000px}
            button,select,input,textarea{font:inherit;color:var(--fg);background:var(--card);border:1px solid var(--line);border-radius:6px;padding:7px 10px}
            button{cursor:pointer;background:var(--accent);border-color:var(--accent);color:#fff;font-weight:600}button:disabled,input:disabled,select:disabled,textarea:disabled{opacity:.5;cursor:not-allowed}
            input[type=checkbox]{accent-color:var(--accent)}input[type=number]{width:7rem}label{display:inline-flex;gap:7px;align-items:center}
            .control{display:flex;gap:8px;align-items:center;flex-wrap:wrap}.list{overflow:auto;max-height:420px;border:1px solid var(--line);border-radius:6px}
            table{width:100%;border-collapse:collapse;white-space:nowrap}th,td{text-align:left;padding:7px 9px;border-bottom:1px solid var(--line)}th{position:sticky;top:0;background:var(--card);color:var(--mut)}
            tr[data-select-id]{cursor:pointer}tr.selected,button.tree.selected{background:color-mix(in srgb,var(--accent) 18%,var(--card));color:var(--fg)}
            ul.tree{list-style:none;padding-left:16px;margin:4px 0}button.tree{background:transparent;color:var(--fg);border-color:transparent;text-align:left;font-weight:400;padding:4px 7px}
            progress{width:min(360px,100%);height:16px;accent-color:var(--accent)}.chart{width:100%;min-height:180px;background:var(--card);border:1px solid var(--line);border-radius:8px}.status{color:var(--mut);font-size:12px}
            </style></head><body><header><h1>{{{Encode(page.Text)}}}</h1><span class="status">{{{Encode(edition)}}}</span></header>
            <main>{{{body}}}</main>
            <script>
            document.addEventListener('click',event=>{const target=event.target.closest('[data-click-id],[data-select-id]');if(!target)return;window.chrome.webview.postMessage({id:target.dataset.clickId||target.dataset.selectId,value:target.dataset.value||null});});
            document.addEventListener('change',event=>{const target=event.target;if(target.dataset.changeId)window.chrome.webview.postMessage({id:target.dataset.changeId,value:target.type==='checkbox'?target.checked:target.value});});
            </script></body></html>
            """;
    }

    string RenderControl(Control control, Dictionary<string, Func<string?, Task>> actions, IdGenerator ids)
    {
        if (!control.Visible || control is TabPage)
            return "";

        if (control is Button button)
        {
            string id = ids.Next();
            actions[id] = _ =>
            {
                if (button.Enabled)
                    button.PerformClick();
                return Task.CompletedTask;
            };
            return $"<button data-click-id=\"{id}\"{Disabled(button.Enabled)}>{Encode(button.Text)}</button>";
        }
        if (control is Label label)
            return label.Text.Length == 0 ? "" : $"<p class=\"note\">{Encode(label.Text)}</p>";
        if (control is ComboBox combo)
        {
            string id = ids.Next();
            actions[id] = value => SetComboAsync(combo, value);
            if (combo.DropDownStyle == ComboBoxStyle.DropDownList)
            {
                var options = new StringBuilder();
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    string selected = string.Equals(combo.Items[i]?.ToString(), combo.Text, StringComparison.Ordinal) ? " selected" : "";
                    options.Append("<option value=\"").Append(i).Append('"').Append(selected).Append('>')
                        .Append(Encode(combo.Items[i]?.ToString())).Append("</option>");
                }
                return $"<label>{Encode(combo.AccessibleName)}<select data-change-id=\"{id}\"{Disabled(combo.Enabled)}>{options}</select></label>";
            }
            return $"<label><input data-change-id=\"{id}\" type=\"text\" value=\"{Encode(combo.Text)}\"{Disabled(combo.Enabled)}></label>";
        }
        if (control is CheckBox checkBox)
        {
            string id = ids.Next();
            actions[id] = value =>
            {
                if (bool.TryParse(value, out bool checkedValue))
                    checkBox.Checked = checkedValue;
                return Task.CompletedTask;
            };
            return $"<label><input type=\"checkbox\" data-change-id=\"{id}\"{(checkBox.Checked ? " checked" : "")}{Disabled(checkBox.Enabled)}>{Encode(checkBox.Text)}</label>";
        }
        if (control is NumericUpDown numeric)
        {
            string id = ids.Next();
            actions[id] = value =>
            {
                if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed))
                    numeric.Value = Math.Clamp(parsed, numeric.Minimum, numeric.Maximum);
                return Task.CompletedTask;
            };
            return $"<input type=\"number\" data-change-id=\"{id}\" min=\"{numeric.Minimum.ToString(CultureInfo.InvariantCulture)}\" max=\"{numeric.Maximum.ToString(CultureInfo.InvariantCulture)}\" value=\"{numeric.Value.ToString(CultureInfo.InvariantCulture)}\"{Disabled(numeric.Enabled)}>";
        }
        if (control is TextBox textBox)
        {
            string id = ids.Next();
            actions[id] = value =>
            {
                textBox.Text = value ?? "";
                return Task.CompletedTask;
            };
            return textBox.Multiline
                ? $"<textarea data-change-id=\"{id}\"{Disabled(textBox.Enabled)}>{Encode(textBox.Text)}</textarea>"
                : $"<input type=\"text\" data-change-id=\"{id}\" value=\"{Encode(textBox.Text)}\"{Disabled(textBox.Enabled)}>";
        }
        if (control is ListView listView)
            return RenderList(listView, actions, ids);
        if (control is TreeView treeView)
            return RenderTree(treeView, actions, ids);
        if (control is ProgressBar progress)
            return $"<progress max=\"{progress.Maximum}\" value=\"{progress.Value}\"></progress>";
        if (control is RateChart chart)
            return RenderChart(chart);
        if (control is GroupBox group)
            return $"<section class=\"card\"><h2>{Encode(group.Text)}</h2>{RenderChildren(group, actions, ids)}</section>";
        if (control is SplitContainer split)
        {
            string orientation = split.Orientation == Orientation.Vertical ? "vertical" : "horizontal";
            return $"<div class=\"split {orientation}\"><section class=\"card\">{RenderChildren(split.Panel1, actions, ids)}</section><section class=\"card\">{RenderChildren(split.Panel2, actions, ids)}</section></div>";
        }
        if (control is FlowLayoutPanel flow)
        {
            string stack = flow.FlowDirection is FlowDirection.TopDown or FlowDirection.BottomUp ? "stack" : "flow";
            return $"<div class=\"layout {stack}\">{RenderChildren(flow, actions, ids)}</div>";
        }
        if (control is TableLayoutPanel)
            return $"<div class=\"grid\">{RenderChildren(control, actions, ids)}</div>";
        if (control.Controls.Count > 0)
            return $"<div class=\"layout\">{RenderChildren(control, actions, ids)}</div>";
        return control.Text.Length == 0 ? "" : $"<p class=\"note\">{Encode(control.Text)}</p>";
    }

    string RenderList(ListView list, Dictionary<string, Func<string?, Task>> actions, IdGenerator ids)
    {
        var html = new StringBuilder("<div class=\"list\"><table><thead><tr>");
        foreach (ColumnHeader column in list.Columns)
            html.Append("<th>").Append(Encode(column.Text)).Append("</th>");
        html.Append("</tr></thead><tbody>");
        for (int i = 0; i < list.Items.Count; i++)
        {
            string id = ids.Next();
            ListViewItem item = list.Items[i];
            actions[id] = _ =>
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
            };
            html.Append($"<tr data-select-id=\"{id}\" class=\"{(item.Selected ? "selected" : "")}\">");
            for (int c = 0; c < Math.Max(list.Columns.Count, item.SubItems.Count); c++)
                html.Append("<td>").Append(Encode(c < item.SubItems.Count ? item.SubItems[c].Text : "")).Append("</td>");
            html.Append("</tr>");
        }
        return html.Append("</tbody></table></div>").ToString();
    }

    string RenderTree(TreeView tree, Dictionary<string, Func<string?, Task>> actions, IdGenerator ids)
    {
        var html = new StringBuilder("<ul class=\"tree\">");
        foreach (TreeNode node in tree.Nodes)
            AppendNode(node);
        return html.Append("</ul>").ToString();

        void AppendNode(TreeNode node)
        {
            string id = ids.Next();
            actions[id] = _ =>
            {
                tree.SelectedNode = node;
                return Task.CompletedTask;
            };
            html.Append("<li><button class=\"tree").Append(tree.SelectedNode == node ? " selected" : "")
                .Append("\" data-select-id=\"").Append(id).Append("\">")
                .Append(Encode(node.Text)).Append("</button>");
            if (node.Nodes.Count > 0)
            {
                html.Append("<ul class=\"tree\">");
                foreach (TreeNode child in node.Nodes)
                    AppendNode(child);
                html.Append("</ul>");
            }
            html.Append("</li>");
        }
    }

    string RenderChart(RateChart chart)
    {
        const double width = 800, height = 220, margin = 12;
        double maximum = Math.Max(1, chart.Readings.SelectMany(p => new[] { p.Read, p.Write }).DefaultIfEmpty().Max()) * 1.1;
        string Series(Func<(double Read, double Write), double> value) =>
            string.Join(' ', chart.Readings.Select((p, i) =>
                $"{(margin + (width - 2 * margin) * i / Math.Max(1, chart.Readings.Count - 1)).ToString("0.##", CultureInfo.InvariantCulture)},{(height - margin - value(p) / maximum * (height - 2 * margin)).ToString("0.##", CultureInfo.InvariantCulture)}"));
        string last = chart.Readings.Count == 0
            ? ""
            : $"<span>Read {Encode(chart.FormatValue(chart.Readings[^1].Read))} · Write {Encode(chart.FormatValue(chart.Readings[^1].Write))}</span>";
        return $"<section class=\"card\"><h2>{Encode(chart.Title)} {last}</h2><svg class=\"chart\" viewBox=\"0 0 800 220\" role=\"img\" aria-label=\"{Encode(chart.Title)} read and write rates\"><polyline points=\"{Series(p => p.Read)}\" fill=\"none\" stroke=\"#2f6fd0\" stroke-width=\"3\"/><polyline points=\"{Series(p => p.Write)}\" fill=\"none\" stroke=\"#d08a2f\" stroke-width=\"3\"/></svg></section>";
    }

    string RenderChildren(Control parent, Dictionary<string, Func<string?, Task>> actions, IdGenerator ids)
    {
        var html = new StringBuilder();
        foreach (Control child in parent.Controls)
            html.Append(RenderControl(child, actions, ids));
        return html.ToString();
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

    static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");

    static string Disabled(bool enabled) => enabled ? "" : " disabled";

    sealed class IdGenerator
    {
        int next;
        public string Next() => $"c{next++}";
    }
}
