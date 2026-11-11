using Eling.Backend.Shell;
using Xunit;

namespace Eling.Backend.Tests.Shell;

/// <summary>
/// The root-protection guardrail is the last thing standing between a
/// mistaken command and an erased disk, so it is pinned from both sides: the
/// catastrophic commands must be refused, and the ordinary destructive ones
/// must not be. A guardrail that blocks <c>rm -rf bin/</c> gets switched off,
/// and a guardrail that lets <c>rm -rf /</c> through is worse than none.
/// </summary>
public sealed class RootProtectionGuardrailTests
{
    /// <summary>
    /// The destructive commands from the design spec (section 3.2): a
    /// recursive delete of the POSIX root, a recursive removal or deletion of
    /// a Windows drive root, and a whole-volume format.
    /// </summary>
    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("rm -rf /*")]
    [InlineData("rm -r /")]
    [InlineData("rd /s /q C:\\")]
    [InlineData("rmdir /s C:")]
    [InlineData("del /s C:\\")]
    [InlineData("del /s /q D:")]
    [InlineData("format D:")]
    public void Guardrail_BlocksDestructiveRootCommands(string command)
    {
        Assert.True(RootProtectionGuardrail.IsBlocked(command));
        Assert.NotNull(RootProtectionGuardrail.BlockedReason(command));
    }

    /// <summary>
    /// The ordinary destructive commands: cleaning a build output directory,
    /// clearing a scratch folder, removing one file. None of them names a
    /// root, so none of them may be blocked.
    /// </summary>
    [Theory]
    [InlineData("rm -rf bin/")]
    [InlineData("rm -rf ./obj")]
    [InlineData("rd /s bin")]
    [InlineData("git clean -fdx")]
    [InlineData("del temp.txt")]
    public void Guardrail_AllowsOrdinaryDestructiveCommands(string command)
    {
        Assert.False(RootProtectionGuardrail.IsBlocked(command));
        Assert.Null(RootProtectionGuardrail.BlockedReason(command));
    }

    /// <summary>
    /// The same catastrophic commands as they are actually typed: with a
    /// prefix, in another case, with the flags in another order, with the
    /// root quoted, and with the Windows system directory variables.
    /// </summary>
    [Theory]
    [InlineData("sudo rm -rf /")]
    [InlineData("RM -RF /")]
    [InlineData("rm -fr /")]
    [InlineData("rm -rf -- /")]
    [InlineData("rm -rf \"/\"")]
    [InlineData("rm -rf /tmp /")]
    [InlineData("rd /s /q \"C:\\\"")]
    [InlineData("del /s C:/")]
    [InlineData("del /s %SystemRoot%")]
    [InlineData("rd /s %SystemDrive%")]
    [InlineData("format c:")]
    public void Guardrail_BlocksRootTargetsInTheirCommonVariants(string command)
    {
        Assert.True(RootProtectionGuardrail.IsBlocked(command));
        Assert.NotNull(RootProtectionGuardrail.BlockedReason(command));
    }

    /// <summary>
    /// The spellings cmd.exe accepts and that a reviewer flagged as false
    /// negatives: a glob right after the drive root, a switch written without
    /// a space in front of it, and a recursive flag written after the target.
    /// </summary>
    [Theory]
    // A glob at the drive root is the drive root: C:\* expands to the root's
    // own entries.
    [InlineData("del /s /q C:\\*")]
    [InlineData("del /s C:\\*.*")]
    // cmd.exe accepts a switch written without a space in front of it, and
    // executes it.
    [InlineData("rd/s /q C:\\")]
    [InlineData("rd/s/q C:")]
    [InlineData("del/s C:\\")]
    [InlineData("format/q D:")]
    // GNU rm permutes its options, so the recursive flag may follow the
    // target.
    [InlineData("rm / -rf")]
    [InlineData("rm /* -rf")]
    [InlineData("rm / --recursive --force")]
    public void Guardrail_BlocksRootTargetsInTheirCmdSpellings(string command)
    {
        Assert.True(RootProtectionGuardrail.IsBlocked(command));
        Assert.NotNull(RootProtectionGuardrail.BlockedReason(command));
    }

    /// <summary>
    /// Relaxing the space in front of a switch must not turn a longer word
    /// into the verb: <c>rdx</c>, <c>delx</c> and <c>formatter</c> are not
    /// <c>rd</c>, <c>del</c> and <c>format</c>.
    /// </summary>
    [Theory]
    [InlineData("rdx /s C:\\")]
    [InlineData("delx /s C:\\")]
    [InlineData("formatter/q D:")]
    public void Guardrail_DoesNotReadALongerWordAsTheVerb(string command)
    {
        Assert.False(RootProtectionGuardrail.IsBlocked(command));
        Assert.Null(RootProtectionGuardrail.BlockedReason(command));
    }

    /// <summary>
    /// Targets below the root are ordinary paths, not roots - and neither is a
    /// command that merely mentions the word "format".
    /// </summary>
    [Theory]
    [InlineData("rm -rf /tmp")]
    [InlineData("rm -rf /var/log")]
    [InlineData("rm -rf build/out")]
    [InlineData("rd /s /q C:\\Users")]
    [InlineData("del /s C:\\temp")]
    [InlineData("del /s *.log")]
    [InlineData("dotnet format --verify-no-changes")]
    [InlineData("git log --format:%h")]
    [InlineData("npm run format")]
    [InlineData("echo done")]
    public void Guardrail_AllowsTargetsBelowTheRoot(string command)
    {
        Assert.False(RootProtectionGuardrail.IsBlocked(command));
        Assert.Null(RootProtectionGuardrail.BlockedReason(command));
    }

    /// <summary>
    /// The reason reaches the agent unchanged, so it has to say which rule
    /// fired - an agent that is told "rm" can retry with a different tool, an
    /// agent that is told "dangerous" cannot.
    /// </summary>
    [Theory]
    [InlineData("rm -rf /", "rm")]
    [InlineData("rd /s /q C:\\", "rd")]
    [InlineData("rmdir /s C:", "rmdir")]
    [InlineData("del /s C:\\", "del")]
    [InlineData("format D:", "format")]
    public void BlockedReason_NamesTheRuleThatMatched(string command, string expectedTool)
    {
        var reason = RootProtectionGuardrail.BlockedReason(command);

        Assert.NotNull(reason);
        Assert.Contains(expectedTool, reason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Two different rules must not report the same reason, otherwise the
    /// agent cannot tell what it tripped over.
    /// </summary>
    [Fact]
    public void BlockedReason_DiffersBetweenRules()
    {
        var deleteReason = RootProtectionGuardrail.BlockedReason("rm -rf /");
        var formatReason = RootProtectionGuardrail.BlockedReason("format D:");

        Assert.NotNull(deleteReason);
        Assert.NotNull(formatReason);
        Assert.NotEqual(deleteReason, formatReason);
    }

    /// <summary>
    /// An allowed command has no reason at all - the tool only ever shows the
    /// reason when there is something to show.
    /// </summary>
    [Fact]
    public void BlockedReason_ForAnAllowedCommand_IsNull()
    {
        Assert.Null(RootProtectionGuardrail.BlockedReason("git status"));
    }

    /// <summary>
    /// An empty command is not a destructive one; the tool rejects it as an
    /// invalid argument before the guardrail ever sees it.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Guardrail_AllowsAnEmptyCommand(string command)
    {
        Assert.False(RootProtectionGuardrail.IsBlocked(command));
        Assert.Null(RootProtectionGuardrail.BlockedReason(command));
    }
}
