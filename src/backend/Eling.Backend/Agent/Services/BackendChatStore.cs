using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eling.Backend.Agent.Ports;
using Eling.Backend.Dtos;
using Eling.Core.Serialization;
using Microsoft.Extensions.Logging;

namespace Eling.Backend.Agent.Services;

public sealed class ChatRecord
{
    public string Id { get; set; } = string.Empty;
    public string Workspace { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public List<AgentMessage> Messages { get; set; } = [];
}

public sealed class BackendChatStore
{
    private static JsonSerializerOptions JsonOptions => JsonDefaults.Shared;

    private readonly string _dir;
    private readonly ILogger<BackendChatStore> _logger;
    private readonly object _gate = new();

    public BackendChatStore(string dir, ILogger<BackendChatStore> logger, string? legacyDir = null)
    {
        _dir = dir;
        _logger = logger;
        if (!string.IsNullOrWhiteSpace(legacyDir))
        {
            StoreMigration.MoveDirectoryIfNeeded(_dir, legacyDir, _logger, "chat history");
        }
    }

    public ChatRecord GetOrCreate(string workspace, string? chatId)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(chatId))
            {
                var existing = Load(chatId);
                if (existing is not null && string.Equals(existing.Workspace, workspace, StringComparison.OrdinalIgnoreCase))
                {
                    return existing;
                }
            }

            var chat = new ChatRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Workspace = workspace,
                Title = "New chat",
                UpdatedAt = DateTimeOffset.UtcNow
            };
            Save(chat);
            return chat;
        }
    }

    public ChatRecord? Get(string id)
    {
        lock (_gate)
        {
            return Load(id);
        }
    }

    public IReadOnlyList<ChatSummary> List(string workspace)
    {
        lock (_gate)
        {
            var summaries = new List<ChatSummary>();
            try
            {
                if (!Directory.Exists(_dir))
                {
                    return summaries;
                }

                foreach (var file in Directory.EnumerateFiles(_dir, Slug(workspace) + "-*.json"))
                {
                    try
                    {
                        var chat = JsonSerializer.Deserialize<ChatRecord>(File.ReadAllText(file), JsonOptions);
                        if (chat is not null)
                        {
                            summaries.Add(new ChatSummary(chat.Id, chat.Workspace, chat.Title, chat.UpdatedAt));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Skipping unreadable chat file {File}", file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list chats for workspace");
            }

            return summaries.OrderByDescending(s => s.UpdatedAt).ToList();
        }
    }

    public void Append(ChatRecord chat, AgentMessage message)
    {
        lock (_gate)
        {
            chat.Messages.Add(message);
            if (chat.Title == "New chat" && message.Role == AgentRole.User && !string.IsNullOrWhiteSpace(message.Text))
            {
                chat.Title = message.Text.Length > 60 ? message.Text[..60] : message.Text;
            }

            chat.UpdatedAt = DateTimeOffset.UtcNow;
            Save(chat);
        }
    }

    private void Save(ChatRecord chat)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(Path.Combine(_dir, Slug(chat.Workspace) + "-" + chat.Id + ".json"), JsonSerializer.Serialize(chat, JsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save chat {Id}", chat.Id);
        }
    }

    private ChatRecord? Load(string id)
    {
        try
        {
            if (!Directory.Exists(_dir))
            {
                return null;
            }

            var match = Directory.EnumerateFiles(_dir, "*-" + id + ".json").FirstOrDefault();
            if (match is null)
            {
                return null;
            }

            return JsonSerializer.Deserialize<ChatRecord>(File.ReadAllText(match), JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load chat {Id}", id);
            return null;
        }
    }

    internal static string Slug(string workspace)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(workspace));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
