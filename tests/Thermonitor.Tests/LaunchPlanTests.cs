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
    public void Normal_launch_without_admin_starts_an_elevated_server_and_a_window()
    {
        var plan = LaunchPlan.Decide(Normal, isAdmin: false, serverRunning: false);

        Assert.Equal(new LaunchPlan(ServerPlan.Elevated, Window: true, StopServerOnClose: true), plan);
    }

    [Fact]
    public void Already_admin_runs_everything_in_this_process()
    {
        var plan = LaunchPlan.Decide(Normal, isAdmin: true, serverRunning: false);

        Assert.Equal(new LaunchPlan(ServerPlan.InProcess, Window: true, StopServerOnClose: false), plan);
    }

    [Fact]
    public void No_elevate_runs_a_limited_server_in_this_process()
    {
        var plan = LaunchPlan.Decide(Normal with { NoElevate = true }, isAdmin: false, serverRunning: false);

        Assert.Equal(ServerPlan.InProcess, plan.Server);
        Assert.True(plan.Window);
    }

    [Fact]
    public void A_running_server_is_reused_and_left_running()
    {
        var plan = LaunchPlan.Decide(Normal, isAdmin: false, serverRunning: true);

        Assert.Equal(new LaunchPlan(ServerPlan.Reuse, Window: true, StopServerOnClose: false), plan);
    }

    [Fact]
    public void The_elevated_child_is_a_server_without_window()
    {
        var plan = LaunchPlan.Decide(Normal with { Server = true }, isAdmin: true, serverRunning: false);

        Assert.Equal(new LaunchPlan(ServerPlan.InProcess, Window: false, StopServerOnClose: false), plan);
    }

    [Fact]
    public void A_server_child_does_nothing_when_a_server_already_runs()
    {
        var plan = LaunchPlan.Decide(Normal with { Server = true }, isAdmin: true, serverRunning: true);

        Assert.Equal(ServerPlan.Reuse, plan.Server);
        Assert.False(plan.Window);
    }

    [Fact]
    public void Takeover_ignores_the_server_that_is_stepping_down()
    {
        var plan = LaunchPlan.Decide(Normal with { Server = true, Takeover = true }, isAdmin: true, serverRunning: true);

        Assert.Equal(ServerPlan.InProcess, plan.Server);
    }

    [Fact]
    public void No_window_runs_the_server_here_without_elevating()
    {
        var plan = LaunchPlan.Decide(Normal with { NoWindow = true }, isAdmin: false, serverRunning: false);

        Assert.Equal(new LaunchPlan(ServerPlan.InProcess, Window: false, StopServerOnClose: false), plan);
    }
}
