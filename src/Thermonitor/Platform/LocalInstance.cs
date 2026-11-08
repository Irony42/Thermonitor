namespace Thermonitor.Platform;

/// <summary>Finds a Thermonitor server already listening on this machine.</summary>
internal static class LocalInstance
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromMilliseconds(800),
    };

    public static async Task<bool> PingAsync(string baseUrl)
    {
        try
        {
            var body = await Http.GetStringAsync(baseUrl + "api/ping");
            return body.Contains("Thermonitor", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Asks the server to stop now (its window closed). False when it could not be reached.</summary>
    public static async Task<bool> RequestQuitAsync(string baseUrl)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "api/quit");
            req.Headers.Add("X-Thermonitor", "1");
            using var res = await Http.SendAsync(req);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> WaitForAsync(string baseUrl, TimeSpan max)
    {
        var deadline = Environment.TickCount64 + (long)max.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (await PingAsync(baseUrl)) return true;
            await Task.Delay(300);
        }
        return false;
    }
}
