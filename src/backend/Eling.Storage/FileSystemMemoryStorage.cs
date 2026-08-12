using Eling.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Eling.Storage;

public class FileSystemMemoryStorage : IMemoryStorage
{
    private readonly string _memoriesDir;
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    public FileSystemMemoryStorage(string rootPath = ".eling")
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        _memoriesDir = Path.GetFullPath(Path.Combine(rootPath, "memories"));

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    private string GetFilePath(MemoryId id)
    {
        var filename = $"{id.Value}.md";
        var fullPath = Path.GetFullPath(Path.Combine(_memoriesDir, filename));

        if (!fullPath.StartsWith(_memoriesDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid memory ID or directory traversal attempt.", nameof(id));
        }

        return fullPath;
    }

    public async Task SaveAsync(Memory memory)
    {
        ArgumentNullException.ThrowIfNull(memory);
        Directory.CreateDirectory(_memoriesDir);

        var filePath = GetFilePath(memory.Id);

        var frontMatter = new MemoryFrontMatter
        {
            Id = memory.Id.Value,
            Type = memory.Type.ToString().ToLowerInvariant(),
            Status = memory.Status.ToString().ToLowerInvariant(),
            Tags = memory.Tags.Count > 0 ? memory.Tags.ToList() : null,
            CreatedAt = memory.CreatedAt,
            UpdatedAt = memory.UpdatedAt,
            Source = memory.Source
        };

        var yaml = _serializer.Serialize(frontMatter).TrimEnd();
        var markdown = $"---\n{yaml}\n---\n{memory.Content}";

        await File.WriteAllTextAsync(filePath, markdown);
    }

    public async Task<Memory?> GetByIdAsync(MemoryId id)
    {
        var filePath = GetFilePath(id);

        if (!File.Exists(filePath))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(filePath);
        return ParseMemory(text);
    }

    public Task<bool> DeleteAsync(MemoryId id)
    {
        var filePath = GetFilePath(id);

        if (!File.Exists(filePath))
        {
            return Task.FromResult(false);
        }

        File.Delete(filePath);
        return Task.FromResult(true);
    }

    public async Task<IReadOnlyCollection<Memory>> ListAllAsync()
    {
        if (!Directory.Exists(_memoriesDir))
        {
            return Array.Empty<Memory>();
        }

        var files = Directory.GetFiles(_memoriesDir, "*.md");
        var memories = new List<Memory>();

        foreach (var file in files)
        {
            var text = await File.ReadAllTextAsync(file);
            var memory = ParseMemory(text);
            if (memory != null)
            {
                memories.Add(memory);
            }
        }

        return memories.AsReadOnly();
    }

    private Memory ParseMemory(string rawMarkdown)
    {
        if (string.IsNullOrWhiteSpace(rawMarkdown))
        {
            throw new InvalidDataException("Memory Markdown file is empty.");
        }

        var lines = rawMarkdown.Replace("\r\n", "\n").Split('\n');

        if (lines.Length < 3 || lines[0].Trim() != "---")
        {
            throw new InvalidDataException("Malformed Memory Markdown: Missing valid YAML front matter delimiters.");
        }

        var closingDelimiterIndex = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() == "---")
            {
                closingDelimiterIndex = i;
                break;
            }
        }

        if (closingDelimiterIndex <= 1)
        {
            throw new InvalidDataException("Malformed Memory Markdown: Missing valid YAML front matter delimiters.");
        }

        var yamlBlock = string.Join("\n", lines.Skip(1).Take(closingDelimiterIndex - 1));
        var content = string.Join("\n", lines.Skip(closingDelimiterIndex + 1)).TrimStart('\r', '\n');

        MemoryFrontMatter frontMatter;
        try
        {
            frontMatter = _deserializer.Deserialize<MemoryFrontMatter>(yamlBlock);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("Malformed YAML front matter.", ex);
        }

        if (frontMatter == null || string.IsNullOrWhiteSpace(frontMatter.Id))
        {
            throw new InvalidDataException("Malformed Memory: Missing Id in front matter.");
        }

        if (!Enum.TryParse<MemoryType>(frontMatter.Type, true, out var memoryType))
        {
            throw new InvalidDataException($"Unknown MemoryType: '{frontMatter.Type}'.");
        }

        if (!Enum.TryParse<MemoryStatus>(frontMatter.Status, true, out var memoryStatus))
        {
            throw new InvalidDataException($"Unknown MemoryStatus: '{frontMatter.Status}'.");
        }

        return new Memory(
            type: memoryType,
            content: content,
            tags: frontMatter.Tags,
            source: frontMatter.Source,
            status: memoryStatus,
            id: MemoryId.Parse(frontMatter.Id),
            createdAt: frontMatter.CreatedAt,
            updatedAt: frontMatter.UpdatedAt);
    }
}
