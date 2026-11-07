namespace Thermonitor.Platform;

/// <summary>Text log in %LOCALAPPDATA%\Thermonitor (the app has no console), rotated at 1 MB.</summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private const int CheckSizeEvery = 200;
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Thermonitor", "thermonitor.log");
    private static readonly object Gate = new();
    private static int _writes;

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            lock (Gate) RotateIfBig();
        }
        catch
        {
            // logging is best effort
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                if (++_writes % CheckSizeEvery == 0) RotateIfBig();
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging is best effort
        }
    }

    // Keeps one previous file (.log.1), so at most 2 MB on disk.
    private static void RotateIfBig()
    {
        var info = new FileInfo(FilePath);
        if (info.Exists && info.Length > MaxBytes) File.Move(FilePath, FilePath + ".1", overwrite: true);
    }
}
