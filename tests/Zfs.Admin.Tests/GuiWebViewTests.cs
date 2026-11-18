using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Zfs.Admin.Gui;

namespace Zfs.Admin.Tests;

/// <summary>The HTML the WebView2 tabs show for the WinForms controls (ControlHtml).</summary>
public sealed class GuiWebViewTests
{
    static readonly ControlHtmlHooks NoHooks = new((_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask);

    static string Render(Control page, out Dictionary<string, Func<string?, Task>> actions) =>
        ControlHtml.Page(page, null, "Kernel edition", NoHooks, out actions);

    static string Render(Control page) => Render(page, out _);

    [Fact]
    public void DockedControlsRenderWhereWinFormsShowsThem()
    {
        // The Snapshots tab's shape: the filling list added first, the action
        // column docked right, the picker docked top and added last.
        var page = new Panel { Text = "Snapshots" };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details };
        list.Columns.Add("Snapshot");
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown };
        actions.Controls.Add(new Button { Text = "Create snapshot…" });
        var top = new FlowLayoutPanel { Dock = DockStyle.Top };
        top.Controls.Add(new Label { Text = "Dataset:" });
        top.Controls.Add(new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
        var outer = new FlowLayoutPanel { Dock = DockStyle.Top };
        outer.Controls.Add(new Label { Text = "Outermost" });
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom };
        bottom.Controls.Add(new Label { Text = "Footer" });
        page.Controls.AddRange([list, actions, bottom, top, outer]);

        string html = Render(page);

        int outermost = html.IndexOf("Outermost", StringComparison.Ordinal);
        int picker = html.IndexOf("Dataset:", StringComparison.Ordinal);
        int table = html.IndexOf("<table", StringComparison.Ordinal);
        int side = html.IndexOf("<div class=\"dock-side\">", StringComparison.Ordinal);
        int footer = html.IndexOf("Footer", StringComparison.Ordinal);
        Assert.True(outermost >= 0 && outermost < picker, "the top control added last is the outermost");
        Assert.True(picker < table, "the picker sits above the list");
        Assert.True(table < side && side < footer, "the right column follows the list; the bottom bar comes last");
        Assert.Contains("<div class=\"dock-row\"><div class=\"dock-fill dock\">", html);
    }

    [Fact]
    public void RowsAndNodesKeepTheirHealthColour()
    {
        var list = new ListView { View = View.Details };
        list.Columns.Add("Pool");
        list.Items.Add(new ListViewItem("tank") { ForeColor = Color.Firebrick });
        list.Items.Add(new ListViewItem("zwin/vm") { ForeColor = Color.DarkOrange });
        list.Items.Add(new ListViewItem("healthy"));
        var mounted = new ListViewItem("@before-cleanup");
        list.Items.Add(mounted);
        mounted.Font = new Font(list.Font, FontStyle.Bold);
        var tree = new TreeView();
        TreeNode root = tree.Nodes.Add("zwin  (DEGRADED)");
        root.Nodes.Add("disk1  ONLINE");
        root.Nodes.Add("disk2  FAULTED").ForeColor = Color.Firebrick;
        var page = new Panel { Text = "Pools" };
        page.Controls.AddRange([list, tree]);

        string html = Render(page);

        Assert.Matches("<tr[^>]*data-tone=\"bad\"[^>]*><td>tank</td>", html);
        Assert.Matches("<tr[^>]*data-tone=\"warn\"[^>]*><td>zwin/vm</td>", html);
        Assert.Matches("<tr data-select-id=\"c\\d+\"><td>healthy</td>", html);
        Assert.Matches("<tr[^>]*class=\"strong\"[^>]*><td>@before-cleanup</td>", html);
        Assert.Matches("<button class=\"tree\" data-tone=\"bad\"[^>]*>disk2  FAULTED</button>", html);
        Assert.Matches("<button class=\"tree\" data-select-id=\"c\\d+\">disk1  ONLINE</button>", html);
    }

    [Fact]
    public void ButtonsCarryTheirWeight()
    {
        var page = new Panel { Text = "Snapshots" };
        page.Controls.Add(new Button { Text = "Create snapshot…" }.Weighted(ButtonWeight.Primary));
        page.Controls.Add(new Button { Text = "Open in Explorer" });
        page.Controls.Add(new Button { Text = "Destroy snapshot…" }.Weighted(ButtonWeight.Danger));

        string html = Render(page);

        Assert.Matches("<button data-click-id=\"c\\d+\" class=\"primary\">Create snapshot…</button>", html);
        Assert.Matches("<button data-click-id=\"c\\d+\">Open in Explorer</button>", html);
        Assert.Matches("<button data-click-id=\"c\\d+\" class=\"danger\">Destroy snapshot…</button>", html);
    }

    [Fact]
    public void ButtonActionClicksTheNativeButton()
    {
        var page = new Panel { Text = "Pools" };
        var button = new Button { Text = "Start scrub" };
        int clicks = 0;
        button.Click += (_, _) => clicks++;
        page.Controls.Add(button);

        string html = Render(page, out var actions);
        string id = Regex.Match(html, "data-click-id=\"(c\\d+)\"").Groups[1].Value;
        actions[id](null);

        Assert.Equal(1, clicks);
    }

    [Fact]
    public void InputsAreLabelled()
    {
        var page = new Panel { Text = "Rates" };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top };
        var pool = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        pool.Items.AddRange(["zwin", "tank<1>"]);
        top.Controls.Add(new Label { Text = "Pool:" });
        top.Controls.Add(pool);
        top.Controls.Add(new NumericUpDown { Minimum = 1, Maximum = 60, Value = 5, AccessibleName = "Sample interval in seconds" });
        var features = new TableLayoutPanel { ColumnCount = 3 };
        var compression = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Text = "zstd" };
        compression.Items.AddRange(["off", "lz4", "zstd"]);
        features.Controls.Add(new Label { Text = "Compression:" });
        features.Controls.Add(compression);
        features.Controls.Add(new Button { Text = "Apply" });
        page.Controls.Add(features);
        page.Controls.Add(top);

        string html = Render(page);

        // A label before an input wraps it; nothing renders the label twice.
        Assert.Matches("<label><span>Pool:</span><select data-change-id=\"c\\d+\">", html);
        Assert.DoesNotContain("<p class=\"note\">Pool:</p>", html);
        Assert.Contains("tank&lt;1&gt;", html);
        // Without a visible label, the accessible name labels it.
        Assert.Matches("<input type=\"number\" data-change-id=\"c\\d+\" aria-label=\"Sample interval in seconds\"", html);
        // An editable combo keeps its values as suggestions, one table row per line.
        Assert.Matches("<div class=\"row\"><label><span>Compression:</span><input type=\"text\" list=\"(c\\d+)-list\"[^>]*value=\"zstd\"><datalist id=\"\\1-list\"><option value=\"off\"></option>", html);
        Assert.Matches("</datalist></label><button data-click-id=\"c\\d+\">Apply</button></div>", html);
    }

    [Fact]
    public void PageIsSelfContainedAndUsesTheSharedTheme()
    {
        var page = new Panel { Text = "<Pools>" };
        string html = Render(page);

        Assert.Contains("default-src 'none'", html);
        Assert.DoesNotContain("https:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(WebTheme.Tokens(null), html);
        Assert.Contains("<h1>&lt;Pools&gt;</h1>", html);
        Assert.DoesNotContain("<Pools>", html);
        foreach (string tag in new[] { "div", "section", "label", "table", "ul", "li", "button" })
            Assert.Equal(Regex.Matches(html, $"<{tag}[ >]").Count, Regex.Matches(html, $"</{tag}>").Count);

        string dashboard = DashboardHtml.Render(new DashboardData("Kernel edition", DateTimeOffset.UtcNow, [], null, Dark: true));
        Assert.Contains(WebTheme.Tokens(true), dashboard);
    }

    [Theory]
    [InlineData(KnownColor.Firebrick, "bad")]
    [InlineData(KnownColor.DarkOrange, "warn")]
    [InlineData(KnownColor.WindowText, null)]
    public void ToneFollowsTheNativeColour(KnownColor color, string? tone) =>
        Assert.Equal(tone, ControlHtml.Tone(Color.FromKnownColor(color)));
}
