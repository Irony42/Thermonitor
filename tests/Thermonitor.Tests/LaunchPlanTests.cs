using Thermonitor.App;

namespace Thermonitor.Tests;

public class LaunchPlanTests
{
    private static readonly LaunchOptions Normal = LaunchOptions.Parse([]);

    [Fact]
    public void Parse_reads_every_flag_case_insensitively()
    {
        var o = LaunchOptions.Parse(["--SERVER", "--takeover", "--no-window", "--no-elevate", "--Keep-Alive"]);

        Assert.Equal(new LaunchOptions(true, true, true, true, true), o);
    }

    [Fact]
    public void No_browser_is_still_understood_as_no_window()
    {
        Assert.True(LaunchOptions.Parse(["--no-browser"]).NoWindow);
    }

    [Fact]
    public void Elevated_child_args_carry_keep_alive()
    {
        Assert.Equal("--server --no-window", Normal.ElevatedServerArgs);
        Assert.Equal("--server --no-window --keep-alive", (Normal with { KeepAlive = true }).ElevatedServerArgs);
    }

    [Fact]
    public void Normal_launch_without_admin_starts_an_elevated_server_and_a_window_here()
    {
        var plan = LaunchPlan.Decide(Normal, isAdmin: false, serverRunning: false);

        Assert.Equal(new LaunchPlan(ServerPlan.Elevated, WindowPlan.Here, StopServerOnClose: true), plan);
    }

    [Fact]
    public void Keep_alive_never_stops_the_server_when_the_window_closes()
    {
        var plan = LaunchPlan.Decide(Normal with { KeepAlive = true }, isAdmin: false, serverRunning: false);

        Assert.Equal(new LaunchPlan(ServerPlan.Elevated, WindowPlan.Here, StopServerOnClose: false), plan);
    }

    [Fact]
    public void Already_admin_serves_here_but_never_hosts_the_web_view()
    {
        var plan = LaunchPlan.Decide(Normal, isAdmin: true, serverRunning: false);

        Assert.Equal(new LaunchPlan(ServerPlan.InProcess, WindowPlan.Unelevated, StopServerOnClose: false), plan);
    }

    [Fact]
    public void Admin_launch_with_a_running_server_only_opens_an_unelevated_window()
    {
        var plan = LaunchPlan.Decide(Normal, isAdmin: true, serverRunning: true);

        Assert.Equal(new LaunchPlan(ServerPlan.Reuse, WindowPlan.Unelevated, StopServerOnClose: false), plan);
    }

    [Fact]
    public void No_elevate_runs_a_limited_server_and_the_window_here()
    {
        var plan = LaunchPlan.Decide(Normal with { NoElevate = true }, isAdmin: false, serverRunning: false);

        Assert.Equal(new LaunchPlan(ServerPlan.InProcess, WindowPlan.Here, StopServerOnClose: false), plan);
    }

    [Fact]
    public void A_running_server_is_reused_and_left_running()
    {
        var plan = LaunchPlan.Decide(Normal, isAdmin: false, serverRunning: true);

        Assert.Equal(new LaunchPlan(ServerPlan.Reuse, WindowPlan.Here, StopServerOnClose: false), plan);
    }

    [Fact]
    public void The_elevated_child_is_a_server_without_window()
    {
        var plan = LaunchPlan.Decide(Normal with { Server = true, NoWindow = true }, isAdmin: true, serverRunning: false);

        Assert.Equal(new LaunchPlan(ServerPlan.InProcess, WindowPlan.None, StopServerOnClose: false), plan);
    }

    [Fact]
    public void A_server_child_does_nothing_when_a_server_already_runs()
    {
        var plan = LaunchPlan.Decide(Normal with { Server = true }, isAdmin: true, serverRunning: true);

        Assert.Equal(new LaunchPlan(ServerPlan.Reuse, WindowPlan.None, StopServerOnClose: false), plan);
    }

    [Fact]
    public void Takeover_ignores_the_server_that_is_stepping_down()
    {
        var plan = LaunchPlan.Decide(Normal with { Server = true, Takeover = true }, isAdmin: true, serverRunning: true);

        Assert.Equal(ServerPlan.InProcess, plan.Server);
    }

    // As before the window: --no-window without admin still asks UAC for a full (elevated) server.
    [Fact]
    public void No_window_without_admin_still_elevates_the_server()
    {
        var plan = LaunchPlan.Decide(Normal with { NoWindow = true }, isAdmin: false, serverRunning: false);

        Assert.Equal(new LaunchPlan(ServerPlan.Elevated, WindowPlan.None, StopServerOnClose: false), plan);
    }

    [Fact]
    public void No_window_and_no_elevate_run_a_limited_server_here()
    {
        var plan = LaunchPlan.Decide(Normal with { NoWindow = true, NoElevate = true }, isAdmin: false, serverRunning: false);

        Assert.Equal(new LaunchPlan(ServerPlan.InProcess, WindowPlan.None, StopServerOnClose: false), plan);
    }
}
