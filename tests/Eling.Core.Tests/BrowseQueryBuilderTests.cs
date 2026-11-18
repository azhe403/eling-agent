using Eling.Core.Database;
using Eling.Core.Database.Browse;

namespace Eling.Core.Tests;

/// <summary>
/// The browse query builder: what SQL goes out for one page of rows with the
/// user's conditions folded in, and what parameters travel with it. The
/// builder is pure - no I/O, no schema round trip - so every case here is a
/// literal expectation, not a mock of the database.
/// </summary>
public sealed class BrowseQueryBuilderTests
{
    private static BrowseFilter F(string col, string op, string? value = null, bool enabled = true, string logic = "first")
        => new(col, op, value, enabled, logic);

    private static BrowseSort S(string col, string order) => new(col, order);

    private static readonly Dictionary<string, string> VarcharTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = "varchar(50)", ["id"] = "int", ["updated_at"] = "datetime",
        ["created_at"] = "datetime", ["is_active"] = "tinyint(1)", ["code"] = "varchar(20)",
        ["score"] = "decimal(10,2)", ["a"] = "varchar(50)", ["b"] = "varchar(50)", ["c"] = "varchar(50)",
        ["decimal_col"] = "decimal(10,2)", ["bigint_col"] = "bigint",
    };

    private static BrowsePlan Build(
        IReadOnlyList<BrowseFilter> filters,
        IReadOnlyList<BrowseSort>? sort = null,
        string? manualCondition = null,
        Dictionary<string, string>? columnTypes = null,
        bool schemaAvailable = true,
        int page = 1,
        int limit = 100,
        DatabaseProvider provider = DatabaseProvider.Sqlite,
        string table = "mst_test")
        => BrowseQueryBuilder.Build(
            provider,
            table,
            filters,
            sort ?? [],
            manualCondition,
            columnTypes ?? VarcharTypes,
            schemaAvailable,
            page,
            limit);

    [Fact]
    public void Build_StringContains_BindsEscapedLikeWithEscapeBang()
    {
        var plan = Build([F("name", "contains", "50%")]);

        Assert.Contains("\"name\" LIKE @p0 ESCAPE '!'", plan.Rows.Sql);
        Assert.Equal("%50!%%", plan.Rows.Parameters[0].Value);
        Assert.Equal(
            "SELECT COUNT(*) FROM \"mst_test\" WHERE (\"name\" LIKE @p0 ESCAPE '!')",
            plan.Count.Sql);
    }

    [Fact]
    public void Build_StringContains_EscapesUnderscoreAndBang()
    {
        var plan = Build([F("name", "contains", "a_!%b")]);

        Assert.Equal("%a!_!!!%b%", plan.Rows.Parameters[0].Value);
    }

    [Fact]
    public void Build_EqualityOnStringColumn_BindsStringParam()
    {
        var plan = Build([F("updated_at", "eq", "2025-02-19 10:45:51")]);

        Assert.Equal("2025-02-19 10:45:51", plan.Rows.Parameters[0].Value);
        Assert.Contains("\"updated_at\" = @p0", plan.Rows.Sql);
    }

    [Fact]
    public void Build_BoolishTinyIntIsActiveTrue_BindsOne()
    {
        Assert.Equal(1, Build([F("is_active", "eq", "true")]).Rows.Parameters[0].Value);
        Assert.Equal(1, Build([F("is_active", "eq", "TRUE")]).Rows.Parameters[0].Value);
        Assert.Equal(0, Build([F("is_active", "eq", "false")]).Rows.Parameters[0].Value);
        Assert.Equal(1, Build([F("is_active", "eq", "1")]).Rows.Parameters[0].Value);
        Assert.Equal(0, Build([F("is_active", "eq", "0")]).Rows.Parameters[0].Value);
    }

    [Fact]
    public void Build_NumericColumn_BadValue_Throws()
    {
        var ex = Assert.Throws<BrowseQueryException>(() => Build([F("decimal_col", "eq", "abc")]));
        Assert.Contains("decimal_col", ex.Message);

        Assert.Throws<BrowseQueryException>(() => Build([F("bigint_col", "eq", "12.5.7")]));
    }

    [Fact]
    public void Build_NumericColumn_GoodValue_BindsDecimal()
    {
        var plan = Build([F("decimal_col", "eq", "12.5")]);

        Assert.Equal(12.5m, plan.Rows.Parameters[0].Value);
    }

    [Fact]
    public void Build_EnabledFiltersLeftAssociativeFold_ProducesParenthesisedNest()
    {
        var plan = Build([
            F("a", "eq", "aaa", true, "first"),
            F("b", "eq", "bbb", true, "and"),
            F("c", "eq", "ccc", true, "or"),
        ]);

        Assert.Equal(
            "SELECT * FROM \"mst_test\" WHERE (((\"a\" = @p0 AND \"b\" = @p1) OR \"c\" = @p2)) LIMIT 100 OFFSET 0",
            plan.Rows.Sql);
    }

    [Fact]
    public void Build_EnabledFiltersOrThenAnd_FoldLeftAssociative()
    {
        var plan = Build([
            F("a", "eq", "aaa", true, "first"),
            F("b", "eq", "bbb", true, "or"),
            F("c", "eq", "ccc", true, "and"),
        ]);

        Assert.Equal(
            "SELECT * FROM \"mst_test\" WHERE (((\"a\" = @p0 OR \"b\" = @p1) AND \"c\" = @p2)) LIMIT 100 OFFSET 0",
            plan.Rows.Sql);
    }

    [Fact]
    public void Build_DisabledRow_IsIgnored()
    {
        var plan = Build([
            F("a", "eq", "aaa", true, "first"),
            F("b", "eq", "bbb", false, "and"),
            F("c", "eq", "ccc", true, "or"),
        ]);

        Assert.DoesNotContain("\"b\"", plan.Rows.Sql);
        Assert.Equal(2, plan.Rows.Parameters.Count);
        Assert.DoesNotContain(plan.Rows.Parameters, p => (string?)p.Value == "bbb");
    }

    [Fact]
    public void Build_FirstLogicOnNonFirstRow_IsTreatedAsAnd()
    {
        var plan = Build([
            F("a", "eq", "aaa", true, "first"),
            F("b", "eq", "bbb", true, "first"),
        ]);

        Assert.Contains("AND", plan.Rows.Sql);
    }

    [Fact]
    public void Build_StartsWith_EscapesAndEmitsPercentSuffix()
    {
        var plan = Build([F("name", "startsWith", "a_50%")]);

        Assert.Contains("\"name\" LIKE @p0 ESCAPE '!'", plan.Rows.Sql);
        Assert.Equal("a!_50!%%", plan.Rows.Parameters[0].Value);
    }

    [Fact]
    public void Build_EndsWith_EscapesAndEmitsPercentPrefix()
    {
        var plan = Build([F("name", "endsWith", "50%")]);

        Assert.Contains("\"name\" LIKE @p0 ESCAPE '!'", plan.Rows.Sql);
        Assert.Equal("%50!%", plan.Rows.Parameters[0].Value);
    }

    [Fact]
    public void Build_NotContains_NegatesLike()
    {
        var plan = Build([F("name", "notContains", "a")]);

        Assert.Contains("NOT (\"name\" LIKE @p0 ESCAPE '!')", plan.Rows.Sql);
    }

    [Fact]
    public void Build_Neq_BindsNumber()
    {
        var plan = Build([F("decimal_col", "neq", "12.5")]);

        Assert.Contains("\"decimal_col\" <> @p0", plan.Rows.Sql);
        Assert.Equal(12.5m, plan.Rows.Parameters[0].Value);
    }

    [Fact]
    public void Build_EmptyStringEq_Binds()
    {
        var plan = Build([F("code", "eq", "")]);

        Assert.Equal("", plan.Rows.Parameters[0].Value);
    }

    [Fact]
    public void Build_PointColumn_TreatsAsText()
    {
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["location"] = "POINT",
        };
        var plan = Build([F("location", "eq", "abc")], columnTypes: types);

        Assert.Equal("abc", plan.Rows.Parameters[0].Value);
    }

    [Fact]
    public void Build_BoolColumn_MapsTrue()
    {
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ok"] = "boolean",
        };
        var plan = Build([F("ok", "eq", "true")], columnTypes: types);

        Assert.Equal(1, plan.Rows.Parameters[0].Value);
    }

    [Fact]
    public void Build_ThousandsSeparator_Throws()
    {
        var ex = Assert.Throws<BrowseQueryException>(() => Build([F("decimal_col", "eq", "1,234")]));
        Assert.Contains("decimal_col", ex.Message);
    }

    [Fact]
    public void Build_PageOverflow_DoesNotWrapArithmetic()
    {
        Assert.EndsWith("OFFSET 2147483646000", Build([], page: int.MaxValue, limit: 1000).Rows.Sql);
    }

    [Fact]
    public void Build_IsNull_And_IsNotNull_BindNoParameters()
    {
        var plan = Build([
            F("name", "isNull"),
            F("id", "isNotNull"),
            F("updated_at", "eq", "2025-02-19"),
        ]);

        Assert.Contains("\"name\" IS NULL", plan.Rows.Sql);
        Assert.Contains("\"id\" IS NOT NULL", plan.Rows.Sql);
        Assert.Single(plan.Rows.Parameters);
    }

    [Fact]
    public void Build_OperatorCasing_IsAcceptedAndCanonicalised()
    {
        Assert.Contains("\"name\" IS NULL", Build([F("name", "ISNULL")]).Rows.Sql);
        Assert.Contains("\"name\" IS NOT NULL", Build([F("name", "isnotnull")]).Rows.Sql);
        Assert.Contains("NOT (\"name\" LIKE", Build([F("name", "NOTCONTAINS", "a")]).Rows.Sql);
        Assert.Contains("\"name\" LIKE", Build([F("name", "StartsWith", "a")]).Rows.Sql);
        Assert.EndsWith("%", Build([F("name", "StartsWith", "a")]).Rows.Parameters[0].Value as string);
        Assert.StartsWith("%", Build([F("name", "ENDSWITH", "a")]).Rows.Parameters[0].Value as string);
    }

    [Fact]
    public void Build_AllDisabledAndNoManual_OmitsWhere()
    {
        var plan = Build([F("name", "eq", "x", enabled: false)]);

        Assert.DoesNotContain("WHERE", plan.Rows.Sql);
        Assert.DoesNotContain("WHERE", plan.Count.Sql);
        Assert.Equal("SELECT COUNT(*) FROM \"mst_test\"", plan.Count.Sql);
    }

    [Fact]
    public void Build_ManualCondition_AppendsParenthesisedAnd()
    {
        var plan = Build([F("a", "eq", "aaa")], manualCondition: "is_active = 1");

        Assert.Equal(
            "SELECT * FROM \"mst_test\" WHERE (\"a\" = @p0) AND (is_active = 1) LIMIT 100 OFFSET 0",
            plan.Rows.Sql);
    }

    [Fact]
    public void Build_ManualOnly_WrapsInParens()
    {
        var plan = Build([], manualCondition: "is_active = 1");

        Assert.Equal(
            "SELECT * FROM \"mst_test\" WHERE (is_active = 1) LIMIT 100 OFFSET 0",
            plan.Rows.Sql);
    }

    [Fact]
    public void Build_ManualWhitespaceOnly_IsIgnored()
    {
        var plan = Build([], manualCondition: "   ");

        Assert.DoesNotContain("WHERE", plan.Rows.Sql);
    }

    [Fact]
    public void Build_MultiColumnSort_EmitsBothOrderClauses()
    {
        var plan = Build([], sort: [S("updated_at", "desc"), S("created_at", "asc")]);

        Assert.Contains("ORDER BY \"updated_at\" DESC, \"created_at\" ASC", plan.Rows.Sql);
    }

    [Fact]
    public void Build_BadOrderValue_Throws()
    {
        Assert.Throws<BrowseQueryException>(() => Build([], sort: [S("a", "sideways")]));
    }

    [Fact]
    public void Build_UnknownOp_Throws()
    {
        var ex = Assert.Throws<BrowseQueryException>(() => Build([F("a", "like", "x")]));
        Assert.Contains("like", ex.Message);
    }

    [Fact]
    public void Build_DisabledRowWithUnknownColumn_Succeeds()
    {
        var plan = Build([F("nope", "eq", "x", enabled: false)]);

        Assert.DoesNotContain("nope", plan.Rows.Sql);
    }

    [Fact]
    public void Build_UnknownColumnWhenSchemaAvailable_Throws()
    {
        var ex = Assert.Throws<BrowseQueryException>(() => Build([F("nope", "eq", "x")]));
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void Build_UnknownColumnWhenSchemaUnavailable_Succeeds()
    {
        var plan = Build([F("nope", "eq", "x")], schemaAvailable: false);

        Assert.Contains("\"nope\" = @p0", plan.Rows.Sql);
    }

    [Fact]
    public void Build_ContainsOnUuidColumn_PostgreSql_CastsColumnToText()
    {
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["attachment_id"] = "uuid" };

        var plan = Build([F("attachment_id", "contains", "abc")], provider: DatabaseProvider.PostgreSql, columnTypes: types);

        Assert.Contains("\"attachment_id\"::text LIKE @p0", plan.Rows.Sql);
        Assert.Contains("ESCAPE '!'", plan.Rows.Sql);
    }

    [Fact]
    public void Build_ContainsOnUuidColumn_MySql_LeavesTheColumnAlone()
    {
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["attachment_id"] = "uuid" };

        var plan = Build([F("attachment_id", "contains", "abc")], provider: DatabaseProvider.MySql, columnTypes: types);

        Assert.DoesNotContain("::text", plan.Rows.Sql);
    }

    [Fact]
    public void Build_EqualityOnTimestamp_PostgreSql_CastsParameter()
    {
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["created_at"] = "timestamp without time zone" };

        var plan = Build([F("created_at", "eq", "2026-06-30")], provider: DatabaseProvider.PostgreSql, columnTypes: types);

        Assert.Contains("\"created_at\" = @p0::timestamp", plan.Rows.Sql);
    }

    [Fact]
    public void Build_EqualityOnUuid_PostgreSql_CastsParameter()
    {
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["attachment_id"] = "uuid" };

        var plan = Build([F("attachment_id", "eq", "4e88fcde-4946-4745-a55e-4fef1d2fd621")], provider: DatabaseProvider.PostgreSql, columnTypes: types);

        Assert.Contains("\"attachment_id\" = @p0::uuid", plan.Rows.Sql);
    }

    [Fact]
    public void Build_BoolEquality_PostgreSql_BindsBool()
    {
        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["is_active"] = "boolean" };

        var plan = Build([F("is_active", "eq", "true")], provider: DatabaseProvider.PostgreSql, columnTypes: types);

        Assert.Equal(true, plan.Rows.Parameters[0].Value);
    }

    [Fact]
    public void Build_IdentifierQuoting_DoublesQuoteChar()
    {
        var weirdTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["col`x"] = "varchar(20)", ["co\"l\"x"] = "varchar(20)",
        };
        var mysql = Build([F("col`x", "eq", "v")], table: "tab`x", provider: DatabaseProvider.MySql, columnTypes: weirdTypes);
        Assert.Contains("`tab``x`", mysql.Rows.Sql);
        Assert.Contains("`col``x`", mysql.Rows.Sql);

        var sqlite = Build([F("co\"l\"x", "eq", "v")], table: "ta\"b\"x", provider: DatabaseProvider.Sqlite, columnTypes: weirdTypes);
        Assert.Contains("\"ta\"\"b\"\"x\"", sqlite.Rows.Sql);
        Assert.Contains("\"co\"\"l\"\"x\"", sqlite.Rows.Sql);

        var postgres = Build([F("co\"l\"x", "eq", "v")], table: "ta\"b\"x", provider: DatabaseProvider.PostgreSql, columnTypes: weirdTypes);
        Assert.Contains("\"ta\"\"b\"\"x\"", postgres.Rows.Sql);
        Assert.Contains("\"co\"\"l\"\"x\"", postgres.Rows.Sql);
    }

    [Fact]
    public void Build_PageAndLimit_InlineClamped()
    {
        Assert.EndsWith("LIMIT 250 OFFSET 250", Build([], page: 2, limit: 250).Rows.Sql);
        Assert.EndsWith("LIMIT 1 OFFSET 0", Build([], page: 0, limit: 0).Rows.Sql);
        Assert.EndsWith("LIMIT 1000 OFFSET 0", Build([], page: 1, limit: 1001).Rows.Sql);
    }

    [Fact]
    public void Build_RowsAndCountQueries_ShareWhereAndParamOrder()
    {
        var plan = Build([F("a", "eq", "aaa"), F("b", "gt", "bbb")], manualCondition: "is_active = 1");

        var countWhere = plan.Count.Sql[(plan.Count.Sql.IndexOf("WHERE", StringComparison.Ordinal))..];
        var rowsWhere = plan.Rows.Sql[(plan.Rows.Sql.IndexOf("WHERE", StringComparison.Ordinal))..(plan.Rows.Sql.IndexOf(" LIMIT", StringComparison.Ordinal))];

        Assert.Equal(rowsWhere.TrimEnd(), countWhere.TrimEnd());
        Assert.Equal(
            plan.Rows.Parameters.Select(p => p.Name).ToArray(),
            plan.Count.Parameters.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Build_EmptyStringValue_BindsEmptyString()
    {
        var plan = Build([F("code", "eq", "")]);

        Assert.Equal("", plan.Rows.Parameters[0].Value);
        Assert.Contains("\"code\" = @p0", plan.Rows.Sql);
    }

    [Fact]
    public void Build_SqlSafetyVerdictForBuilderOutput_IsReadOnly()
    {
        var sqlite = Build([F("a", "contains", "x%")]);
        Assert.Equal(SqlVerdict.ReadOnly, SqlSafety.Classify(sqlite.Rows.Sql).Verdict);

        var mysql = Build([F("a", "contains", "x%")], provider: DatabaseProvider.MySql, table: "mst_test");
        Assert.Equal(SqlVerdict.ReadOnly, SqlSafety.Classify(mysql.Rows.Sql).Verdict);
    }
}
