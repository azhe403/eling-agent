using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Eling.Backend.Dtos;
using Eling.Backend.Mcp.Filters;
using Eling.Backend.Mcp.Tools;
using Eling.Core.Skill;
using Eling.Core.Skill.Storage;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Eling.Backend.Tests;

/// <summary>
/// Integration tests for the skill_* MCP tools: save/get/search/list/delete/restore
/// happy paths over a real service + temp-dir storage and SQLite index, with the
/// error paths captured through the same ToolExceptionFilter the host uses, so the
/// agent-visible IsError surface is what the tests assert.
/// </summary>
public sealed class SkillToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eling-skill-tools-" + Guid.NewGuid().ToString("N"));

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "eling-skill-tools-" + Guid.NewGuid().ToString("N") + ".db");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException ex)
        {
            // Best-effort cleanup; a locked temp catalog must not fail the run.
            _ = ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            _ = ex.Message;
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_dbPath + suffix);
            }
            catch (IOException ex)
            {
                _ = ex.Message;
            }
            catch (UnauthorizedAccessException ex)
            {
                _ = ex.Message;
            }
        }
    }

    private SkillService NewService(SqliteSkillIndex? index = null)
        => new(new FileSystemSkillStorage(_root), index ?? new SqliteSkillIndex(_dbPath));

    private SkillWriteTool NewWriteTool(ISkillService service)
        => new(service, catalogRoot: _root);

    private SkillReadTool NewReadTool(ISkillService service)
        => new(service, catalogRoot: _root);

    private static string Name64(string name)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(name));

    [Fact]
    public async Task skill_save_creates_skill_visible_in_folder_and_index()
    {
        var service = NewService();
        var writeTool = NewWriteTool(service);
        var readTool = NewReadTool(service);

        var response = await writeTool.SaveAsync(
            "my-skill", "does useful things", "# body\n\nsteps", tags: ["blazor"]);

        Assert.Equal("my-skill", response.Name);
        Assert.Equal("eling", response.Origin);
        Assert.False(string.IsNullOrWhiteSpace(response.Id));
        Assert.EndsWith(Path.Combine("skills", "my-skill", "SKILL.md"), response.Path);
        Assert.True(File.Exists(Path.Combine(_root, "skills", "my-skill", "SKILL.md")));

        var fetched = await readTool.GetAsync("my-skill");
        Assert.NotNull(fetched);
        Assert.Equal(response.Id, fetched!.Id);
        Assert.Equal("does useful things", fetched.Description);
        Assert.Equal("# body\n\nsteps", fetched.Body);
        Assert.Contains("blazor", fetched.Tags);
    }

    [Fact]
    public async Task skill_save_rejects_kebab_invalid()
    {
        var writeTool = NewWriteTool(NewService());

        var result = await ThroughFilterAsync(
            "skill_save",
            async () => (object?)await writeTool.SaveAsync("Bad_Name", "does useful things", "# body"));

        Assert.True(result.IsError);
        Assert.Contains("kebab-case", FirstBlock(result).Text);
        Assert.False(Directory.Exists(Path.Combine(_root, "skills", "Bad_Name")));
    }

    [Fact]
    public async Task skill_save_rejects_external_collision()
    {
        var index = new SqliteSkillIndex(_dbPath);
        await index.AdoptExternalScanResultAsync(
            new Skill("my-skill", "external desc", "external body", origin: SkillOrigin.External));
        var writeTool = NewWriteTool(NewService(index));

        var result = await ThroughFilterAsync(
            "skill_save",
            async () => (object?)await writeTool.SaveAsync("my-skill", "does useful things", "# body"));

        Assert.True(result.IsError);
        Assert.Contains("external", FirstBlock(result).Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task skill_save_rejects_traversal_sibling_path()
    {
        var writeTool = NewWriteTool(NewService());

        foreach (var badPath in new[] { "../escape.txt", "sub/../../escape.txt", "/absolute.txt", "C:/drive.txt", "..\\escape.txt", "~/home.txt", "" })
        {
            var siblings = new[]
            {
                new SkillSiblingFileInput { RelativePath = badPath, ContentBase64 = Name64("x") }
            };

            var result = await ThroughFilterAsync(
                "skill_save",
                async () => (object?)await writeTool.SaveAsync(
                    "my-skill", "does useful things", "# body", siblingFiles: siblings));

            Assert.True(result.IsError, $"expected IsError for sibling path '{badPath}'");
        }

        // The traversal rule also holds for base64: a bad payload fails the
        // whole save before any filesystem write.
        var badBase64 = new[]
        {
            new SkillSiblingFileInput { RelativePath = "script.ps1", ContentBase64 = "not!base64!" }
        };
        var invalid = await ThroughFilterAsync(
            "skill_save",
            async () => (object?)await writeTool.SaveAsync(
                "my-skill", "does useful things", "# body", siblingFiles: badBase64));

        Assert.True(invalid.IsError);
        Assert.Contains("Base64", FirstBlock(invalid).Text);
        Assert.False(Directory.Exists(Path.Combine(_root, "skills", "my-skill")));
        Assert.False(File.Exists(Path.Combine(_root, "skills", "escape.txt")));
    }

    [Fact]
    public async Task skill_get_by_name_and_by_id()
    {
        var service = NewService();
        var writeTool = NewWriteTool(service);
        var readTool = NewReadTool(service);

        var saved = await writeTool.SaveAsync("my-skill", "does useful things", "# body");

        var byName = await readTool.GetAsync("my-skill");
        var byId = await readTool.GetAsync(saved.Id);

        Assert.NotNull(byName);
        Assert.NotNull(byId);
        Assert.Equal("my-skill", byName!.Name);
        Assert.Equal(saved.Id, byName.Id);
        Assert.Equal(saved.Id, byId!.Id);
        Assert.Equal("does useful things", byId.Description);

        Assert.Null(await readTool.GetAsync("no-such-skill"));
        Assert.Null(await readTool.GetAsync("01m00000000000000000000000"));
    }

    [Fact]
    public async Task skill_search_returns_ranked_hits_with_origin_filter()
    {
        var index = new SqliteSkillIndex(_dbPath);
        await index.AdoptExternalScanResultAsync(
            new Skill("external-blazor", "External blazor toolkit", "external blazor body", origin: SkillOrigin.External));
        var service = NewService(index);
        var writeTool = NewWriteTool(service);
        var readTool = NewReadTool(service);

        await writeTool.SaveAsync("blazor-forms", "Build blazor forms", "blazor forms body");
        await writeTool.SaveAsync("blazor-validation", "Validate blazor input", "blazor validation body");

        var hits = await readTool.SearchAsync("blazor");

        Assert.Equal(3, hits.Count);
        Assert.All(hits, hit =>
        {
            Assert.False(string.IsNullOrWhiteSpace(hit.Name));
            Assert.False(string.IsNullOrWhiteSpace(hit.Description));
            Assert.EndsWith("SKILL.md", hit.Path);
            Assert.True(hit.Score >= 0);
        });
        Assert.Equal(hits.OrderByDescending(h => h.Score).Select(h => h.Score).ToList(),
            hits.Select(h => h.Score).ToList());

        var externalOnly = await readTool.SearchAsync("blazor", origin: "external");

        var hit = Assert.Single(externalOnly);
        Assert.Equal("external-blazor", hit.Name);
        Assert.Equal("external", hit.Origin);
    }

    [Fact]
    public async Task skill_delete_then_restore()
    {
        var service = NewService();
        var writeTool = NewWriteTool(service);
        var readTool = NewReadTool(service);

        await writeTool.SaveAsync("my-skill", "does useful things", "# body");
        Assert.NotNull(await readTool.GetAsync("my-skill"));

        var deleted = await writeTool.DeleteAsync("my-skill");
        Assert.True(deleted.Trashed);
        Assert.Null(await readTool.GetAsync("my-skill"));
        Assert.True(Directory.Exists(Path.Combine(_root, ".trash", "skills", "my-skill")));

        var restored = await writeTool.RestoreAsync("my-skill");
        Assert.Equal("my-skill", restored.Name);
        Assert.NotNull(await readTool.GetAsync("my-skill"));
    }

    [Fact]
    public async Task skill_delete_refuses_external_skill()
    {
        var index = new SqliteSkillIndex(_dbPath);
        await index.AdoptExternalScanResultAsync(
            new Skill("my-skill", "external desc", "external body", origin: SkillOrigin.External));
        var writeTool = NewWriteTool(NewService(index));

        var result = await ThroughFilterAsync(
            "skill_delete",
            async () => (object?)await writeTool.DeleteAsync("my-skill"));

        Assert.True(result.IsError);
        Assert.Contains("external", FirstBlock(result).Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task skill_list_paginates_and_filters()
    {
        var service = NewService();
        var writeTool = NewWriteTool(service);
        var readTool = NewReadTool(service);

        for (var i = 1; i <= 25; i++)
        {
            var tags = i % 8 == 5 ? new[] { "special" } : new[] { "bulk" };
            await writeTool.SaveAsync($"bulk-{i:D2}", $"bulk skill number {i}", $"# bulk {i}", tags: tags);
        }

        var page2 = await readTool.ListAsync(page: 2, pageSize: 10);

        Assert.Equal(25, page2.Metadata.Total);
        Assert.Equal(25, page2.Metadata.Matching);
        Assert.Equal(10, page2.Items.Count);
        Assert.Equal(10, page2.Metadata.Limit);
        Assert.Equal(10, page2.Metadata.Offset);
        Assert.Equal(
            Enumerable.Range(11, 10).Select(i => $"bulk-{i:D2}").ToList(),
            page2.Items.Select(i => i.Name).ToList());

        var tagged = await readTool.ListAsync(tag: "special");

        Assert.Equal(25, tagged.Metadata.Total);
        Assert.Equal(3, tagged.Metadata.Matching);
        Assert.Equal(3, tagged.Items.Count);

        await writeTool.DeleteAsync("bulk-13");

        var afterDelete = await readTool.ListAsync(page: 1, pageSize: 10);
        Assert.Equal(25, afterDelete.Metadata.Total);
        Assert.Equal(24, afterDelete.Metadata.Matching);

        var withTrash = await readTool.ListAsync(includeTrashed: true, page: 1, pageSize: 30);
        Assert.Equal(25, withTrash.Metadata.Total);
        Assert.Equal(25, withTrash.Metadata.Matching);
    }

    private static async Task<CallToolResult> ThroughFilterAsync(string toolName, Func<Task<object?>> invoke)
    {
        McpRequestHandler<CallToolRequestParams, CallToolResult> next = async (_, _) =>
        {
            var payload = await invoke();
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = json }]
            };
        };

        var handler = ToolExceptionFilter.Create()(next);
        var context = CreateCallContext(toolName);
        return await handler(context, CancellationToken.None);
    }

    private static TextContentBlock FirstBlock(CallToolResult result)
        => Assert.IsType<TextContentBlock>(Assert.Single(result.Content!));

    private static RequestContext<CallToolRequestParams> CreateCallContext(string toolName)
        => CreateContext(new CallToolRequestParams { Name = toolName });

    private static RequestContext<TParams> CreateContext<TParams>(TParams? requestParams)
    {
        var context = (RequestContext<TParams>)RuntimeHelpers.GetUninitializedObject(typeof(RequestContext<TParams>));
        SetMember(context, "Params", requestParams);
        SetMember(context, "Services", null);
        return context;
    }

    private static void SetMember<TParams>(RequestContext<TParams> context, string memberName, object? value)
    {
        var setter = typeof(RequestContext<TParams>)
            .GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance)
            ?.GetSetMethod(nonPublic: true);
        if (setter is not null)
        {
            setter.Invoke(context, [value]);
            return;
        }

        var backingField = typeof(RequestContext<TParams>)
            .GetField($"<{memberName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? typeof(RequestContext<TParams>).BaseType?.GetField(
                $"<{memberName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        backingField?.SetValue(context, value);
    }
}
