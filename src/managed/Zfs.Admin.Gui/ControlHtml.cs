using System.Globalization;
using System.Net;
using System.Text;
using Zfs.Admin;

namespace Zfs.Admin.Gui;

/// <summary>How much a button's action weighs: the web view styles each differently.</summary>
enum ButtonWeight
{
    Default,
    /// <summary>The page's main action; at most one per tab.</summary>
    Primary,
    /// <summary>Destroys or reduces data or redundancy, or stops a service.</summary>
    Danger,
}

static class ButtonWeights
{
    /// <summary>Marks the button for the web view (kept in <see cref="Control.Tag"/>).</summary>
    public static Button Weighted(this Button button, ButtonWeight weight)
    {
        button.Tag = weight;
        return button;
    }
}

/// <summary>What the page's controls call back into the form for.</summary>
sealed record ControlHtmlHooks(
    Func<ListView, ListViewItem, Task> SelectRow,
    Func<ComboBox, string?, Task> SetCombo);

/// <summary>
/// Renders a tab's WinForms controls as HTML for the shared WebView2: the
/// layout in the order WinForms docks it, each row's health colour, each
/// button's weight, and labels joined to the inputs they name.  Every
/// interactive element gets an id the page posts back; <c>actions</c> maps
/// those ids to what the native control would do.
/// </summary>
sealed class ControlHtml
{
    readonly Dictionary<string, Func<string?, Task>> actions;
    readonly ControlHtmlHooks hooks;
    int next;

    ControlHtml(Dictionary<string, Func<string?, Task>> actions, ControlHtmlHooks hooks)
    {
        this.actions = actions;
        this.hooks = hooks;
    }

    public static string Page(Control page, Control? skip, string edition, ControlHtmlHooks hooks, out Dictionary<string, Func<string?, Task>> actions)
    {
        actions = [];
        var renderer = new ControlHtml(actions, hooks);
        string body = renderer.Children(page, skip);
        return $$$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{{E(page.Text)}}}</title><style>
            {{{WebTheme.Tokens(null)}}}
            {{{Css}}}
            </style></head><body><header><h1>{{{E(page.Text)}}}</h1><span class="edition">{{{E(edition)}}}</span></header>
            <main class="dock">{{{body}}}</main>
            <script>
            document.addEventListener('click',event=>{const target=event.target.closest('[data-click-id],[data-select-id]');if(!target)return;window.chrome.webview.postMessage({id:target.dataset.clickId||target.dataset.selectId,value:target.dataset.value||null});});
            document.addEventListener('change',event=>{const target=event.target;if(target.dataset.changeId)window.chrome.webview.postMessage({id:target.dataset.changeId,value:target.type==='checkbox'?target.checked:target.value});});
            </script></body></html>
            """;
    }

    const string Css = $$"""
        *{box-sizing:border-box}body{margin:0;padding:16px;background:var(--bg);color:var(--fg);font:{{WebTheme.Font}}}
        header{display:flex;align-items:center;gap:12px;margin-bottom:12px}h1{font-size:18px;margin:0;font-weight:600}
        .edition{margin-left:auto;padding:2px 10px;border-radius:10px;background:var(--accent-soft);color:var(--accent);font-weight:600;font-size:11px}
        .dock{display:flex;flex-direction:column;gap:12px;min-width:0}
        .dock-row{display:flex;gap:12px;align-items:flex-start;flex-wrap:wrap}.dock-fill{flex:1 1 480px;min-width:0}
        .dock-side{flex:0 1 300px;display:flex;flex-direction:column;gap:8px;min-width:0}.dock-side>button{text-align:left}
        .flow{display:flex;gap:8px;flex-wrap:wrap;align-items:center}.stack{display:flex;flex-direction:column;gap:8px;align-items:stretch}
        .row{display:flex;gap:12px;flex-wrap:wrap;align-items:center}.row>section.card{flex:1 1 280px}
        .split{display:grid;gap:12px}.split.vertical{grid-template-columns:repeat(2,minmax(0,1fr))}.split.horizontal{grid-template-columns:1fr}
        section.card{min-width:0;background:var(--card);border:1px solid var(--line);border-radius:8px;padding:12px 14px}
        h2{margin:0 0 8px;font-size:11px;text-transform:uppercase;letter-spacing:.06em;color:var(--mut);font-weight:600}
        .note{color:var(--mut);margin:0;max-width:110ch}
        button,select,input,textarea{font:inherit;color:var(--fg);background:var(--card);border:1px solid var(--line);border-radius:6px;padding:6px 10px}
        button{cursor:pointer;font-weight:600}button:hover{background:var(--sunk)}
        button.primary{background:var(--accent);border-color:var(--accent);color:var(--accent-fg)}button.primary:hover{filter:brightness(1.08)}
        button.danger{color:var(--bad);border-color:color-mix(in srgb,var(--bad) 45%,var(--line))}button.danger:hover{background:var(--bad-soft)}
        button:disabled,input:disabled,select:disabled,textarea:disabled{opacity:.5;cursor:not-allowed}
        :focus-visible{outline:2px solid var(--accent);outline-offset:1px}
        input[type=checkbox]{accent-color:var(--accent)}input[type=number]{width:6rem}label{display:inline-flex;gap:7px;align-items:center}
        .list{overflow:auto;max-height:420px;border:1px solid var(--line);border-radius:6px;background:var(--card)}
        table{width:100%;border-collapse:collapse;white-space:nowrap;font-variant-numeric:tabular-nums}th,td{text-align:left;padding:6px 9px;border-bottom:1px solid var(--line)}
        th{position:sticky;top:0;background:var(--card);color:var(--mut);font-weight:600}
        tr[data-select-id]{cursor:pointer}tr[data-select-id]:hover td{background:var(--sunk)}
        tr.selected td,button.tree.selected{background:var(--accent-soft)}
        tr.strong td{font-weight:600}
        tr[data-tone=bad] td{color:var(--bad)}tr[data-tone=warn] td{color:var(--warn)}
        tr[data-tone] td:first-child{box-shadow:inset 3px 0 currentColor}
        ul.tree{list-style:none;padding-left:16px;margin:4px 0}
        button.tree{background:transparent;color:var(--fg);border-color:transparent;text-align:left;font-weight:400;padding:4px 7px}
        button.tree[data-tone=bad]{color:var(--bad);font-weight:600}button.tree[data-tone=warn]{color:var(--warn);font-weight:600}
        progress{width:min(360px,100%);height:16px;accent-color:var(--accent)}
        .chart{width:100%;min-height:180px;background:var(--card);border:1px solid var(--line);border-radius:6px}
        .chart .read{stroke:var(--read)}.chart .write{stroke:var(--write)}
        .legend{font-weight:400;text-transform:none;letter-spacing:0;color:var(--fg);margin-left:8px}
        .legend b{display:inline-block;width:9px;height:9px;border-radius:2px;margin:0 4px 0 10px}
        """;

    // --- layout ---------------------------------------------------------------

    /// <summary>
    /// A container's children where WinForms shows them.  Docking runs from
    /// the highest index down, so the control added last takes the outer
    /// edge: Top children read last-to-first, Bottom children first-to-last,
    /// and Left/Right children flank the filling ones.  Flow and table
    /// layouts place their children in index order.
    /// </summary>
    string Children(Control parent, Control? skip = null)
    {
        Control[] all = parent.Controls.Cast<Control>().Where(c => !ReferenceEquals(c, skip) && c.Visible).ToArray();
        if (parent is FlowLayoutPanel)
            return Sequence(all);
        if (parent is TableLayoutPanel table)
        {
            int columns = Math.Max(1, table.ColumnCount);
            return string.Concat(all.Chunk(columns).Select(row => $"<div class=\"row\">{Sequence(row)}</div>"));
        }

        Control[] tops = [.. all.Where(c => c.Dock == DockStyle.Top).Reverse()];
        Control[] lefts = [.. all.Where(c => c.Dock == DockStyle.Left).Reverse()];
        Control[] fills = [.. all.Where(c => c.Dock is DockStyle.Fill or DockStyle.None)];
        Control[] rights = [.. all.Where(c => c.Dock == DockStyle.Right)];
        Control[] bottoms = [.. all.Where(c => c.Dock == DockStyle.Bottom)];

        var html = new StringBuilder(Sequence(tops));
        string middle = Sequence(fills);
        if (lefts.Length + rights.Length == 0)
        {
            html.Append(middle);
        }
        else
        {
            html.Append("<div class=\"dock-row\">");
            if (lefts.Length > 0)
                html.Append("<div class=\"dock-side\">").Append(Sequence(lefts)).Append("</div>");
            html.Append("<div class=\"dock-fill dock\">").Append(middle).Append("</div>");
            if (rights.Length > 0)
                html.Append("<div class=\"dock-side\">").Append(Sequence(rights)).Append("</div>");
            html.Append("</div>");
        }
        return html.Append(Sequence(bottoms)).ToString();
    }

    /// <summary>Controls in the given order; a label directly before an input becomes that input's label.</summary>
    string Sequence(IReadOnlyList<Control> controls)
    {
        var html = new StringBuilder();
        for (int i = 0; i < controls.Count; i++)
        {
            if (controls[i] is Label { Text.Length: > 0 } label && i + 1 < controls.Count && IsInput(controls[i + 1]))
            {
                html.Append(Render(controls[++i], label.Text));
                continue;
            }
            html.Append(Render(controls[i], null));
        }
        return html.ToString();
    }

    static bool IsInput(Control control) => control is ComboBox or NumericUpDown or (TextBox and not { Multiline: true });

    string Render(Control control, string? label)
    {
        if (!control.Visible || control is TabPage)
            return "";

        switch (control)
        {
            case Button button:
                {
                    string id = Register(_ =>
                    {
                        if (button.Enabled)
                            button.PerformClick();
                        return Task.CompletedTask;
                    });
                    string weight = button.Tag switch { ButtonWeight.Primary => " class=\"primary\"", ButtonWeight.Danger => " class=\"danger\"", _ => "" };
                    return $"<button data-click-id=\"{id}\"{weight}{Disabled(button.Enabled)}>{E(button.Text)}</button>";
                }
            case Label text:
                return text.Text.Length == 0 ? "" : $"<p class=\"note\">{E(text.Text)}</p>";
            case ComboBox combo:
                return Labelled(label, RenderCombo(combo, label is null ? combo.AccessibleName : null));
            case CheckBox checkBox:
                {
                    string id = Register(value =>
                    {
                        if (bool.TryParse(value, out bool checkedValue))
                            checkBox.Checked = checkedValue;
                        return Task.CompletedTask;
                    });
                    return $"<label><input type=\"checkbox\" data-change-id=\"{id}\"{(checkBox.Checked ? " checked" : "")}{Disabled(checkBox.Enabled)}>{E(checkBox.Text)}</label>";
                }
            case NumericUpDown numeric:
                {
                    string id = Register(value =>
                    {
                        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed))
                            numeric.Value = Math.Clamp(parsed, numeric.Minimum, numeric.Maximum);
                        return Task.CompletedTask;
                    });
                    return Labelled(label, $"<input type=\"number\" data-change-id=\"{id}\"{AriaLabel(label, numeric)} min=\"{numeric.Minimum.ToString(CultureInfo.InvariantCulture)}\" max=\"{numeric.Maximum.ToString(CultureInfo.InvariantCulture)}\" value=\"{numeric.Value.ToString(CultureInfo.InvariantCulture)}\"{Disabled(numeric.Enabled)}>");
                }
            case TextBox textBox:
                {
                    string id = Register(value =>
                    {
                        textBox.Text = value ?? "";
                        return Task.CompletedTask;
                    });
                    return textBox.Multiline
                        ? $"<textarea data-change-id=\"{id}\"{AriaLabel(null, textBox)}{Disabled(textBox.Enabled)}>{E(textBox.Text)}</textarea>"
                        : Labelled(label, $"<input type=\"text\" data-change-id=\"{id}\"{AriaLabel(label, textBox)} value=\"{E(textBox.Text)}\"{Disabled(textBox.Enabled)}>");
                }
            case ListView list:
                return RenderList(list);
            case TreeView tree:
                return RenderTree(tree);
            case ProgressBar progress:
                return $"<progress max=\"{progress.Maximum}\" value=\"{progress.Value}\"></progress>";
            case RateChart chart:
                return RenderChart(chart);
            case GroupBox group:
                return $"<section class=\"card dock\"><h2>{E(group.Text)}</h2>{Children(group)}</section>";
            case SplitContainer split:
                {
                    string orientation = split.Orientation == Orientation.Vertical ? "vertical" : "horizontal";
                    return $"<div class=\"split {orientation}\"><div class=\"dock\">{Children(split.Panel1)}</div><div class=\"dock\">{Children(split.Panel2)}</div></div>";
                }
            case FlowLayoutPanel flow:
                {
                    string kind = flow.FlowDirection is FlowDirection.TopDown or FlowDirection.BottomUp ? "stack" : "flow";
                    return $"<div class=\"{kind}\">{Children(flow)}</div>";
                }
            case TableLayoutPanel:
                return $"<div class=\"stack\">{Children(control)}</div>";
        }
        if (control.Controls.Count > 0)
            return $"<div class=\"dock\">{Children(control)}</div>";
        return control.Text.Length == 0 ? "" : $"<p class=\"note\">{E(control.Text)}</p>";
    }

    /// <summary>The visible label wraps the input; without one, the input's accessible name labels it.</summary>
    static string Labelled(string? label, string inputHtml) =>
        label is null ? inputHtml : $"<label><span>{E(label)}</span>{inputHtml}</label>";

    static string AriaLabel(string? label, Control input) =>
        label is null && input.AccessibleName is { Length: > 0 } name ? $" aria-label=\"{E(name)}\"" : "";

    string RenderCombo(ComboBox combo, string? accessibleName)
    {
        string id = Register(value => hooks.SetCombo(combo, value));
        string aria = accessibleName is { Length: > 0 } ? $" aria-label=\"{E(accessibleName)}\"" : "";
        var options = new StringBuilder();
        if (combo.DropDownStyle == ComboBoxStyle.DropDownList)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                string selected = string.Equals(combo.Items[i]?.ToString(), combo.Text, StringComparison.Ordinal) ? " selected" : "";
                options.Append("<option value=\"").Append(i).Append('"').Append(selected).Append('>')
                    .Append(E(combo.Items[i]?.ToString())).Append("</option>");
            }
            return $"<select data-change-id=\"{id}\"{aria}{Disabled(combo.Enabled)}>{options}</select>";
        }
        // An editable combo: free text, with its items offered as suggestions.
        foreach (object? item in combo.Items)
            options.Append("<option value=\"").Append(E(item?.ToString())).Append("\"></option>");
        return $"<input type=\"text\" list=\"{id}-list\" data-change-id=\"{id}\"{aria} value=\"{E(combo.Text)}\"{Disabled(combo.Enabled)}><datalist id=\"{id}-list\">{options}</datalist>";
    }

    string RenderList(ListView list)
    {
        var html = new StringBuilder("<div class=\"list\"><table>");
        if (list.AccessibleName is { Length: > 0 } name)
            html.Append("<caption hidden>").Append(E(name)).Append("</caption>");
        html.Append("<thead><tr>");
        foreach (ColumnHeader column in list.Columns)
            html.Append("<th>").Append(E(column.Text)).Append("</th>");
        html.Append("</tr></thead><tbody>");
        foreach (ListViewItem item in list.Items)
        {
            string id = Register(_ => hooks.SelectRow(list, item));
            string classes = string.Join(' ', new[] { item.Selected ? "selected" : null, item.Font.Bold ? "strong" : null }.OfType<string>());
            html.Append("<tr data-select-id=\"").Append(id).Append('"');
            if (classes.Length > 0)
                html.Append(" class=\"").Append(classes).Append('"');
            if (item.Selected)
                html.Append(" aria-selected=\"true\"");
            if (Tone(item.ForeColor) is string tone)
                html.Append(" data-tone=\"").Append(tone).Append('"');
            html.Append('>');
            for (int c = 0; c < Math.Max(list.Columns.Count, item.SubItems.Count); c++)
                html.Append("<td>").Append(E(c < item.SubItems.Count ? item.SubItems[c].Text : "")).Append("</td>");
            html.Append("</tr>");
        }
        return html.Append("</tbody></table></div>").ToString();
    }

    string RenderTree(TreeView tree)
    {
        var html = new StringBuilder("<ul class=\"tree\" role=\"tree\">");
        foreach (TreeNode node in tree.Nodes)
            AppendNode(node);
        return html.Append("</ul>").ToString();

        void AppendNode(TreeNode node)
        {
            string id = Register(_ =>
            {
                tree.SelectedNode = node;
                return Task.CompletedTask;
            });
            bool selected = tree.SelectedNode == node;
            html.Append("<li role=\"treeitem\"").Append(selected ? " aria-selected=\"true\"" : "").Append("><button class=\"tree")
                .Append(selected ? " selected" : "").Append('"');
            if (Tone(node.ForeColor) is string tone)
                html.Append(" data-tone=\"").Append(tone).Append('"');
            html.Append(" data-select-id=\"").Append(id).Append("\">").Append(E(node.Text)).Append("</button>");
            if (node.Nodes.Count > 0)
            {
                html.Append("<ul class=\"tree\" role=\"group\">");
                foreach (TreeNode child in node.Nodes)
                    AppendNode(child);
                html.Append("</ul>");
            }
            html.Append("</li>");
        }
    }

    static string RenderChart(RateChart chart)
    {
        const double width = 800, height = 220, margin = 12;
        double maximum = Math.Max(1, chart.Readings.SelectMany(p => new[] { p.Read, p.Write }).DefaultIfEmpty().Max()) * 1.1;
        string Series(Func<(double Read, double Write), double> value) =>
            string.Join(' ', chart.Readings.Select((p, i) =>
                $"{(margin + (width - 2 * margin) * i / Math.Max(1, chart.Readings.Count - 1)).ToString("0.##", CultureInfo.InvariantCulture)},{(height - margin - value(p) / maximum * (height - 2 * margin)).ToString("0.##", CultureInfo.InvariantCulture)}"));
        string last = chart.Readings.Count == 0
            ? ""
            : $"<span class=\"legend\"><b style=\"background:var(--read)\"></b>Read {E(chart.FormatValue(chart.Readings[^1].Read))}<b style=\"background:var(--write)\"></b>Write {E(chart.FormatValue(chart.Readings[^1].Write))}</span>";
        return $"<section class=\"card\"><h2>{E(chart.Title)}{last}</h2><svg class=\"chart\" viewBox=\"0 0 800 220\" role=\"img\" aria-label=\"{E(chart.Title)} read and write rates\"><polyline class=\"read\" points=\"{Series(p => p.Read)}\" fill=\"none\" stroke-width=\"3\"/><polyline class=\"write\" points=\"{Series(p => p.Write)}\" fill=\"none\" stroke-width=\"3\"/></svg></section>";
    }

    // --- helpers ----------------------------------------------------------------

    /// <summary>The health a row's or node's colour stands for in the native list.</summary>
    internal static string? Tone(Color color)
    {
        if (color.IsEmpty)
            return null;
        int argb = color.ToArgb();
        if (argb == Color.Firebrick.ToArgb() || argb == Color.Red.ToArgb() || argb == Color.DarkRed.ToArgb())
            return "bad";
        if (argb == Color.DarkOrange.ToArgb() || argb == Color.Orange.ToArgb())
            return "warn";
        return null;
    }

    string Register(Func<string?, Task> action)
    {
        string id = $"c{next++}";
        actions[id] = action;
        return id;
    }

    static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    static string Disabled(bool enabled) => enabled ? "" : " disabled";
}
