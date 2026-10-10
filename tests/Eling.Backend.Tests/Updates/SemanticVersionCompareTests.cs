using Eling.Backend.Updates;
using Xunit;

namespace Eling.Backend.Tests.Updates;

/// <summary>
/// The update check stands or falls on version ordering, so the comparer is
/// pinned down first: numeric pre-release suffixes must beat lexicographic
/// traps (<c>pre.12</c> over <c>pre.9</c>), and CI build metadata must never
/// affect precedence.
/// </summary>
public sealed class SemanticVersionCompareTests
{
    [Theory]
    [InlineData("v0.2.0", "v0.1.0")]
    [InlineData("0.1.0", "0.1.0-pre.5")]
    [InlineData("v0.1.0-pre.12", "v0.1.0-pre.9")]
    [InlineData("v0.1.0-pre.32", "v0.1.0-pre.9")]
    public void IsNewer_NewerCandidate_ReturnsTrue(string candidate, string current)
        => Assert.True(SemanticVersionCompare.IsNewer(candidate, current));

    [Theory]
    [InlineData("v0.1.0", "v0.2.0")]
    [InlineData("v0.1.0-pre.9", "v0.1.0-pre.12")]
    [InlineData("v0.1.0", "v0.1.0")]
    [InlineData("v0.1.0-pre.5", "v0.1.0-pre.5")]
    [InlineData("v0.1.0-pre.5", "v0.1.0")]
    public void IsNewer_OlderOrEqualCandidate_ReturnsFalse(string candidate, string current)
        => Assert.False(SemanticVersionCompare.IsNewer(candidate, current));

    [Theory]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("0.1.0-pre.5+sha.abc1234", "0.1.0-pre.5")]
    [InlineData("  v0.1.0  ", "0.1.0")]
    public void Compare_IgnoresPrefixCasingMetadataAndPadding(string left, string right)
        => Assert.Equal(0, SemanticVersionCompare.Compare(left, right));
}
