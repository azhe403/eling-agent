using System.Text.RegularExpressions;

namespace Eling.Backend.Shell;

/// <summary>
/// Refuses command lines that would destroy a filesystem root, a whole drive,
/// or a system directory.
/// </summary>
/// <remarks>
/// <para>
/// This is a pattern matcher over the command text: it never executes
/// anything, and it never expands an environment variable. It is deliberately
/// one-sided - a command that names a root target is refused, while an
/// ordinary destructive command (removing a build output directory, for
/// instance) is left alone, because a guardrail that cries wolf gets switched
/// off.
/// </para>
/// <para>
/// The rule set follows the design spec (section 3.2). It is not a complete
/// model of "dangerous": commands outside it - <c>dd of=/dev/sda</c>,
/// <c>mkfs</c>, PowerShell's <c>Remove-Item -Recurse</c>, a literal
/// <c>C:\Windows</c> - are what the user's own blocked-command rules are for,
/// not this guardrail's.
/// </para>
/// </remarks>
public static class RootProtectionGuardrail
{
    /// <summary>
    /// The options every pattern uses: matching is case-insensitive, the
    /// patterns are compiled once when the type is initialized, and matching
    /// does not depend on the current culture.
    /// </summary>
    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    /// <summary>
    /// The start of an ASCII word. Written out instead of using <c>\b</c>,
    /// because .NET's <c>\b</c> follows Unicode word characters: a non-ASCII
    /// letter in front of a command name would then decide whether the name is
    /// recognized, which is not a decision a guardrail should leave to the
    /// alphabet.
    /// </summary>
    private const string WordStart = @"(?<![A-Za-z0-9_])";

    /// <summary>The end of an ASCII word; the counterpart of <see cref="WordStart"/>.</summary>
    private const string WordEnd = @"(?![A-Za-z0-9_])";

    /// <summary>
    /// A POSIX root argument - <c>/</c>, <c>/*</c> or <c>/.*</c> - optionally
    /// quoted. The trailing lookahead is what keeps <c>/tmp</c> and
    /// <c>/var</c> out of the rule: only an argument that <i>ends</i> at the
    /// root token counts, so a path below the root stays an ordinary path.
    /// </summary>
    private const string PosixRootArgument = @"[""']?/(?:\*|\.\*)?[""']?(?![^\s;&|""'])";

    /// <summary>
    /// A Windows drive root - <c>C:</c> or <c>C:\</c> - optionally quoted. The
    /// lookbehind keeps the tail of a URL (<c>https://</c>) from reading as a
    /// drive; the trailing lookahead keeps <c>C:\Windows</c>, a directory below
    /// the root rather than the root itself, out of the rule.
    /// </summary>
    /// <remarks>
    /// The lookahead also accepts a glob character right after the root,
    /// because <c>C:\*</c> <i>is</i> the drive root as a target - it expands to
    /// the root's own entries. <c>C:\temp</c> and <c>C:\Users\Public\*.txt</c>
    /// still do not match, because their next character is neither a glob nor
    /// a boundary.
    /// </remarks>
    private const string DriveRootArgument = @"(?<![A-Za-z0-9_])[""']?[a-zA-Z]:(?:[\\/])?(?=[ \t;&|""'*?]|$)";

    /// <summary>
    /// An environment variable that expands to a system directory root.
    /// </summary>
    private const string SystemRootArgument = @"(?<![A-Za-z0-9_%])%(?:SystemDrive|SystemRoot|WinDir)%(?![^\s""'\\/])";

    /// <summary>
    /// Any Windows root target: a drive root, or one of the system directory
    /// environment variables.
    /// </summary>
    private const string WindowsRootArgument = "(?:" + DriveRootArgument + "|" + SystemRootArgument + ")";

    /// <summary>
    /// <c>rm</c> carrying a recursive flag, with the filesystem root among its
    /// targets: <c>rm -rf /</c>, <c>rm -r /*</c>. The flag is matched loosely
    /// (any flag token holding an <c>r</c>) but the target is matched strictly,
    /// so the rule fires on the root and on nothing else.
    /// </summary>
    /// <remarks>
    /// The two lookaheads are independent: one asks for a recursive flag
    /// somewhere in the argument list, the other for a root operand somewhere
    /// in it. They have to be independent because GNU <c>rm</c> permutes its
    /// options - <c>rm / -rf</c> is the same recursive delete of the root as
    /// <c>rm -rf /</c>, and a pattern that only accepted the flag first missed
    /// it. Both lookaheads walk whole argument tokens, so the root operand is
    /// only ever recognized at the start of a token.
    /// </remarks>
    private static readonly Regex PosixRootDelete = new(
        WordStart + "rm" + WordEnd
        + @"[ \t]+"
        + @"(?=[ \t]*(?:[^\s;&|]+[ \t]+)*-[^\s;&|]*[rR][^\s;&|]*)"
        + @"(?=[ \t]*(?:[^\s;&|]+[ \t]+)*" + PosixRootArgument + ")",
        Options);

    // The two Windows recursive-delete rules differ only in the verb; sharing
    // the builder means a delta (e.g. /s ordering) is applied in one place.
    private static Regex WindowsRecursive(string verb) => new(
        WordStart + verb + WordEnd
        + @"(?=[ \t]*[^;&\r\n|]*?" + WindowsRootArgument + ")"
        + @"(?=[ \t]*[^;&\r\n|]*?/[sS](?![a-zA-Z]))",
        Options);

    /// <summary>
    /// <c>rd</c> / <c>rmdir</c> with the recursive switch, aimed at a drive root
    /// or a system directory: <c>rd /s /q C:\</c>. The two lookaheads keep the
    /// switch and the target in the same command segment, so
    /// <c>del report.txt &amp;&amp; echo C:</c> is not mistaken for a root
    /// deletion.
    /// </summary>
    /// <remarks>
    /// The switch may be written without a space in front of it -
    /// <c>rd/s /q C:\</c> is what cmd.exe accepts and what it executes.
    /// </remarks>
    private static readonly Regex WindowsRecursiveDirectoryRemove = WindowsRecursive(@"(?:rd|rmdir)");

    /// <summary>
    /// <c>del</c> with the recursive switch, aimed at a drive root or a system
    /// directory: <c>del /s C:\</c>. The switch may be written without a space
    /// in front of it - <c>del/s C:\</c>.
    /// </summary>
    private static readonly Regex WindowsRecursiveFileDelete = WindowsRecursive("del");

    /// <summary>
    /// A whole-volume format: <c>format D:</c>. The switches may be written
    /// without a space in front of them - <c>format/q D:</c>.
    /// </summary>
    private static readonly Regex FormatDrive = new(
        WordStart + "format" + WordEnd
        + @"[ \t]*(?:/[^\s]*[ \t]+)*(?<![A-Za-z0-9_])[""']?[a-zA-Z]:",
        Options);

    /// <summary>
    /// The rules, in the order they are evaluated. The first match wins, so
    /// the reason reported names the rule that actually fired.
    /// </summary>
    private static readonly GuardrailRule[] Rules =
    [
        new(PosixRootDelete, "Recursive delete of the filesystem root (rm)."),
        new(WindowsRecursiveDirectoryRemove, "Recursive removal of a drive or system directory root (rd/rmdir)."),
        new(WindowsRecursiveFileDelete, "Recursive deletion of a drive or system directory root (del /s)."),
        new(FormatDrive, "Disk format of a drive root (format)."),
    ];

    /// <summary>
    /// One rule: the pattern that recognizes a destructive root command, and
    /// the reason reported when it matches.
    /// </summary>
    private sealed record GuardrailRule(
        Regex Pattern,
        string Reason);

    /// <summary>
    /// Reports whether the command would destroy a filesystem root, a drive,
    /// or a system directory.
    /// </summary>
    /// <param name="command">The command line to inspect.</param>
    /// <returns><see langword="true"/> when the command must not run.</returns>
    public static bool IsBlocked(string command) => BlockedReason(command) is not null;

    /// <summary>
    /// Explains why the command is refused.
    /// </summary>
    /// <param name="command">The command line to inspect.</param>
    /// <returns>
    /// A short human-readable reason naming the rule that matched, or
    /// <see langword="null"/> when the command is allowed.
    /// </returns>
    public static string? BlockedReason(string command)
    {
        // An empty command is not a destructive one; the tool rejects it as an
        // invalid argument before it ever reaches the guardrail.
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        foreach (var rule in Rules)
        {
            if (rule.Pattern.IsMatch(command))
            {
                return rule.Reason;
            }
        }

        return null;
    }
}
