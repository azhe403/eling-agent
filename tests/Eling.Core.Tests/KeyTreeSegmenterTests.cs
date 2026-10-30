using Eling.Core.Database;

namespace Eling.Core.Tests;

/// <summary>
/// The segmenter decides, for one scanned key under one prefix, whether it is
/// a node the tree must offer to expand or a leaf the tree shows in full. The
/// dashboard's tree shape hangs on this decision, so the cases in spec §5.1
/// are pinned here: nested keys, a leaf that is also a node, the colon
/// edge cases, and keys the prefix does not own.
/// </summary>
public sealed class KeyTreeSegmenterTests
{
    [Theory]
    [InlineData("user:", "user:1:email", "1", "user:1:")]
    [InlineData("user:", "user:1:email:x", "1", "user:1:")]
    public void NestedKey_ANodeUnderThePrefix(
        string prefix, string key, string expectedSegment, string expectedPrefix)
    {
        var result = KeyTreeSegmenter.Segment(prefix, key);

        Assert.False(result.IsLeaf);
        Assert.Equal(expectedSegment, result.NodeSegment);
        Assert.Equal(expectedPrefix, result.NodePrefix);
    }

    [Theory]
    [InlineData("user:", "user:1")]
    [InlineData("user:", "user:1a")]
    [InlineData("", "order")]
    public void NoFurtherColon_IsALeaf(string prefix, string key)
    {
        var result = KeyTreeSegmenter.Segment(prefix, key);

        Assert.True(result.IsLeaf);
        Assert.Null(result.NodeSegment);
        Assert.Null(result.NodePrefix);
    }

    [Theory]
    [InlineData("user:", "user:")]
    [InlineData("user:", "user::1")]
    [InlineData("a:", "a:")]
    [InlineData("a:", "a::1")]
    public void EmptySegment_IsALeaf(string prefix, string key)
    {
        // A trailing colon and a double colon both mean "no further segment",
        // so the key is a leaf.
        var result = KeyTreeSegmenter.Segment(prefix, key);

        Assert.True(result.IsLeaf);
    }

    [Fact]
    public void LeafAndNodeShareAName_BothAppear()
    {
        // Spec §5.1: under "user:", "user:1" is a leaf and "user:1:email"
        // makes "1" a node. One call per key; the tree sees both.
        Assert.True(KeyTreeSegmenter.IsLeaf("user:", "user:1"));
        Assert.False(KeyTreeSegmenter.IsLeaf("user:", "user:1:email"));
    }

    [Theory]
    [InlineData("user:", "other:1")]
    [InlineData("user:", "user")]
    public void KeyThatDoesNotStartWithPrefix_IsALeaf(string prefix, string key)
    {
        var result = KeyTreeSegmenter.Segment(prefix, key);

        Assert.True(result.IsLeaf);
    }

    [Fact]
    public void Utf8Key_SegmentsOnColonOnly()
    {
        var result = KeyTreeSegmenter.Segment("k:", "k:héllo:1");

        Assert.False(result.IsLeaf);
        Assert.Equal("héllo", result.NodeSegment);
        Assert.Equal("k:héllo:", result.NodePrefix);
    }

    [Fact]
    public void ReplacementCharacterKey_DoesNotBreakTheColon()
    {
        var result = KeyTreeSegmenter.Segment("k:", "k:\uFFFD:1");

        Assert.False(result.IsLeaf);
        Assert.Equal("\uFFFD", result.NodeSegment);
    }
}
