using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Eling.Core.Database;

/// <summary>
/// Compiles rows decoded from a query string ("filters=[{field,op,value}]") into the
/// Elasticsearch "bool" query that drops in as a filter clause list. Pure JSON building:
/// no ES client, no network. The field string is used exactly as given - the compiler
/// never appends ".keyword"; the UI offers it explicitly.
/// </summary>
/// <remarks>
/// Validation is row-by-row and the first failure wins. <c>Value == null</c> is legal
/// only for <c>exists</c>; every other op requires a value. Range values are passed
/// through to the DSL verbatim (as strings) - a value is accepted when it parses as an
/// invariant-culture number (epoch milliseconds etc.) or as an ISO 8601 date/time
/// ("2026-01-01"), which is what range filters on <c>@timestamp</c> carry.
/// </remarks>
public static class ElasticsearchFilterCompiler
{
    private static readonly Regex FieldPattern = new(
        "^[A-Za-z0-9_.@-]{1,255}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Ops = new(StringComparer.Ordinal)
    {
        "is",
        "contains",
        "gt",
        "gte",
        "lt",
        "lte",
        "exists",
    };

    public static JsonElement Compile(
        IReadOnlyList<ElasticsearchFilterRow> rows,
        string? logic)
    {
        if (rows.Count == 0)
        {
            throw new ElasticsearchFilterException("at least one filter row is required");
        }

        var clauses = new JsonArray();
        foreach (var row in rows)
        {
            ValidateRow(row);
            clauses.Add(CompileRow(row));
        }

        var boolQuery = new JsonObject();
        if (string.Equals(logic, "or", StringComparison.Ordinal))
        {
            boolQuery["should"] = clauses;
            boolQuery["minimum_should_match"] = 1;
        }
        else
        {
            boolQuery["must"] = clauses;
        }

        var root = new JsonObject
        {
            ["bool"] = boolQuery,
        };

        return JsonSerializer.SerializeToElement(root);
    }

    private static void ValidateRow(ElasticsearchFilterRow row)
    {
        if (row.Field is null || !FieldPattern.IsMatch(row.Field))
        {
            throw new ElasticsearchFilterException(
                $"invalid field '{row.Field}': must match ^[A-Za-z0-9_.@-]{{1,255}}$");
        }

        if (row.Op is null || !Ops.Contains(row.Op))
        {
            throw new ElasticsearchFilterException(
                $"unknown op '{row.Op}': expected one of is, contains, gt, gte, lt, lte, exists");
        }

        if (row.Op == "exists")
        {
            if (row.Value is not null)
            {
                throw new ElasticsearchFilterException("exists does not accept a value");
            }

            return;
        }

        if (row.Value is null)
        {
            throw new ElasticsearchFilterException(
                row.Op is "gt" or "gte" or "lt" or "lte"
                    ? "range requires a value"
                    : $"{row.Op} requires a value");
        }

        if ((row.Op is "gt" or "gte" or "lt" or "lte") && !IsRangeValue(row.Value))
        {
            throw new ElasticsearchFilterException($"range value '{row.Value}' is not numeric");
        }
    }

    private static bool IsRangeValue(string value)
    {
        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _)
            || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
    }

    private static JsonObject CompileRow(ElasticsearchFilterRow row)
    {
        switch (row.Op)
        {
            case "is":
                return new JsonObject
                {
                    ["term"] = new JsonObject
                    {
                        [row.Field] = new JsonObject
                        {
                            ["value"] = row.Value,
                        },
                    },
                };
            case "contains":
                return new JsonObject
                {
                    ["match"] = new JsonObject
                    {
                        [row.Field] = new JsonObject
                        {
                            ["query"] = row.Value,
                            ["operator"] = "and",
                        },
                    },
                };
            case "gt":
            case "gte":
            case "lt":
            case "lte":
                return new JsonObject
                {
                    ["range"] = new JsonObject
                    {
                        [row.Field] = new JsonObject
                        {
                            [row.Op] = row.Value,
                        },
                    },
                };
            case "exists":
                return new JsonObject
                {
                    ["exists"] = new JsonObject
                    {
                        ["field"] = row.Field,
                    },
                };
            default:
                throw new ElasticsearchFilterException($"unknown op '{row.Op}'");
        }
    }
}
