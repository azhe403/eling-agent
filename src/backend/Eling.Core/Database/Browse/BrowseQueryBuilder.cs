using System.Globalization;
using System.Text;

namespace Eling.Core.Database.Browse;

/// <summary>
/// Turns one browse request into the two statements it needs: the page of
/// rows and the count behind it. Pure - no I/O, no connection - so the exact
/// SQL every user sees is reviewable and unit-testable.
///
/// Semantics, restated from the spec:
///   - disabled rows never participate;
///   - the AND/OR fold is left-associative: (((a AND b) OR c));
///   - the first enabled row's <see cref="BrowseFilter.Logic"/> is ignored,
///     and "first" on a later row folds as AND;
///   - LIKE escapes with '!' (doubled first, then % and _), with ESCAPE '!';
///   - tinyint coerces bool-first ('true'/'false'/'1'/'0' → 1/0), every other
///     numeric family parses invariant-culture decimal else throws
///     <see cref="BrowseQueryException"/>;
///   - limit/offset are inlined clamped integers - never parameters - so the
///     read cache's SQL-string keys stay valid.
/// </summary>
public static class BrowseQueryBuilder
{
    // The wire spells operators in any casing ("isNull", "ISNULL", "isnull");
    // every comparison against the canonical camelCase form goes through one
    // lookup, so the endpoint and the builder cannot drift apart.
    private static readonly Dictionary<string, string> CanonicalOps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eq"] = "eq",
        ["neq"] = "neq",
        ["contains"] = "contains",
        ["notcontains"] = "notContains",
        ["startswith"] = "startsWith",
        ["endswith"] = "endsWith",
        ["gt"] = "gt",
        ["gte"] = "gte",
        ["lt"] = "lt",
        ["lte"] = "lte",
        ["isnull"] = "isNull",
        ["isnotnull"] = "isNotNull",
    };

    private static readonly HashSet<string> NoValueOps = new(StringComparer.Ordinal) { "isNull", "isNotNull" };

    public static BrowsePlan Build(
        DatabaseProvider provider,
        string table,
        IReadOnlyList<BrowseFilter> filters,
        IReadOnlyList<BrowseSort> sort,
        string? manualCondition,
        IReadOnlyDictionary<string, string> columnTypes,
        bool schemaAvailable,
        int page,
        int limit)
    {
        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 1000);
        long offset = ((long)page - 1) * limit;

        var types = new Dictionary<string, string>(columnTypes, StringComparer.OrdinalIgnoreCase);
        string Quote(string name) => SqlIdentifiers.Quote(provider, name);

        // Every column referenced anywhere is validated once, schema-readable
        // or not - a typo'd filter must not reach the wire.
        void EnsureColumn(string column)
        {
            if (schemaAvailable && !types.ContainsKey(column))
            {
                throw new BrowseQueryException($"column '{column}' not found");
            }
        }

        string? TypeOf(string column) => types.TryGetValue(column, out var t) ? t?.ToLowerInvariant() : null;

        // A disabled row never participates - it must not demand validation
        // for a column that may not exist any more.
        foreach (var f in filters)
        {
            if (!f.Enabled) continue;
            EnsureColumn(f.Column);
        }
        foreach (var s in sort) EnsureColumn(s.Column);

        var parameters = new List<SqlParam>();
        var clauseSql = new List<string>();
        var clauseText = new List<string>();
        var clauseLogic = new List<string>(); // logic that joins this clause to the previous one; [0] ignored

        foreach (var filter in filters)
        {
            if (!filter.Enabled) continue;

            var rawOp = (filter.Op ?? "").Trim();
            if (!CanonicalOps.TryGetValue(rawOp, out var op))
            {
                throw new BrowseQueryException($"unknown operator '{filter.Op}'");
            }

            var col = Quote(filter.Column);
            var columnType = TypeOf(filter.Column);

            // PostgreSQL has no implicit text/numeric/uuid/timestamp cast in
            // comparison operators, so a raw string bound to one of those
            // columns is 42883. MySQL and SQLite coerce like the browser
            // expects; PG needs the cast spelled out.
            var isPg = provider == DatabaseProvider.PostgreSql;
            var pgText = isPg && IsPgTextType(columnType);
            var likeCol = isPg && !pgText ? col + "::text" : col;
            var eqCast = isPg ? PgScalarCast(columnType) : null;
            var comparison = eqCast is null ? "" : "::" + eqCast;

            if (NoValueOps.Contains(op))
            {
                clauseSql.Add(op == "isNull" ? $"{col} IS NULL" : $"{col} IS NOT NULL");
                clauseText.Add(op == "isNull" ? $"{filter.Column} IS NULL" : $"{filter.Column} IS NOT NULL");
                clauseLogic.Add(NormalizeLogic(filter.Logic));
                continue;
            }

            var bind = CoerceValue(filter.Column, filter.Value, columnType, isPg);
            parameters.Add(new SqlParam("@p" + parameters.Count, bind));
            var name = "@p" + (parameters.Count - 1);
            var literal = LiteralFor(bind);

            string likePattern;
            switch (op)
            {
                case "eq": clauseSql.Add($"{col} = {name}{comparison}"); clauseText.Add($"{filter.Column} = {literal}"); break;
                case "neq": clauseSql.Add($"{col} <> {name}{comparison}"); clauseText.Add($"{filter.Column} <> {literal}"); break;
                case "gt": clauseSql.Add($"{col} > {name}{comparison}"); clauseText.Add($"{filter.Column} > {literal}"); break;
                case "gte": clauseSql.Add($"{col} >= {name}{comparison}"); clauseText.Add($"{filter.Column} >= {literal}"); break;
                case "lt": clauseSql.Add($"{col} < {name}{comparison}"); clauseText.Add($"{filter.Column} < {literal}"); break;
                case "lte": clauseSql.Add($"{col} <= {name}{comparison}"); clauseText.Add($"{filter.Column} <= {literal}"); break;
                case "contains":
                case "notContains":
                case "startsWith":
                case "endsWith":
                    likePattern = LikeEscape(filter.Value ?? "", op);
                    parameters[parameters.Count - 1] = new SqlParam(name, likePattern);
                    literal = LiteralFor(likePattern);
                    var likeText = $"{filter.Column} LIKE {literal}";
                    var likeSql = $"{likeCol} LIKE {name} ESCAPE '!'";
                    if (op == "notContains")
                    {
                        clauseSql.Add($"NOT ({likeSql})");
                        clauseText.Add($"NOT ({likeText})");
                    }
                    else
                    {
                        clauseSql.Add(likeSql);
                        clauseText.Add(likeText);
                    }
                    break;
                // op is canonicalised above, so the cases above are exhaustive.
            }

            clauseLogic.Add(NormalizeLogic(filter.Logic));
        }

        // Left-associative fold: (a), then ((a AND b)), then (((a AND b) OR c)).
        string? Fold(IReadOnlyList<string> items, IReadOnlyList<string> logics)
        {
            if (items.Count == 0) return null;
            var acc = items[0];
            for (var i = 1; i < items.Count; i++)
            {
                acc = "(" + acc + " " + logics[i].ToUpperInvariant() + " " + items[i] + ")";
            }
            return acc;
        }

        var whereParts = Fold(clauseSql, clauseLogic);
        var whereText = Fold(clauseText, clauseLogic);

        var manual = string.IsNullOrWhiteSpace(manualCondition) ? null : "(" + manualCondition.Trim() + ")";
        string? whereSql;
        string whereDescription;
        if (whereParts is null && manual is null)
        {
            whereSql = null;
            whereDescription = "";
        }
        else if (whereParts is null)
        {
            whereSql = "(" + manualCondition!.Trim() + ")";
            whereDescription = manualCondition!.Trim();
        }
        else if (manual is null)
        {
            whereSql = "(" + whereParts + ")";
            whereDescription = whereText ?? whereParts;
        }
        else
        {
            whereSql = "(" + whereParts + ") AND " + manual;
            whereDescription = (whereText is null ? whereParts : whereText) + " AND " + manual;
        }

        var orderBy = "";
        if (sort.Count > 0)
        {
            var keys = new List<string>();
            foreach (var s in sort)
            {
                var ord = (s.Order ?? "").Trim().ToLowerInvariant();
                if (ord != "asc" && ord != "desc")
                {
                    throw new BrowseQueryException($"unknown sort order '{s.Order}'");
                }
                keys.Add(Quote(s.Column) + (ord == "asc" ? " ASC" : " DESC"));
            }
            orderBy = " ORDER BY " + string.Join(", ", keys);
        }

        var tableSql = Quote(table);
        var rowsSql = "SELECT * FROM " + tableSql
            + (whereSql is null ? "" : " WHERE " + whereSql)
            + orderBy
            + " LIMIT " + limit + " OFFSET " + offset;
        var countSql = "SELECT COUNT(*) FROM " + tableSql
            + (whereSql is null ? "" : " WHERE " + whereSql);

        if (whereDescription.Length > 600)
        {
            whereDescription = whereDescription[..599] + "…";
        }

        return new BrowsePlan(
            new BrowseQuery(rowsSql, parameters),
            new BrowseQuery(countSql, parameters),
            whereDescription);

        static string NormalizeLogic(string? logic)
        {
            var l = (logic ?? "and").Trim().ToLowerInvariant();
            return l is "or" ? "or" : "and"; // first and anything unrecognised fold as AND
        }

        static string LikeEscape(string value, string op)
        {
            var esc = value.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
            return op switch
            {
                "contains" or "notContains" => "%" + esc + "%",
                "startsWith" => esc + "%",
                "endsWith" => "%" + esc,
                _ => esc,
            };
        }

        // No thousands separator: '1,234' binds as 1234 otherwise, silently.
        const NumberStyles NumericStyle = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

        // PostgreSQL: the column type is the source of truth for whether the
        // value binds as text. Text-like families accept LIKE directly;
        // everything else needs the column cast (LIKE) or the parameter cast.
        static bool IsPgTextType(string? dataType) =>
            TrimPgType(dataType) is "text" or "character varying" or "varchar" or "character"
                or "char" or "name" or "citext";

        // Collapses runs of spaces: "timestamp  without" is spellable only one way here.
        static string TrimPgType(string? dataType)
        {
            var t = (dataType ?? "").Trim().ToLowerInvariant();
            var cut = t.IndexOf('(');
            if (cut > 0) t = t[..cut];
            return string.Join(' ', t.Split([' '], StringSplitOptions.RemoveEmptyEntries));
        }

        static string? PgScalarCast(string? dataType)
        {
            var t = (dataType ?? "").Trim().ToLowerInvariant();
            t = string.Join(' ', t.Split([' '], StringSplitOptions.RemoveEmptyEntries));
            return t switch
            {
                "uuid" => "uuid",
                "date" => "date",
                "time" or "time without time zone" => "time",
                "time with time zone" or "timetz" => "timetz",
                "timestamp" or "timestamp without time zone" or "datetime" => "timestamp",
                "timestamp with time zone" or "timestamptz" => "timestamptz",
                _ => null,
            };
        }

        static string FamilyOf(string? dataType)
        {
            var t = (dataType ?? "").ToLowerInvariant().Trim();
            var cut = t.IndexOfAny([' ', '(']);
            t = cut < 0 ? t : t[..cut];
            return t switch
            {
                "tinyint" or "bool" or "boolean" or "bit" => "boolean",
                "int" or "integer" or "smallint" or "mediumint" or "bigint"
                    or "decimal" or "numeric" or "float" or "double" or "real" => "numeric",
                "date" or "datetime" or "timestamp" or "time" or "year" => "datetime",
                _ => "text",
            };
        }

        object CoerceValue(string column, string? raw, string? columnType, bool isPg)
        {
            var value = raw ?? "";
            var family = FamilyOf(columnType);

            // tinyint never parses as "text" - bool-first keeps is_active = 'true' legal.
            if (family == "boolean")
            {
                var b = value.Trim();
                if (isPg)
                {
                    if (b.Equals("true", StringComparison.OrdinalIgnoreCase) || b.Equals("1", StringComparison.Ordinal)) return true;
                    if (b.Equals("false", StringComparison.OrdinalIgnoreCase) || b.Equals("0", StringComparison.Ordinal)) return false;
                    if (decimal.TryParse(value, NumericStyle, CultureInfo.InvariantCulture, out var pgBool)) return pgBool != 0;
                    throw new BrowseQueryException($"column '{column}' is boolean; '{value}' is not true/false");
                }
                if (b.Equals("true", StringComparison.OrdinalIgnoreCase) || b.Equals("1", StringComparison.Ordinal)) return 1;
                if (b.Equals("false", StringComparison.OrdinalIgnoreCase) || b.Equals("0", StringComparison.Ordinal)) return 0;
                if (decimal.TryParse(value, NumericStyle, CultureInfo.InvariantCulture, out var d)) return d;
                throw new BrowseQueryException($"column '{column}' is numeric; '{value}' is not a number");
            }

            if (family == "numeric")
            {
                if (decimal.TryParse(value, NumericStyle, CultureInfo.InvariantCulture, out var d)) return d;
                throw new BrowseQueryException($"column '{column}' is numeric; '{value}' is not a number");
            }

            // Datetime families ride through as strings: MySQL coerces a literal
            // ISO comparison natively, SQLite compares ISO text correctly.
            return value;
        }

        static string LiteralFor(object? value) => value switch
        {
            null => "NULL",
            string s => "'" + s.Replace("'", "''") + "'",
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            bool b => b ? "1" : "0",
            _ => "'" + Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace("'", "''") + "'",
        };
    }
}
