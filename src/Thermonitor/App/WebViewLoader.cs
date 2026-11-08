using System.Security.Cryptography;
using Microsoft.Web.WebView2.Core;
using Thermonitor.Platform;

namespace Thermonitor.App;

/// <summary>
/// Makes WebView2 usable from the single-file exe, and tells whether it can be used at all.
/// </summary>
internal static class WebViewLoader
{
    private const string LoaderName = "WebView2Loader.dll";

    /// <summary>
    /// Extracts the embedded native loader to %LOCALAPPDATA%\Thermonitor\bin\&lt;hash&gt;, checks it and
    /// points WebView2 at it, then checks that the WebView2 runtime is installed. False means: use the browser.
    /// </summary>
    public static bool TryPrepare()
    {
        try
        {
            var dir = ExtractLoader();
            if (dir is not null) CoreWebView2Environment.SetLoaderDllFolderPath(dir);
            return CoreWebView2Environment.GetAvailableBrowserVersionString() is not null;
        }
        catch (Exception ex)
        {
            // Runtime missing, loader blocked by antivirus...: the browser window still works.
            Log.Write("WebView2 unavailable: " + ex.Message);
            return false;
        }
    }

    private static string? ExtractLoader()
    {
        using var resource = typeof(WebViewLoader).Assembly.GetManifestResourceStream(LoaderName);
        if (resource is null) return null;   // plain (non single-file) build: the loader sits in runtimes\
        using var ms = new MemoryStream();
        resource.CopyTo(ms);
        var bytes = ms.ToArray();
        var hash = SHA256.HashData(bytes);

        // One folder per loader version: never overwrite a DLL another running instance has loaded.
        var dir = Path.Combine(AppPaths.Root, "bin", Convert.ToHexString(hash)[..16]);   // not under WebView2\ (the browser profile)
        var path = Path.Combine(dir, LoaderName);
        if (IsIntact(path, hash)) return dir;

        Directory.CreateDirectory(dir);
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another instance extracted (and loaded) it at the same time: fine if it is the right file.
            if (!IsIntact(path, hash)) throw;
        }
        finally
        {
            File.Delete(tmp);
        }
        // Checked again right before use: a DLL anyone in this session could have replaced is never loaded blindly.
        if (!IsIntact(path, hash)) throw new InvalidDataException($"{path} does not match the embedded loader");
        return dir;
    }

    private static bool IsIntact(string path, byte[] expectedHash)
    {
        try
        {
            using var file = File.OpenRead(path);
            return SHA256.HashData(file).AsSpan().SequenceEqual(expectedHash);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
