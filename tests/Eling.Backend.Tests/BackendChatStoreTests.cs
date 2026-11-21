using Eling.Backend.Agent.Ports;
using Eling.Backend.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Eling.Backend.Tests;

/// Covers BackendChatStore.Append's auto-rename: chats are retitled from the first
/// user message, but only while they still carry the default title — a title the
/// user (or a store client) set is never overwritten.
public class BackendChatStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly BackendChatStore _store;

    public BackendChatStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "eling-chatstore-" + Guid.NewGuid().ToString("N")[..8]);
        _store = new BackendChatStore(_dir, NullLogger<BackendChatStore>.Instance);
    }

    [Fact]
    public void Append_AutoTitle_CollapsesFirstUserMessage()
    {
        var chat = _store.GetOrCreate(_dir, null);
        _store.Append(chat, new AgentMessage(AgentRole.User, "Use the shell tool with timeout 6,000ms\n  then run it"));

        Assert.Equal("Use the shell tool with timeout 6,000ms then run it", _store.List(_dir).Single().Title);
    }

    [Fact]
    public void Append_AutoTitle_TruncatesTo60CharsWithEllipsis()
    {
        var chat = _store.GetOrCreate(_dir, null);
        _store.Append(chat, new AgentMessage(AgentRole.User, new string('x', 100)));

        var title = _store.List(_dir).Single().Title;
        Assert.Equal(61, title.Length);
        Assert.EndsWith("…", title);
    }

    [Fact]
    public void Append_DoesNotRetitleOnWhitespaceOnly()
    {
        var chat = _store.GetOrCreate(_dir, null);
        _store.Append(chat, new AgentMessage(AgentRole.User, "   \n\t  "));

        Assert.Equal("New chat", _store.List(_dir).Single().Title);
    }

    [Fact]
    public void Append_DoesNotClobberExplicitTitle()
    {
        var chat = _store.GetOrCreate(_dir, null);
        chat.Title = "Pinned by hand";
        _store.Append(chat, new AgentMessage(AgentRole.User, "hello there"));

        Assert.Equal("Pinned by hand", _store.List(_dir).Single().Title);
    }

    [Fact]
    public void Append_DoesNotRenameWhenAssistantMessage()
    {
        var chat = _store.GetOrCreate(_dir, null);
        _store.Append(chat, new AgentMessage(AgentRole.Assistant, "some answer"));

        Assert.Equal("New chat", _store.List(_dir).Single().Title);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            TestLogs.Log("test cleanup: delete temp dir failed", ex);
        }
    }
}
