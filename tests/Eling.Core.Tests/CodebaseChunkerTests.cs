using System.Text;
using Eling.Core.Codebase;

namespace Eling.Core.Tests;

/// <summary>
/// Chunk boundaries must not split a declaration. The chunker packs whole
/// members into a chunk and only gives up — cutting mid-declaration and
/// repeating the tail as overlap — when one member is larger than a chunk.
/// </summary>
public class CodebaseChunkerTests
{
    [Fact]
    public void Chunk_EmptyContent_ReturnsNoChunks()
    {
        Assert.Empty(CodebaseChunker.Chunk("Empty.cs", string.Empty));
    }

    [Fact]
    public void Chunk_ContentShorterThanOneChunk_ReturnsASingleChunk()
    {
        var content = CsClassWithMethods(3, 4);

        var chunks = CodebaseChunker.Chunk("Sample.cs", content);

        var chunk = Assert.Single(chunks);
        Assert.Equal(1, chunk.StartLine);
        Assert.Equal(content.Split('\n').Length, chunk.EndLine);
    }

    [Fact]
    public void Chunk_LongClass_StartsEveryChunkAfterTheFirstAtAMember()
    {
        var content = CsClassWithMethods(40, 20);

        var chunks = CodebaseChunker.Chunk("Sample.cs", content);

        Assert.True(chunks.Count > 1, "fixture should span several chunks");
        foreach (var chunk in chunks.Skip(1))
        {
            var firstLine = chunk.Content.Split('\n')[0].Trim();
            Assert.True(
                firstLine.StartsWith("///", StringComparison.Ordinal)
                || firstLine.StartsWith("public", StringComparison.Ordinal),
                $"chunk starting at line {chunk.StartLine} begins with '{firstLine}', not a member start");
        }
    }

    [Fact]
    public void Chunk_LongClass_ChunksAreAPartitionOfTheFile()
    {
        var content = CsClassWithMethods(40, 20);

        var chunks = CodebaseChunker.Chunk("Sample.cs", content);

        Assert.Equal(content, string.Join('\n', chunks.Select(c => c.Content)));
    }

    [Fact]
    public void Chunk_LongClass_StaysWithinTheLineCap()
    {
        var content = CsClassWithMethods(40, 20);

        var chunks = CodebaseChunker.Chunk("Sample.cs", content);

        Assert.All(chunks, c => Assert.True(
            c.EndLine - c.StartLine + 1 <= CodebaseChunker.MaxChunkLines,
            $"chunk {c.StartLine}-{c.EndLine} exceeds the cap"));
    }

    [Fact]
    public void Chunk_MethodLargerThanTheCap_SplitsItAndRepeatsTheTailAsOverlap()
    {
        var content = CsClassWithOneLongMethod(900);

        var chunks = CodebaseChunker.Chunk("Sample.cs", content);

        Assert.True(chunks.Count > 1, "an oversized method still has to be split");
        Assert.Equal(chunks[0].EndLine - CodebaseChunker.OverlapLines + 1, chunks[1].StartLine);
    }

    [Fact]
    public void Chunk_UnbalancedSource_FallsBackToFixedWindows()
    {
        // A truncated file cannot be scanned, so the chunker must behave exactly
        // as it did before: plain line windows, no overlap, nothing dropped.
        var body = string.Join('\n', Enumerable.Range(0, 600).Select(i => $"        var value{i} = {i};"));
        var content = $"public sealed class Sample\n{{\n    public void Run()\n    {{\n{body}\n";

        var chunks = CodebaseChunker.Chunk("Broken.cs", content);

        Assert.True(chunks.Count > 1, "fixture should span several chunks");
        Assert.Equal(content, string.Join('\n', chunks.Select(c => c.Content)));
        // Every chunk but the tail is a full window; the last one is the remainder.
        Assert.All(chunks.Take(chunks.Count - 1), c =>
            Assert.Equal(CodebaseChunker.TargetLines, c.EndLine - c.StartLine + 1));
    }

    [Fact]
    public void Chunk_CrlfContent_PreservesLineEndings()
    {
        var content = CsClassWithMethods(40, 20).Replace("\r\n", "\n").Replace("\n", "\r\n");

        var chunks = CodebaseChunker.Chunk("Sample.cs", content);

        Assert.Equal(content, string.Join('\n', chunks.Select(c => c.Content)));
    }

    [Fact]
    public void Chunk_Markdown_CutsAtBlankLines()
    {
        var content = MarkdownWithSections(20, 20);

        var chunks = CodebaseChunker.Chunk("doc.md", content);

        Assert.True(chunks.Count > 1, "fixture should span several chunks");
        Assert.All(chunks.Skip(1), c =>
            Assert.StartsWith("#", c.Content.TrimStart(), StringComparison.Ordinal));
    }

    /// <summary>
    /// A file-shaped class whose members are all the same length, so a chunk
    /// boundary that lands on a member is unambiguous.
    /// </summary>
    private static string CsClassWithMethods(int methodCount, int linesPerMethod)
    {
        var sb = new StringBuilder();
        sb.Append("namespace Probe;\n\npublic sealed class Sample\n{\n");
        for (var m = 0; m < methodCount; m++)
        {
            sb.Append($"    /// Doc for method {m}.\n");
            sb.Append($"    public int Method{m:D2}()\n");
            sb.Append("    {\n");
            for (var i = 0; i < linesPerMethod; i++) sb.Append($"        var value{m:D2}_{i:D2} = {i};\n");
            sb.Append("    }\n\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    private static string CsClassWithOneLongMethod(int linesPerMethod)
    {
        var sb = new StringBuilder();
        sb.Append("namespace Probe;\n\npublic sealed class Sample\n{\n");
        sb.Append("    public int Huge()\n    {\n");
        for (var i = 0; i < linesPerMethod; i++) sb.Append($"        var value{i:D4} = {i};\n");
        sb.Append("    }\n}\n");
        return sb.ToString();
    }

    private static string MarkdownWithSections(int sectionCount, int linesPerSection)
    {
        var sb = new StringBuilder();
        for (var s = 0; s < sectionCount; s++)
        {
            sb.Append($"# Section {s}\n\n");
            for (var i = 0; i < linesPerSection; i++) sb.Append($"Body line {i} of section {s}.\n");
            sb.Append('\n');
        }
        return sb.ToString();
    }
}