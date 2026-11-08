using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Thermonitor.Platform;

namespace Thermonitor.App;

/// <summary>
/// The Thermonitor window: a native window (own taskbar button and icon) showing the page the
/// local server serves, through WebView2 (the Edge engine built into Windows). Never elevated:
/// an elevated launch hands the window to an un-elevated copy (see <see cref="WindowPlan.Unelevated"/>).
/// </summary>
internal sealed class MainWindow : Form
{
    private static readonly Color PageDark = Color.FromArgb(13, 13, 13);   // --page in dark theme: no white flash
    private readonly WebView2 _web;
    private readonly Uri _url;
    private readonly WindowBounds _place;

    public MainWindow(string url)
    {
        _url = new Uri(url);
        Text = "Thermonitor";
        Icon = LoadIcon();
        BackColor = PageDark;

        // First launch: centred on the screen the user clicked from (where the mouse is).
        var target = Screen.FromPoint(Cursor.Position).WorkingArea;
        var scale = Win32.ScaleAt(new Point(target.X + target.Width / 2, target.Y + target.Height / 2));
        var screens = Screen.AllScreens.Select(s => s.WorkingArea).ToList();
        _place = WindowPlacement.Fit(WindowPlacement.Load(AppPaths.WindowFile), screens, target, scale);
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(_place.X, _place.Y, _place.Width, _place.Height);
        if (_place.Maximized) WindowState = FormWindowState.Maximized;

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = PageDark };
        Controls.Add(_web);
    }

    /// <summary>WebView2 could not show the page: the caller opens the browser instead.</summary>
    public bool WebViewFailed { get; private set; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Win32.SetDarkTitleBar(Handle, Win32.AppsUseDarkTheme());
        MinimumSize = new Size(WindowPlacement.MinWidth * DeviceDpi / 96, WindowPlacement.MinHeight * DeviceDpi / 96);
    }

    // Windows may move a new window to the monitor the launch came from: put it where it belongs.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (WindowState != FormWindowState.Normal || Location == new Point(_place.X, _place.Y)) return;
        Log.Write($"window moved by Windows to {Location}, restoring {_place.X},{_place.Y}");
        Bounds = new Rectangle(_place.X, _place.Y, _place.Width, _place.Height);
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: AppPaths.WebViewData);
            if (IsDisposed) return;   // closed while WebView2 was starting
            await _web.EnsureCoreWebView2Async(env);
            if (IsDisposed) return;
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += (_, a) => a.Handled = true;    // the page never opens pop-ups
            core.NavigationStarting += (_, a) => a.Cancel = !IsOurs(a.Uri);   // stay on the local server
            core.WebMessageReceived += OnPageMessage;
            core.ProcessFailed += OnProcessFailed;
            _web.Source = _url;
        }
        catch (Exception ex) when (!IsDisposed)
        {
            Log.Write("webview2 failed to start: " + ex);
            WebViewFailed = true;   // Program falls back to the browser window
            Close();
        }
        catch (Exception ex)
        {
            Log.Write("webview2 start interrupted by closing: " + ex.Message);
        }
    }

    private bool IsOurs(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u)
        && Uri.Compare(u, _url, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;

    // A crashed or hung page process would leave a dead window: reload it.
    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Log.Write($"webview2 process failed: {e.ProcessFailedKind} ({e.Reason})");
        if (IsDisposed) return;
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            _web.Reload();
    }

    // The page reports its theme ({ "theme": "dark" | "light" }), so the title bar follows the in-page switch.
    private void OnPageMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!IsOurs(e.Source)) return;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("theme", out var theme)
                && theme.ValueKind == JsonValueKind.String)
                Win32.SetDarkTitleBar(Handle, theme.GetString() == "dark");
        }
        catch (JsonException)
        {
            // not a message for us
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        WindowPlacement.Save(AppPaths.WindowFile, new WindowBounds(b.X, b.Y, b.Width, b.Height, WindowState == FormWindowState.Maximized));
        base.OnFormClosing(e);
    }

    // The embedded .ico holds every size, so Windows picks an exact one for the taskbar (24 px at 100 %).
    private static Icon? LoadIcon()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("thermonitor.ico");
        return stream is null ? null : new Icon(stream);
    }
}
