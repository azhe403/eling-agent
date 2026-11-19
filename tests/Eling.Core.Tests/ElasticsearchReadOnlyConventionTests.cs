using System.Reflection;
using Eling.Core.Database;

namespace Eling.Core.Tests;

/// <summary>
/// Elasticsearch support is read-only by construction: <see cref="IElasticsearchReadService"/>
/// must never grow a write-shaped member. This convention test pins that.
/// </summary>
/// <remarks>
/// A banned verb is matched only at the START of the method name, never as a bare
/// substring. The mandated read surface contains <c>GetIndexDetailAsync</c> and
/// <c>ListIndicesAsync</c>; "Index" as a leading verb is the write shape (as in
/// Elasticsearch's index-document API), "Index" mid-name after Get/List is a
/// read. Anchoring at the start keeps the guard meaningful without banning the
/// read surface the task requires.
/// </remarks>
public sealed class ElasticsearchReadOnlyConventionTests
{
    private static readonly string[] BannedVerbs =
    [
        "Index",
        "Update",
        "Delete",
        "Bulk",
        "Reindex",
        "Put",
        "Create",
        "Write",
        "Ingest",
        "Open",
        "Close",
        "PutMapping"
    ];

    [Fact]
    public void Interface_HasNoWriteShapedMembers()
    {
        var offenders = typeof(IElasticsearchReadService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .Where(name => BannedVerbs.Any(verb => name.StartsWith(verb, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(offenders.Count == 0,
            "IElasticsearchReadService is a read-only contract; write-shaped members are not allowed:\n"
            + string.Join("\n", offenders));
    }
}
