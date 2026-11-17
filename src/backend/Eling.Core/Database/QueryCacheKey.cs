using System.Globalization;
using System.Text;

namespace Eling.Core.Database;

/// <summary>
/// The read cache keys query snapshots on the SQL text. A statement with
/// bound parameters needs its values folded into that key, or two filter
/// values would collide on the same placeholder text and serve each other's
/// rows. With no parameters the key is the SQL text unchanged, so today's
/// snapshots keep working.
///
/// Format: <c>sql + "\n--p:" + name=tag:value</c>, joined with unit separator.
/// The tag distinguishes SQLite's type-sensitive compares ('1' vs 1).
/// </summary>
public static class QueryCacheKey
{
    public static string For(string sql, IReadOnlyList<SqlParam>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return sql;
        }

        var sb = new StringBuilder(sql.Length + 64);
        sb.Append(sql).Append("\n--p:");
        for (var i = 0; i < parameters.Count; i++)
        {
            if (i > 0) sb.Append('\u001f');
            var p = parameters[i];
            sb.Append(p.Name).Append('=').Append(Tag(p.Value)).Append(':')
              .Append(Convert.ToString(p.Value, CultureInfo.InvariantCulture));
        }
        return sb.ToString();

        static string Tag(object? value) => value switch
        {
            null => "n",
            bool => "b",
            string => "s",
            int or long or short or byte => "i",
            decimal or double or float => "d",
            _ => "x",
        };
    }
}
