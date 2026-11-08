using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Thermonitor.Platform;

namespace Thermonitor.App;

/// <summary>
/// The Thermonitor window: a native window (own taskbar button and icon) showing the page the
/// local server serves, through WebView2 (the Edge engine built into Windows). Never elevated.
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
        MinimumSize = new Size(WindowPlacement.MinWidth, WindowPlacement.MinHeight);

        // First launch: centred on the screen the user clicked from (where the mouse is).
        var screens = Screen.AllScreens.Select(s => s.WorkingArea).ToList();
        _place = WindowPlacement.Fit(WindowPlacement.Load(AppPaths.WindowFile), screens, Screen.FromPoint(Cursor.Position).WorkingArea);
        StartPosition = FormStartPosition.Manual;
        Bounds = new Rectangle(_place.X, _place.Y, _place.Width, _place.Height);
        if (_place.Maximized) WindowState = FormWindowState.Maximized;

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = PageDark };
        Controls.Add(_web);
    }

    // Windows may move a new window to the monitor the launch came from: put it where it belongs.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (WindowState != FormWindowState.Normal || Location == new Point(_place.X, _place.Y)) return;
        Log.Write($"window moved by Windows to {Location}, restoring {_place.X},{_place.Y}");
        Bounds = new Rectangle(_place.X, _place.Y, _place.Width, _place.Height);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Win32.SetDarkTitleBar(Handle, Win32.AppsUseDarkTheme());
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: AppPaths.WebViewData);
            await _web.EnsureCoreWebView2Async(env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += (_, a) => a.Handled = true;    // the page never opens pop-ups
            core.NavigationStarting += (_, a) =>                       // stay on the local server
            {
                if (!Uri.TryCreate(a.Uri, UriKind.Absolute, out var target) || target.Authority != _url.Authority) a.Cancel = true;
            };
            core.WebMessageReceived += OnPageMessage;
            _web.Source = _url;
        }
        catch (Exception ex)
        {
            Log.Write("webview2: " + ex);
            Dialog.Error("Thermonitor", "La fenêtre n'a pas pu afficher la page (WebView2).\n\n" + ex.Message);
            Close();
        }
    }

    // The page reports its theme, so the title bar follows the in-page light/dark switch.
    private void OnPageMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var from) || from.Authority != _url.Authority) return;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.TryGetProperty("theme", out var theme))
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
