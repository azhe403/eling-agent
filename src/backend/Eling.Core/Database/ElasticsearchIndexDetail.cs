using System.Text.Json;

namespace Eling.Core.Database;

public sealed record ElasticsearchIndexDetail(
    string Name,
    JsonElement Mappings,
    JsonElement Settings,
    long DocsCount,
    long StoreSizeBytes);
