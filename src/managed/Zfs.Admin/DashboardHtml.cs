using System.Globalization;
using System.Net;
using System.Text;

namespace Zfs.Admin;

/// <summary>One pool's figures for the Overview dashboard.</summary>
public sealed record DashboardPool(
    PoolInfo Pool,
    PoolStatusInfo? Status,
    long DamagedFiles,
    long? BlockCloneSaved,
    IReadOnlyList<DatasetInfo> Datasets,
    IReadOnlyDictionary<string, DatasetCountInfo> Counts,
    RedundancyInfo? Redundancy);

public sealed record DashboardData(
    string Edition,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<DashboardPool> Pools,
    MemoryInfo? Memory,
    bool Dark = false,
    int? RefreshSeconds = null);

/// <summary>
/// The Overview tab: a self-contained HTML page (inline CSS and SVG, no
/// script, nothing fetched; its Content-Security-Policy forbids loading
/// anything) computed from one status snapshot.  The GUI shows it in
/// WebView2, or writes it to a file that reloads itself for a browser.
/// </summary>
public static class DashboardHtml
{
    static readonly CultureInfo En = CultureInfo.InvariantCulture;

    public static string Render(DashboardData data)
    {
        var h = new StringBuilder(16 * 1024);
        h.Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">");
        h.Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">");
        if (data.RefreshSeconds is int r and > 0)
            h.Append("<meta http-equiv=\"refresh\" content=\"").Append(r.ToString(En)).Append("\">");
        h.Append("<title>OpenZFS for Windows</title><style>").Append(Css(data.Dark)).Append("</style></head><body>");
        h.Append("<header><h1>OpenZFS for Windows</h1><span class=\"edition\">").Append(E(data.Edition)).Append("</span>");
        h.Append("<span class=\"mut\">Updated ").Append(E(data.GeneratedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", En))).Append("</span></header>");
        if (data.Pools.Count == 0)
            h.Append("<section class=\"card\"><h3>Pools</h3><p>No pool is imported.</p></section>");
        foreach (DashboardPool p in data.Pools)
            Pool(h, p);
        if (data.Memory is MemoryInfo m)
            Memory(h, m);
        h.Append("</body></html>\n");
        return h.ToString();
    }

    static void Pool(StringBuilder h, DashboardPool p)
    {
        string health = p.Pool.Health ?? p.Status?.State ?? "UNKNOWN";
        string tone = health switch { "ONLINE" => "ok", "DEGRADED" => "warn", _ => "bad" };
        h.Append("<section class=\"pool\">");
        // Header: health, layout, last scrub, damaged files.
        h.Append("<div class=\"card wide row\">");
        Stat(h, "Pool", $"<div class=\"big\">{E(p.Pool.Name)}</div>");
        Stat(h, "Health", $"<span class=\"pill {tone}\">{E(health)}</span>");
        if (p.Redundancy is RedundancyInfo red)
            Stat(h, "Layout", $"<div>{E(red.Layout)}</div>");
        Stat(h, "Last scrub", $"<div>{E(Scrub(p.Status?.Scan))}</div>");
        string dtone = p.DamagedFiles > 0 ? "bad" : "ok";
        Stat(h, "Damaged files", $"<div class=\"big {dtone}t\">{p.DamagedFiles.ToString("N0", En)}</div>");
        h.Append("</div>");

        // Capacity ring.
        long used = p.Pool.Allocated ?? 0, free = p.Pool.Free ?? 0, size = p.Pool.Size ?? used + free;
        double fraction = size > 0 ? Math.Clamp((double)used / size, 0, 1) : 0;
        const double r = 50, circumference = 2 * Math.PI * r;
        h.Append("<div class=\"card\"><h3>Capacity</h3><div class=\"row\">");
        h.Append("<svg width=\"130\" height=\"130\" viewBox=\"0 0 130 130\" role=\"img\" aria-label=\"")
            .Append(Pct(fraction)).Append(" used\"><circle cx=\"65\" cy=\"65\" r=\"50\" fill=\"none\" class=\"track\" stroke-width=\"16\"/>")
            .Append("<circle cx=\"65\" cy=\"65\" r=\"50\" fill=\"none\" class=\"").Append(fraction > 0.9 ? "fillbad" : fraction > 0.8 ? "fillwarn" : "fill")
            .Append("\" stroke-width=\"16\" stroke-dasharray=\"").Append(N(fraction * circumference)).Append(' ').Append(N(circumference))
            .Append("\" transform=\"rotate(-90 65 65)\"/><text x=\"65\" y=\"64\" text-anchor=\"middle\" class=\"ringv\">").Append(Pct(fraction))
            .Append("</text><text x=\"65\" y=\"82\" text-anchor=\"middle\" class=\"ringl\">used</text></svg>");
        h.Append("<div class=\"kv\"><div><span class=\"mut\">Used</span><b>").Append(Bytes(used)).Append("</b></div>")
            .Append("<div><span class=\"mut\">Free</span><b>").Append(Bytes(free)).Append("</b></div>")
            .Append("<div><span class=\"mut\">Fragmentation</span><b>").Append(p.Pool.FragmentationPercent is long f ? $"{f}%" : "–").Append("</b></div></div>");
        h.Append("</div></div>");

        // Space saved: compression per dataset, dedup, block cloning.
        long compression = p.Datasets.Sum(d => d.Saved);
        double dedupRatio = Ratio(p.Pool.DedupRatio);
        long dedup = dedupRatio > 1 ? (long)(used * (dedupRatio - 1)) : 0;
        long clone = p.BlockCloneSaved ?? 0;
        long total = compression + dedup + clone;
        long scale = Math.Max(1, used + total);
        string? root = p.Datasets.FirstOrDefault(d => d.Name == p.Pool.Name)?.CompressRatio;
        h.Append("<div class=\"card\"><h3>Space saved</h3><div class=\"big\">").Append(Bytes(total)).Append(" <small>saved in total</small></div><div class=\"bars\">");
        Bar(h, $"Compression{(root is null ? "" : $" ({E(root)}x)")}", Bytes(compression), (double)compression / scale, "c1");
        Bar(h, $"Deduplication ({E((p.Pool.DedupRatio ?? "1.00").TrimEnd('x') + "x")})", "about " + Bytes(dedup), (double)dedup / scale, "c2");
        Bar(h, "Block cloning", Bytes(clone), (double)clone / scale, "c3");
        h.Append("</div></div>");

        // Files and folders.
        long files = p.Counts.Values.Sum(c => c.Files ?? 0), folders = p.Counts.Values.Sum(c => c.Folders ?? 0);
        long? objects = p.Counts.Values.Any(c => c.Objects is not null) ? p.Counts.Values.Sum(c => c.Objects ?? 0) : null;
        bool counted = p.Counts.Values.Any(c => c.Files is not null);
        h.Append("<div class=\"card\"><h3>Files and folders</h3><div class=\"row\">");
        if (counted)
        {
            h.Append("<div><div class=\"big\">").Append(files.ToString("N0", En)).Append("</div><span class=\"mut\">files</span></div>");
            h.Append("<div><div class=\"big\">").Append(folders.ToString("N0", En)).Append("</div><span class=\"mut\">folders</span></div>");
        }
        else
        {
            h.Append("<div class=\"mut\">Not counted yet (Count now).</div>");
        }
        h.Append("</div>");
        if (objects is long o)
            h.Append("<p class=\"mut\">").Append(o.ToString("N0", En)).Append(" objects (files + folders + internal)</p>");
        if (p.Counts.Values.Where(c => c.Files is not null).Select(c => c.CountedAt).DefaultIfEmpty().Min() is DateTimeOffset when && when != default)
            h.Append("<p class=\"mut\">counted ").Append(E(Ago(when))).Append("</p>");
        h.Append("<table><tr><th>Dataset</th><th class=\"num\">Used</th><th class=\"num\">Saved</th><th class=\"num\">Files</th><th class=\"num\">Folders</th></tr>");
        foreach (DatasetInfo d in p.Datasets)
        {
            p.Counts.TryGetValue(d.Name, out DatasetCountInfo? c);
            h.Append("<tr><td>").Append(E(d.Name)).Append("</td><td class=\"num\">").Append(Bytes(d.Used ?? 0))
                .Append("</td><td class=\"num\">").Append(Bytes(d.Saved)).Append("</td><td class=\"num\">")
                .Append(c?.Files is long cf ? cf.ToString("N0", En) : "–").Append("</td><td class=\"num\">")
                .Append(c?.Folders is long cd ? cd.ToString("N0", En) : "–").Append("</td></tr>");
        }
        h.Append("</table></div>");
        if (p.Redundancy is RedundancyInfo rd)
            h.Append("<div class=\"card wide ").Append(rd.Warning ? "warnbox" : "").Append("\"><h3>Copies of your data</h3><p>").Append(E(rd.Text)).Append("</p></div>");
        h.Append("</section>");
    }

    static void Memory(StringBuilder h, MemoryInfo m)
    {
        h.Append("<section class=\"pool\"><div class=\"card wide\"><h3>ZFS memory</h3>");
        if (m.Arc is not ArcInfo a)
        {
            h.Append("<p class=\"mut\">ARC statistics are not available.</p></div></section>");
            return;
        }
        h.Append("<div class=\"row\">");
        Stat(h, "ARC size", $"<div class=\"big\">{Bytes(a.Size ?? 0)}</div>");
        Stat(h, "Target / max", $"<div>{Bytes(a.Target ?? 0)} / {Bytes(a.Max ?? 0)}</div>");
        Stat(h, "Hit ratio", $"<div>{(a.HitRatio is double hr ? Pct(hr) : "–")}</div>");
        if (a.L2Size is > 0)
            Stat(h, "L2ARC", $"<div>{Bytes(a.L2Size.Value)}, {(a.L2HitRatio is double l2 ? Pct(l2) : "–")} hits</div>");
        long ddt = m.DedupTables.Sum(t => t.Bytes ?? 0);
        if (ddt > 0)
            Stat(h, "Dedup tables", $"<div>{Bytes(ddt)}</div>");
        if (m.UserModeServicePrivateBytes is long svc)
            Stat(h, "ZfsWinUserMode process", $"<div>{Bytes(svc)} private</div>");
        h.Append("</div>");
        long mru = a.MruSize ?? 0, mfu = a.MfuSize ?? 0, all = Math.Max(1, mru + mfu);
        h.Append("<div class=\"hb\"><i class=\"c1\" style=\"width:").Append(N(100.0 * mru / all)).Append("%\"></i><i class=\"c2\" style=\"width:")
            .Append(N(100.0 * mfu / all)).Append("%\"></i></div><div class=\"legend\"><span><b class=\"c1\"></b>recently used ")
            .Append(Bytes(mru)).Append("</span><span><b class=\"c2\"></b>frequently used ").Append(Bytes(mfu)).Append("</span></div>");
        h.Append("</div></section>");
    }

    static void Stat(StringBuilder h, string title, string valueHtml) =>
        h.Append("<div><h3>").Append(E(title)).Append("</h3>").Append(valueHtml).Append("</div>");

    static void Bar(StringBuilder h, string labelHtml, string value, double fraction, string color) =>
        h.Append("<div><div class=\"bl\"><span>").Append(labelHtml).Append("</span><b>").Append(E(value)).Append("</b></div><div class=\"hb\"><i class=\"")
            .Append(color).Append("\" style=\"width:").Append(N(100 * Math.Clamp(fraction, 0, 1))).Append("%\"></i></div></div>");

    static string Scrub(ScanInfo? s)
    {
        if (s is null)
            return "never";
        string what = s.Function?.ToLowerInvariant() ?? "scan";
        return s.State switch
        {
            "SCANNING" when s.ToExamine is > 0 => $"{what} {(s.Paused ? "paused" : "running")}, {Pct((double)(s.Examined ?? 0) / s.ToExamine.Value)}",
            "FINISHED" => $"{what} finished, {s.Errors ?? 0} errors",
            null => "never",
            _ => $"{what} {s.State.ToLowerInvariant()}",
        };
    }

    static double Ratio(string? text) =>
        double.TryParse(text?.TrimEnd('x', 'X'), NumberStyles.Float, En, out double v) ? v : 1;

    internal static string Ago(DateTimeOffset when)
    {
        TimeSpan t = DateTimeOffset.UtcNow - when;
        return t.TotalMinutes < 1 ? "just now" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min ago" : t.TotalDays < 1 ? $"{(int)t.TotalHours} h ago" : $"{(int)t.TotalDays} d ago";
    }

    public static string Bytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double v = value;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{value.ToString(En)} B" : $"{v.ToString(v < 10 ? "0.0" : "0", En)} {units[i]}";
    }

    static string Pct(double f) => (f * 100).ToString(f < 0.1 ? "0.0" : "0", En) + "%";

    static string N(double v) => v.ToString("0.##", En);

    static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    static string Css(bool dark)
    {
        var (bg, card, line, fg, mut, track) = dark
            ? ("#1c1c1c", "#2b2b2b", "#3a3a3a", "#f0f0f0", "#a8b0bb", "#3a3f46")
            : ("#f6f8fb", "#ffffff", "#e4e8ee", "#1b1b1b", "#5f6b7a", "#e8edf3");
        return $$"""
            :root{--bg:{{bg}};--card:{{card}};--line:{{line}};--fg:{{fg}};--mut:{{mut}};--track:{{track}};--ok:#2e9d4f;--warn:#c27c0e;--bad:#d23b2c;--c1:#2f9e6f;--c2:#7b5fd0;--c3:#d08a2f;--acc:#2f6fd0}
            *{box-sizing:border-box}body{margin:0;padding:14px;background:var(--bg);color:var(--fg);font:13px "Segoe UI",system-ui,sans-serif}
            header{display:flex;gap:12px;align-items:baseline;margin-bottom:10px}h1{font-size:16px;margin:0}
            .edition{padding:2px 10px;border-radius:10px;background:var(--line);font-weight:600;font-size:11px}
            .pool{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:12px;margin-bottom:14px}
            .card{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:12px 14px}.wide{grid-column:1/-1}
            .card h3{margin:0 0 8px;font-size:11px;text-transform:uppercase;letter-spacing:.06em;color:var(--mut);font-weight:600}
            .row{display:flex;gap:24px;align-items:center;flex-wrap:wrap}.big{font-size:24px;font-weight:600}.big small{font-size:12px;font-weight:400;color:var(--mut)}
            .mut{color:var(--mut)}.kv{display:grid;gap:6px}.kv b{display:block;font-size:15px}
            .pill{display:inline-block;padding:3px 12px;border-radius:10px;font-weight:600;color:#fff}.pill.ok{background:var(--ok)}.pill.warn{background:var(--warn)}.pill.bad{background:var(--bad)}
            .okt{color:var(--ok)}.badt{color:var(--bad)}.warnbox{border-color:var(--warn)}
            .track{stroke:var(--track)}.fill{stroke:var(--acc)}.fillwarn{stroke:var(--warn)}.fillbad{stroke:var(--bad)}
            .ringv{font-size:22px;font-weight:600;fill:var(--fg)}.ringl{font-size:11px;fill:var(--mut)}
            .bars{display:grid;gap:8px;margin-top:10px}.bl{display:flex;justify-content:space-between}
            .hb{height:12px;border-radius:6px;background:var(--track);overflow:hidden;display:flex;margin-top:3px}.hb i{display:block;height:100%}
            .c1{background:var(--c1)}.c2{background:var(--c2)}.c3{background:var(--c3)}
            .legend{display:flex;gap:14px;color:var(--mut);font-size:11px;margin-top:6px}.legend b{display:inline-block;width:9px;height:9px;border-radius:2px;margin-right:4px}
            table{border-collapse:collapse;width:100%;margin-top:8px}th{text-align:left;color:var(--mut);font-weight:600;border-bottom:1px solid var(--line);padding:3px 6px}
            td{padding:3px 6px;border-bottom:1px solid var(--line);font-variant-numeric:tabular-nums}.num{text-align:right}
            """;
    }
}
