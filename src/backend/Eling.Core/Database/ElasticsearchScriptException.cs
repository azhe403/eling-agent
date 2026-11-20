namespace Eling.Core.Database;

/// <summary>
/// A DSL body on the Elasticsearch read-only surface names a script-bearing
/// key: <c>scripted_metric</c>, <c>script</c>, <c>script_fields</c>,
/// <c>script_score</c> or <c>runtime_mappings</c>. Scripts execute Painless
/// on the cluster (a <c>scripted_metric</c> <c>map_script</c> runs arbitrary
/// code with cluster privileges), so the surface refuses them wholesale
/// instead of relying on the account's privileges. The offending key is named
/// in the message; the caller maps this to a 400.
/// </summary>
public sealed class ElasticsearchScriptException : Exception
{
    /// <summary>The offending key, e.g. <c>scripted_metric</c>.</summary>
    public string Key { get; }

    public ElasticsearchScriptException(string key)
        : base($"scripted key '{key}' is not allowed on the read-only surface")
    {
        Key = key;
    }
}
