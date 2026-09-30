using System.Collections.Generic;
using Eling.Backend.Identity;

namespace Eling.Backend.Tests;

/// <summary>
/// Canned git config so the fallback paths are exercised without depending on
/// whatever global git config the machine running the tests happens to have.
/// </summary>
public sealed class FakeGitConfigReader(Dictionary<string, string?> values) : IGitConfigReader
{
    public List<string> RequestedKeys { get; } = [];

    public Task<string?> ReadGlobalAsync(string key, CancellationToken cancellationToken)
    {
        RequestedKeys.Add(key);
        return Task.FromResult(values.TryGetValue(key, out var value) ? value : null);
    }
}
