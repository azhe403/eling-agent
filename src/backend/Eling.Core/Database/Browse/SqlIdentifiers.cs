namespace Eling.Core.Database.Browse;

/// <summary>
/// Provider-correct identifier quoting. Doubling the quote character is the
/// only escaping both providers honour inside delimited identifiers, so any
/// caller input can be quoted and land in the SQL text safely.
/// </summary>
internal static class SqlIdentifiers
{
    public static string Quote(DatabaseProvider provider, string name) => provider switch
    {
        DatabaseProvider.MySql => "`" + name.Replace("`", "``") + "`",
        DatabaseProvider.Sqlite => "\"" + name.Replace("\"", "\"\"") + "\"",
        _ => throw new NotSupportedException($"Provider '{provider}' is not supported for browse."),
    };
}
