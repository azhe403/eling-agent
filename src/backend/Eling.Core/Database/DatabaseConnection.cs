using System;

namespace Eling.Core.Database;

public sealed record DatabaseConnection(
    string Id,
    string Name,
    DatabaseProvider Provider,
    string ConnectionString,
    DatabaseEnvironment Environment,
    bool AllowAgentMutations,
    bool RequireApprovalForMutations,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastConnectedAt)
{
    public static DatabaseConnection Create(
        string name,
        DatabaseProvider provider,
        string connectionString,
        DatabaseEnvironment environment,
        bool allowAgentMutations = false,
        bool requireApprovalForMutations = true,
        DateTimeOffset? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("name must not be blank.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("connectionString must not be blank.", nameof(connectionString));
        }

        if (!Enum.IsDefined(provider))
        {
            throw new ArgumentException("provider must be a defined value.", nameof(provider));
        }

        if (!Enum.IsDefined(environment))
        {
            throw new ArgumentException("environment must be a defined value.", nameof(environment));
        }

        var id = System.Ulid.NewUlid().ToString().ToLowerInvariant();

        return new DatabaseConnection(
            id,
            name,
            provider,
            connectionString,
            environment,
            allowAgentMutations,
            requireApprovalForMutations,
            createdAt ?? DateTimeOffset.UtcNow,
            null);
    }
}
