namespace Eling.Core.Codebase;

public static class CodebaseChunker
{
    public const int ChunkLines = 280;
    public const int OverlapLines = 30;

    public static IReadOnlyList<CodebaseChunk> Chunk(string filePath, string content)
    {
        if (string.IsNullOrEmpty(content)) return Array.Empty<CodebaseChunk>();
        var lines = content.Split('\n');
        var chunks = new List<CodebaseChunk>();
        for (var start = 0; start < lines.Length; start += ChunkLines - OverlapLines)
        {
            var end = Math.Min(start + ChunkLines, lines.Length);
            var slice = string.Join('\n', lines[start..end]);
            chunks.Add(new CodebaseChunk(filePath, start + 1, end, slice));
            if (end == lines.Length) break;
        }
        return chunks;
    }
}
