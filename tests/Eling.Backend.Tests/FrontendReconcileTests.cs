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
        var action = FrontendReconcile.Decide(portListening: false, responding: false);

        Assert.Equal(FrontendAction.Start, action);
    }

    [Fact]
    public void Decide_WhenListeningButNotResponding_RestartsFrontend()
    {
        var action = FrontendReconcile.Decide(portListening: true, responding: false);

        Assert.Equal(FrontendAction.Restart, action);
    }

    [Fact]
    public void Decide_WhenListeningAndResponding_LeavesFrontendAlone()
    {
        var action = FrontendReconcile.Decide(portListening: true, responding: true);

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
}