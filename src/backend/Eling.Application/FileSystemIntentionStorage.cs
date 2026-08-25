using Eling.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.Converters;
using YamlDotNet.Serialization.NamingConventions;

namespace Eling.Application;

public class FileSystemIntentionStorage : IIntentionStorage
{
    private readonly string _intentionsDir;
    private readonly ISerializer _serializer;
    private readonly IDeserializer _deserializer;

    public FileSystemIntentionStorage(string rootPath = ".eling")
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        _intentionsDir = Path.GetFullPath(Path.Combine(rootPath, "intentions"));

        _serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithTypeConverter(new LegacyTolerantDateTimeOffsetConverter())
            .Build();

        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .WithTypeConverter(new LegacyTolerantDateTimeOffsetConverter())
            .Build();
    }

    private string GetFilePath(MemoryId id)
    {
        var filename = $"{id.Value}.md";
        var fullPath = Path.GetFullPath(Path.Combine(_intentionsDir, filename));

        if (!fullPath.StartsWith(_intentionsDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Invalid intention ID or directory traversal attempt.", nameof(id));
        }

        return fullPath;
    }

    public async Task SaveAsync(Intention intention)
    {
        ArgumentNullException.ThrowIfNull(intention);

        Directory.CreateDirectory(_intentionsDir);
        var filePath = GetFilePath(intention.Id);

        var frontMatter = new IntentionFrontMatter
        {
            Id = intention.Id.Value,
            Description = intention.Description,
            TriggerType = intention.TriggerType.ToString(),
            Status = intention.Status.ToString(),
            CreatedAt = intention.CreatedAt,
            UpdatedAt = intention.UpdatedAt,
            ExpiresAt = intention.ExpiresAt,
            Source = intention.Source,
            Tags = intention.Tags.Count > 0 ? intention.Tags.ToList() : null
        };

        var yaml = _serializer.Serialize(frontMatter).TrimEnd();
        var markdown = $"---\n{yaml}\n---\n{intention.Description}";

        await File.WriteAllTextAsync(filePath, markdown);
    }

    public async Task<Intention?> GetByIdAsync(MemoryId id)
    {
        var filePath = GetFilePath(id);

        if (!File.Exists(filePath))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(filePath);
        var intention = ParseIntention(text);

        return intention;
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

    public async Task<IReadOnlyCollection<Intention>> ListAllAsync()
    {
        if (!Directory.Exists(_intentionsDir))
        {
            return Array.Empty<Intention>();
        }

        var files = Directory.GetFiles(_intentionsDir, "*.md");
        var intentions = new List<Intention>();

        foreach (var file in files)
        {
            var text = await File.ReadAllTextAsync(file);
            var intention = ParseIntention(text);
            if (intention != null)
            {
                intentions.Add(intention);
            }
        }

        return intentions.AsReadOnly();
    }

    private Intention? ParseIntention(string rawMarkdown)
    {
        if (string.IsNullOrWhiteSpace(rawMarkdown))
        {
            return null;
        }

        var lines = rawMarkdown.Replace("\r\n", "\n").Split('\n');

        if (lines.Length < 3 || lines[0].Trim() != "---")
        {
            return null;
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
            return null;
        }

        var yamlBlock = string.Join("\n", lines.Skip(1).Take(closingDelimiterIndex - 1));
        var content = string.Join("\n", lines.Skip(closingDelimiterIndex + 1)).TrimStart('\r', '\n');

        IntentionFrontMatter? frontMatter;
        try
        {
            frontMatter = _deserializer.Deserialize<IntentionFrontMatter>(yamlBlock);
        }
        catch
        {
            return null;
        }

        if (frontMatter == null || string.IsNullOrWhiteSpace(frontMatter.Id))
        {
            return null;
        }

        if (!MemoryId.TryParse(frontMatter.Id, out var id))
        {
            return null;
        }

        if (!Enum.TryParse(frontMatter.TriggerType, out TriggerType triggerType))
        {
            triggerType = TriggerType.Topic;
        }

        if (!Enum.TryParse(frontMatter.Status, out MemoryStatus status))
        {
            status = MemoryStatus.Active;
        }

        var description = frontMatter.Description ?? string.Empty;

        return new Intention(
            description: description,
            triggerType,
            frontMatter.Tags,
            frontMatter.Source,
            frontMatter.CreatedAt,
            frontMatter.UpdatedAt,
            frontMatter.ExpiresAt)
        {
            Status = status
        };
    }
}