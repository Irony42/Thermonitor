using System.Reflection;

namespace Thermonitor.Web;

/// <summary>
/// The page (wwwroot), embedded in the exe. With a dev root set, the same files are read
/// from disk instead, so the page can be edited without rebuilding.
/// </summary>
internal sealed class StaticFiles
{
    private const string Prefix = "wwwroot/";
    private readonly Dictionary<string, byte[]> _embedded = new(StringComparer.Ordinal);
    private readonly string? _devRoot;

    public StaticFiles(Assembly assembly, string? devRoot = null)
    {
        _devRoot = devRoot;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            _embedded["/" + normalized[Prefix.Length..]] = ms.ToArray();
        }
    }

    public IReadOnlyCollection<string> Paths => _embedded.Keys;

    /// <summary>Only paths that exist in the embedded set are served, even from the dev root.</summary>
    public bool TryGet(string urlPath, out byte[] body, out string contentType)
    {
        var path = urlPath == "/" ? "/index.html" : urlPath;
        contentType = ContentType(path);
        if (!_embedded.TryGetValue(path, out body!)) return false;

        if (_devRoot is not null)
        {
            var onDisk = Path.Combine(_devRoot, path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(onDisk)) body = File.ReadAllBytes(onDisk);
        }
        return true;
    }

    internal static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };
}
