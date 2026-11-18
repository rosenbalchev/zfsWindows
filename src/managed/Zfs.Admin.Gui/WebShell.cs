using System.Reflection;

namespace Zfs.Admin.Gui;

/// <summary>
/// The web app's files, embedded in ZfsWinGui.exe and served to WebView2
/// from a private origin that never reaches the network: WebView2 answers
/// every request under <see cref="Origin"/> from these resources, and the
/// window refuses to navigate anywhere else.
/// </summary>
static class WebShell
{
    /// <summary>".example" is reserved (RFC 2606): no real host can answer for it.</summary>
    public const string Origin = "https://zfswin-app.example";

    public const string StartPage = Origin + "/index.html";

    /// <summary>Only this app's own files: no other origin, no inline script, no network calls.</summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'none'; " +
        "frame-ancestors 'none'; form-action 'none'; base-uri 'none'";

    static readonly Assembly Assembly = typeof(WebShell).Assembly;

    /// <summary>The embedded file a URL under <see cref="Origin"/> names, or null for anything else.</summary>
    public static (byte[] Content, string ContentType)? Resolve(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? u) || !IsOwnOrigin(u))
            return null;
        string path = u.AbsolutePath.TrimStart('/');
        if (path.Length == 0)
            path = "index.html";
        if (path.Contains("..", StringComparison.Ordinal) || path.Contains('\\'))
            return null;
        // The colour tokens come from the one palette the dashboard and the report use.
        if (path == "theme.css")
            return (System.Text.Encoding.UTF8.GetBytes(Zfs.Admin.WebTheme.Tokens(null)), "text/css; charset=utf-8");
        using Stream? stream = Assembly.GetManifestResourceStream("web/" + path);
        if (stream is null)
            return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        string type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
        return (buffer.ToArray(), type);
    }

    public static bool IsOwnOrigin(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out Uri? u) && IsOwnOrigin(u);

    static bool IsOwnOrigin(Uri u) =>
        u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.Host.Equals(new Uri(Origin).Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>The response headers for a served file.</summary>
    public static string Headers(string contentType) =>
        $"Content-Type: {contentType}\r\nContent-Security-Policy: {ContentSecurityPolicy}\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store";
}
