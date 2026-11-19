using System.Text.Json;

namespace Eling.Core.Database;

public sealed record ElasticsearchHit(
    string Index,
    string Id,
    double? Score,
    JsonElement Source);
