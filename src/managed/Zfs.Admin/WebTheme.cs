namespace Zfs.Admin;

/// <summary>
/// The colour tokens every web page of the GUI uses: the Overview dashboard
/// (and the report saved from it) and the other tabs in WebView2.  One
/// palette, so the pages and the report look like one application.
/// </summary>
public static class WebTheme
{
    const string Light =
        "--bg:#f3f5f8;--card:#ffffff;--sunk:#eaeef3;--line:#d9e0e8;--track:#e6ebf1;--fg:#17202b;--mut:#5b6776;" +
        "--accent:#0b6e8f;--accent-fg:#ffffff;--accent-soft:#dcf0f6;" +
        "--ok:#137a3a;--ok-soft:#dff3e5;--warn:#8f5a00;--warn-soft:#fff1cf;--bad:#b3261e;--bad-soft:#fde4e2;" +
        "--c1:#1f8a63;--c2:#6f55c9;--c3:#c47a1d;--read:#0b6e8f;--write:#c47a1d;color-scheme:light";

    const string Dark =
        "--bg:#12161c;--card:#1b2129;--sunk:#151a21;--line:#2e3742;--track:#2a323c;--fg:#e8edf3;--mut:#9aa6b4;" +
        "--accent:#4fb6d6;--accent-fg:#06222c;--accent-soft:#16323d;" +
        "--ok:#5fcf8a;--ok-soft:#173326;--warn:#f0b54a;--warn-soft:#3a2c10;--bad:#ff8a80;--bad-soft:#3d1c1a;" +
        "--c1:#4cc597;--c2:#a48ef0;--c3:#e5a14a;--read:#4fb6d6;--write:#e5a14a;color-scheme:dark";

    /// <summary>The body font, shipped with Windows: the pages' CSP loads no web fonts.</summary>
    public const string Font = "13px \"Segoe UI Variable Text\",\"Segoe UI\",system-ui,sans-serif";

    /// <summary>
    /// The <c>:root</c> rules.  <paramref name="dark"/> fixes the theme (a file
    /// opened in a browser follows the Windows setting it was saved with);
    /// null follows <c>prefers-color-scheme</c>, which WebView2 takes from Windows.
    /// </summary>
    public static string Tokens(bool? dark) => dark switch
    {
        true => $":root{{{Dark}}}",
        false => $":root{{{Light}}}",
        null => $":root{{{Light}}}@media(prefers-color-scheme:dark){{:root{{{Dark}}}}}",
    };
}
