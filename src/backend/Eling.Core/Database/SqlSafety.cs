namespace Eling.Core.Database;

public static class SqlSafety
{
    private static readonly HashSet<string> MutationKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "insert", "update", "delete", "drop", "alter", "create", "truncate",
        "replace", "grant", "revoke", "attach", "detach", "vacuum"
    };

    public static SqlClassification Classify(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return new SqlClassification(SqlVerdict.Rejected, "empty statement");
        }

        var stripped = new System.Text.StringBuilder(sql.Length);
        var i = 0;
        var n = sql.Length;
        while (i < n)
        {
            var c = sql[i];
            if (c == '\'')
            {
                stripped.Append(' ');
                i++;
                while (i < n)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < n && sql[i + 1] == '\'')
                        {
                            stripped.Append(' ');
                            i += 2;
                            continue;
                        }
                        stripped.Append(' ');
                        i++;
                        break;
                    }
                    stripped.Append(' ');
                    i++;
                }
                continue;
            }

            if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                while (i < n && sql[i] != '\n')
                {
                    stripped.Append(' ');
                    i++;
                }
                continue;
            }

            if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                stripped.Append(' ');
                stripped.Append(' ');
                i += 2;
                while (i < n)
                {
                    if (sql[i] == '*' && i + 1 < n && sql[i + 1] == '/')
                    {
                        stripped.Append(' ');
                        stripped.Append(' ');
                        i += 2;
                        break;
                    }
                    stripped.Append(' ');
                    i++;
                }
                continue;
            }

            stripped.Append(c);
            i++;
        }

        var text = stripped.ToString();
        var tokens = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        string first = "";
        if (tokens.Length > 0)
        {
            first = tokens[0].TrimStart('(', '[', '{', '"', '\\');
        }

        var firstLower = first.ToLowerInvariant();

        // Mutation statements: classified as Mutation (not executed by /query).
        if (MutationKeywords.Contains(firstLower))
        {
            return new SqlClassification(SqlVerdict.Mutation, null, first.ToUpperInvariant());
        }

        // Read-only statements.
        if (string.Equals(firstLower, "select", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(firstLower, "explain", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(firstLower, "pragma", StringComparison.OrdinalIgnoreCase))
        {
            // Secondary scan: reject mixed statements (e.g. SELECT ...; DROP ...).
            foreach (var token in tokens)
            {
                var t = token.TrimEnd(')', ']', '}', ';', ',', '.');
                if (MutationKeywords.Contains(t))
                {
                    return new SqlClassification(SqlVerdict.Rejected, $"statement contains mutation keyword '{t.ToLowerInvariant()}'");
                }
            }
            return new SqlClassification(SqlVerdict.ReadOnly, null);
        }

        return new SqlClassification(SqlVerdict.Rejected, "statement is not a recognized read or mutation command");
    }
}
