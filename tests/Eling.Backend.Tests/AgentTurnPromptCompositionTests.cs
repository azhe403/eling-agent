using Eling.Backend.Agent.Ports;
using Eling.Backend.Agent.Services;
using Eling.Backend.Bootstrap;
using Eling.Backend.Mcp;
using Eling.Core.Codebase;
using Eling.Core.Recall;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Eling.Backend.Tests;

public sealed class AgentTurnPromptCompositionTests
{
    private sealed class CapturingGateway : IChatGateway
    {
        public SingleShotRequest? LastRequest { get; private set; }

        public Task<SingleShotResult> CompleteAsync(SingleShotRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new SingleShotResult("ok", []));
        }

        public IAsyncEnumerable<ChatStreamEvent> CompleteStreamingAsync(SingleShotRequest request, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task AgentTurn_SystemPrompt_CombinesIdentityAndServerInstructions()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "eling-prompt-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var gateway = new CapturingGateway();
            var chats = new BackendChatStore(Path.Combine(tempDir, "chats"), NullLogger<BackendChatStore>.Instance);
            var provider = new ProviderStore(tempDir, NullLogger<ProviderStore>.Instance);
            var recall = new StubRecallService();
            var codebase = new CodebaseIndexService(
                tempDir,
                new SqliteCodebaseIndex(Path.Combine(tempDir, "test-cb.db")));

            var svc = new AgentTurnService(
                gateway,
                [],
                chats,
                provider,
                recall,
                codebase,
                NullLogger<AgentTurnService>.Instance);

            await svc.RunTurnAsync(
                "C:/Test/Workspace",
                "chat-1",
                "hello",
                CancellationToken.None,
                onEvent: null);

            Assert.NotNull(gateway.LastRequest);
            var sys = gateway.LastRequest!.Messages.First(m => m.Role == AgentRole.System).Text;

            // Has agent identity and workspace path
            Assert.Contains("You are Eling", sys);
            Assert.Contains("C:/Test/Workspace", sys);

            // Has server instructions content directly
            Assert.Contains("Eling is a durable markdown-backed memory system", sys);
            Assert.Contains("Recall (MUST DO)", sys);
            Assert.Contains("Save (MUST DO)", sys);
            Assert.Contains("Scope - you choose it", sys);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private sealed class StubRecallService : IRecallService
    {
        public Task<RecallResult> RecallAsync(
            RecallContext? context,
            int recallLimit = 10,
            int recentLimit = 10,
            string? scope = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RecallResult(
                [],
                [],
                [],
                new RecallStats(0, 0, 0, 0, 0)));
        }
    }
}
