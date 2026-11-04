using Eling.Core.Database;
using Xunit;

namespace Eling.Core.Tests;

public sealed class SqlSafetyTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("SELECT * FROM users WHERE note = 'hello;world'")]
    [InlineData("SELECT * FROM users WHERE note = 'foo;bar' AND status = 1")]
    [InlineData("EXPLAIN SELECT 1")]
    [InlineData("PRAGMA table_info(users)")]
    [InlineData("SHOW CREATE TABLE mst_book_order")]
    [InlineData("SHOW CREATE VIEW v_orders")]
    [InlineData("SHOW COLUMNS FROM users")]
    [InlineData("SHOW FULL COLUMNS FROM users")]
    [InlineData("SHOW INDEX FROM users")]
    [InlineData("SHOW TABLES")]
    [InlineData("SHOW TABLE STATUS")]
    [InlineData("SHOW PROCESSLIST")]
    [InlineData("SHOW VARIABLES LIKE 'max_connections'")]
    [InlineData("DESCRIBE users")]
    [InlineData("DESC orders")]
    [InlineData("WITH cte AS (SELECT 1 AS x) SELECT x FROM cte")]
    [InlineData("/* leading block comment */ SELECT 1")]
    [InlineData("-- leading line comment\nSELECT 1")]
    [InlineData("   \t\n SELECT 1")]
    public void RecognizedReadOnlyStatements_ClassifyReadOnly(string sql)
    {
        var c = SqlSafety.Classify(sql);
        Assert.Equal(SqlVerdict.ReadOnly, c.Verdict);
    }

    [Theory]
    [InlineData("SELECT 1; DROP TABLE users")]
    [InlineData("SHOW PROCESSLIST; DELETE FROM users")]
    [InlineData("WITH cte AS (SELECT 1 AS x) INSERT INTO t SELECT x FROM cte")]
    [InlineData("SHOW CREATE TABLE t; UPDATE t SET a = 1")]
    [InlineData("DESCRIBE t; UPDATE t SET a = 1")]
    [InlineData("SHOW TABLES; DROP TABLE t")]
    public void MixedStatements_ContainMutationAndAreRejected(string sql)
    {
        var c = SqlSafety.Classify(sql);
        Assert.Equal(SqlVerdict.Rejected, c.Verdict);
    }

    [Theory]
    [InlineData("UPDATE users SET name = 'x'")]
    [InlineData("INSERT INTO t VALUES (1)")]
    [InlineData("DELETE FROM t")]
    [InlineData("DROP TABLE t")]
    [InlineData("ALTER TABLE t ADD COLUMN x INT")]
    [InlineData("TRUNCATE TABLE t")]
    [InlineData("CREATE TABLE t (id INT)")]
    [InlineData("GRANT ALL ON db.* TO 'u'@'%'")]
    public void MutationStartingStatements_ClassifyAsMutation(string sql)
    {
        var c = SqlSafety.Classify(sql);
        Assert.Equal(SqlVerdict.Mutation, c.Verdict);
    }

    [Theory]
    [InlineData("ANALYZE TABLE t")]
    [InlineData("EXEC sp_help users")]
    [InlineData("CALL some_procedure()")]
    [InlineData("LOAD DATA INFILE '/x' INTO TABLE t")]
    [InlineData("")]
    [InlineData("   ")]
    public void UnrecognizedOrNonReadOnlyStatements_AreRejected(string sql)
    {
        var c = SqlSafety.Classify(sql);
        Assert.Equal(SqlVerdict.Rejected, c.Verdict);
    }
}
