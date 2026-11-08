using System.Text;
using System.Text.RegularExpressions;
using Thermonitor.Web;

namespace Thermonitor.Tests;

public partial class StaticFilesTests
{
    private static readonly StaticFiles Files = new(typeof(StaticFiles).Assembly);

    [Fact]
    public void The_whole_page_is_embedded()
    {
        Assert.Superset(
            new HashSet<string> { "/index.html", "/app.css", "/js/app.js", "/js/charts.js", "/js/domain.js", "/js/feed.js", "/js/store.js", "/img/logo.png", "/img/mark.png" },
            Files.Paths.ToHashSet());
    }

    [Fact]
    public void Root_serves_the_page()
    {
        Assert.True(Files.TryGet("/", out var body, out var type));
        Assert.Equal("text/html; charset=utf-8", type);
        Assert.Contains("<title>Thermonitor</title>", Encoding.UTF8.GetString(body));
    }

    [Theory]
    [InlineData("/nope.js")]
    [InlineData("/../Thermonitor.dll")]
    [InlineData("/js/../../secret")]
    [InlineData("/INDEX.HTML")]
    public void Unknown_paths_are_not_served(string path) =>
        Assert.False(Files.TryGet(path, out _, out _));

    [Theory]
    [InlineData("/a.html", "text/html; charset=utf-8")]
    [InlineData("/a.css", "text/css; charset=utf-8")]
    [InlineData("/a.js", "text/javascript; charset=utf-8")]
    [InlineData("/a.svg", "image/svg+xml")]
    [InlineData("/img/mark.png", "image/png")]
    [InlineData("/a.bin", "application/octet-stream")]
    public void Content_type_follows_the_extension(string path, string expected) =>
        Assert.Equal(expected, StaticFiles.ContentType(path));

    [Fact]
    public void Dev_root_overrides_embedded_files_but_not_the_allow_list()
    {
        var dir = Directory.CreateTempSubdirectory("thermonitor-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "index.html"), "dev copy");
            File.WriteAllText(Path.Combine(dir.FullName, "extra.html"), "not embedded");
            var files = new StaticFiles(typeof(StaticFiles).Assembly, dir.FullName);

            Assert.True(files.TryGet("/index.html", out var body, out _));
            Assert.Equal("dev copy", Encoding.UTF8.GetString(body));
            Assert.False(files.TryGet("/extra.html", out _, out _));
            Assert.True(files.TryGet("/app.css", out _, out _));   // missing on disk: embedded copy
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // Catches a broken page after moving files around: every local reference must be embedded.
    [Fact]
    public void Every_local_reference_in_the_page_exists()
    {
        var missing = new List<string>();
        foreach (var path in Files.Paths.Where(p => p.EndsWith(".html") || p.EndsWith(".js")))
        {
            Files.TryGet(path, out var body, out _);
            var dir = path[..(path.LastIndexOf('/') + 1)];
            foreach (Match m in LocalRef().Matches(Encoding.UTF8.GetString(body)))
            {
                var target = new Uri(new Uri("http://x" + dir), m.Groups["ref"].Value).AbsolutePath;
                if (!Files.Paths.Contains(target)) missing.Add($"{path} -> {target}");
            }
        }
        Assert.Empty(missing);
    }

    [GeneratedRegex("""(?:src|href)="(?<ref>(?!data:|https?:|#)[^"]+)"|from '(?<ref>\.[^']+)'""")]
    private static partial Regex LocalRef();
}
