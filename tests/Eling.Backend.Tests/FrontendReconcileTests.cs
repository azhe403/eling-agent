using Eling.Backend.Bootstrap;

namespace Eling.Backend.Tests;

/// <summary>
/// Covers the reconcile decisions for the frontend dev server on port 4427.
/// </summary>
/// <remarks>
/// This logic is pure so it can be tested without spawning processes or binding
/// ports. What is under test is the policy: a healthy dashboard must never be
/// killed, and repeated failures must escalate so they cannot become a restart
/// loop.
/// </remarks>
public sealed class FrontendReconcileTests
{
    [Fact]
    public void Decide_WhenNothingListening_StartsFrontend()
    {
        var action = FrontendReconcile.Decide(
            portListening: false, responding: false, starting: false, consecutiveSilentProbes: 0);

        Assert.Equal(FrontendAction.Start, action);
    }

    [Fact]
    public void Decide_WhenListeningButNotResponding_RestartsFrontend()
    {
        // Two silent probes in a row is what proves the hang: one slow
        // on-demand compile must not kill a working frontend.
        var action = FrontendReconcile.Decide(
            portListening: true, responding: false, starting: false, consecutiveSilentProbes: 2);

        Assert.Equal(FrontendAction.Restart, action);
    }

    [Fact]
    public void Decide_WhenListeningButNotResponding_OnceOnly_LeavesFrontendAlone()
    {
        // A single silent probe is not proof of a hang - Next.js compiles
        // routes on demand, and the first hit can outlast the probe timeout.
        // Killing here is what made the loop thrash against its own spawn.
        var action = FrontendReconcile.Decide(
            portListening: true, responding: false, starting: false, consecutiveSilentProbes: 1);

        Assert.Equal(FrontendAction.None, action);
    }

    [Fact]
    public void Decide_WhenListeningButNotResponding_DuringStartupGrace_LeavesFrontendAlone()
    {
        // Next.js binds 4427 before it can answer, so a fresh spawn is
        // "listening but silent" for a while. Killing it there is what kept the
        // dashboard from ever coming up.
        var action = FrontendReconcile.Decide(
            portListening: true, responding: false, starting: true, consecutiveSilentProbes: 5);

        Assert.Equal(FrontendAction.None, action);
    }

    [Fact]
    public void Decide_WhenNothingListening_DuringStartupGrace_DoesNotSpawnAgain()
    {
        // A second pnpm would race the first one into EADDRINUSE.
        var action = FrontendReconcile.Decide(
            portListening: false, responding: false, starting: true, consecutiveSilentProbes: 0);

        Assert.Equal(FrontendAction.None, action);
    }

    [Fact]
    public void Decide_WhenHealthy_DuringStartupGrace_LeavesFrontendAlone()
    {
        var action = FrontendReconcile.Decide(
            portListening: true, responding: true, starting: true, consecutiveSilentProbes: 0);

        Assert.Equal(FrontendAction.None, action);
    }

    [Fact]
    public void Decide_WhenListeningAndResponding_LeavesFrontendAlone()
    {
        var action = FrontendReconcile.Decide(
            portListening: true, responding: true, starting: false, consecutiveSilentProbes: 0);

        Assert.Equal(FrontendAction.None, action);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]
    [InlineData(50, 60)]
    public void RestartDelayAfter_EscalatesThenCapsAtSixtySeconds(int consecutiveFailures, int expectedSeconds)
    {
        var delay = FrontendReconcile.RestartDelayAfter(consecutiveFailures);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public void RestartDelayAfter_NoFailures_ReturnsZero()
    {
        var delay = FrontendReconcile.RestartDelayAfter(0);

        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsWatchEnabled_ParsesTheEnvironmentFlagValue(string? raw, bool expected)
    {
        var enabled = FrontendReconcile.IsWatchEnabled(raw);

        Assert.Equal(expected, enabled);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void Decide_RestartsOnlyAfterTwoConsecutiveSilentProbes(
        int consecutiveSilentProbes,
        bool expectRestart)
    {
        // A single silent probe can be a slow on-demand compile, so the kill
        // waits for a second one. The threshold is what separates "still
        // compiling" from "hung".
        var action = FrontendReconcile.Decide(
            portListening: true, responding: false, starting: false, consecutiveSilentProbes);

        Assert.Equal(expectRestart, action == FrontendAction.Restart);
    }

    [Fact]
    public void SilentProbeThreshold_MatchesTheRestartBoundary()
    {
        // The threshold and the policy have to agree: the first silent probe
        // at the threshold must already restart, and the one below it must not.
        var below = FrontendReconcile.Decide(
            portListening: true, responding: false, starting: false,
            consecutiveSilentProbes: FrontendReconcile.SilentProbeThreshold - 1);
        var at = FrontendReconcile.Decide(
            portListening: true, responding: false, starting: false,
            consecutiveSilentProbes: FrontendReconcile.SilentProbeThreshold);

        Assert.Equal(FrontendAction.None, below);
        Assert.Equal(FrontendAction.Restart, at);
    }
}