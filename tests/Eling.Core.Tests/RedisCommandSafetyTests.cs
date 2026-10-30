using Eling.Core.Database;

namespace Eling.Core.Tests;

/// <summary>
/// The command whitelist is the only thing standing between the read-only
/// dashboard and a live Redis server: everything it refuses must be refused
/// for a reason a person can read, and everything it allows must be a read.
/// These tests pin both sides and the two argument limits that stop one
/// allowed command from being turned into a denial of service.
/// </summary>
public sealed class RedisCommandSafetyTests
{
    [Theory]
    [InlineData("DBSIZE")]
    [InlineData("PING")]
    [InlineData("ECHO")]
    [InlineData("TIME")]
    [InlineData("RANDOMKEY")]
    [InlineData("INFO")]
    [InlineData("GET")]
    [InlineData("MGET")]
    [InlineData("STRLEN")]
    [InlineData("GETRANGE")]
    [InlineData("TYPE")]
    [InlineData("TTL")]
    [InlineData("PTTL")]
    [InlineData("EXISTS")]
    [InlineData("HGET")]
    [InlineData("HGETALL")]
    [InlineData("HKEYS")]
    [InlineData("HVALS")]
    [InlineData("HLEN")]
    [InlineData("LRANGE")]
    [InlineData("LLEN")]
    [InlineData("LINDEX")]
    [InlineData("SMEMBERS")]
    [InlineData("SCARD")]
    [InlineData("SISMEMBER")]
    [InlineData("SSCAN")]
    [InlineData("ZRANGE")]
    [InlineData("ZREVRANGE")]
    [InlineData("ZCARD")]
    [InlineData("ZSCORE")]
    [InlineData("ZSCAN")]
    [InlineData("XRANGE")]
    [InlineData("XREVRANGE")]
    [InlineData("XLEN")]
    [InlineData("XINFO")]
    public void WhitelistedCommand_IsAllowed(string command)
    {
        var verdict = RedisCommandSafety.Classify(command, []);

        Assert.Equal(RedisVerdict.Allowed, verdict.Verdict);
    }

    [Theory]
    [InlineData("SET")]
    [InlineData("DEL")]
    [InlineData("EXPIRE")]
    [InlineData("PERSIST")]
    [InlineData("RENAME")]
    [InlineData("KEYS")]
    [InlineData("FLUSHDB")]
    [InlineData("FLUSHALL")]
    [InlineData("CONFIG")]
    [InlineData("SHUTDOWN")]
    [InlineData("MONITOR")]
    [InlineData("SUBSCRIBE")]
    [InlineData("CLUSTER")]
    [InlineData("SCRIPT")]
    [InlineData("EVAL")]
    [InlineData("EVALSHA")]
    [InlineData("SWAPDB")]
    [InlineData("MIGRATE")]
    [InlineData("RESTORE")]
    [InlineData("DUMP")]
    [InlineData("GETDEL")]
    [InlineData("GETEX")]
    [InlineData("BITFIELD")]
    [InlineData("DEBUG")]
    [InlineData("ACL")]
    [InlineData("CLIENT")]
    [InlineData("LATENCY")]
    [InlineData("SLOWLOG")]
    [InlineData("FUNCTION")]
    [InlineData("FCALL")]
    public void DeniedCommand_IsRejected(string command)
    {
        var verdict = RedisCommandSafety.Classify(command, []);

        Assert.Equal(RedisVerdict.Rejected, verdict.Verdict);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
    }

    [Fact]
    public void CaseAndWhitespace_AreNormalized()
    {
        Assert.Equal(RedisVerdict.Allowed, RedisCommandSafety.Classify("  get  ", ["a"]).Verdict);
        Assert.Equal(RedisVerdict.Allowed, RedisCommandSafety.Classify("get", ["a"]).Verdict);
        Assert.Equal(RedisVerdict.Rejected, RedisCommandSafety.Classify("  set ", ["a", "b"]).Verdict);
    }

    [Theory]
    [InlineData("MEMORY", "USAGE")]
    [InlineData("MEMORY", "STATS")]
    public void MemorySubCommand_OnlyUsageIsAllowed(string command, string sub)
    {
        var verdict = RedisCommandSafety.Classify(command, [sub]);

        if (sub == "USAGE")
        {
            Assert.Equal(RedisVerdict.Allowed, verdict.Verdict);
        }
        else
        {
            Assert.Equal(RedisVerdict.Rejected, verdict.Verdict);
        }
    }

    [Fact]
    public void ObjectSubCommand_EncodingIdletimeFreqAllowed()
    {
        Assert.Equal(RedisVerdict.Allowed, RedisCommandSafety.Classify("OBJECT", ["ENCODING", "k"]).Verdict);
        Assert.Equal(RedisVerdict.Allowed, RedisCommandSafety.Classify("OBJECT", ["IDLETIME", "k"]).Verdict);
        Assert.Equal(RedisVerdict.Allowed, RedisCommandSafety.Classify("OBJECT", ["FREQ", "k"]).Verdict);
        Assert.Equal(RedisVerdict.Rejected, RedisCommandSafety.Classify("OBJECT", ["FLUSH", "k"]).Verdict);
    }

    [Fact]
    public void CompoundCommandWord_StillCheckedAgainstBothLists()
    {
        // "MEMORY USAGE" typed as one string is allowed; "MEMORY STATS" as one
        // string is rejected on the sub-word.
        Assert.Equal(RedisVerdict.Allowed, RedisCommandSafety.Classify("MEMORY USAGE", []).Verdict);
        Assert.Equal(RedisVerdict.Rejected, RedisCommandSafety.Classify("MEMORY STATS", []).Verdict);
    }

    [Fact]
    public void EmptyCommand_IsRejected()
    {
        var verdict = RedisCommandSafety.Classify("", []);

        Assert.Equal(RedisVerdict.Rejected, verdict.Verdict);
        var blank = RedisCommandSafety.Classify(null, null);
        Assert.Equal(RedisVerdict.Rejected, blank.Verdict);
    }

    [Fact]
    public void TooManyArguments_IsRejected()
    {
        var args = Enumerable.Range(0, 17).Select(i => "a" + i).ToList();
        var verdict = RedisCommandSafety.Classify("MGET", args);

        Assert.Equal(RedisVerdict.Rejected, verdict.Verdict);
        Assert.Contains("too many arguments", verdict.Reason);
    }

    [Fact]
    public void ArgumentOver512Bytes_IsRejected()
    {
        var fat = new string('x', 513);
        var verdict = RedisCommandSafety.Classify("GET", [fat]);

        Assert.Equal(RedisVerdict.Rejected, verdict.Verdict);
        Assert.Contains("argument too large", verdict.Reason);
    }

    [Fact]
    public void ArgumentWithNul_IsRejected()
    {
        var verdict = RedisCommandSafety.Classify("GET", ["a\0b"]);

        Assert.Equal(RedisVerdict.Rejected, verdict.Verdict);
        Assert.Contains("NUL", verdict.Reason);
    }

    [Fact]
    public void Scan_IsAllowedByTheClassifier_ButNeverExecutedRaw()
    {
        // SCAN appears in the whitelist so the console can issue it, but spec 6
        // rule 6 says the service rewrites every scan into its own bounded
        // paging loop. The classifier says "allowed" and that is the whole of
        // its job; the rewrite lives in RedisKeyReadService.ExecuteCommandAsync,
        // which returns before the raw ExecuteAsync whenever the command is
        // SCAN. This test pins the classifier half only - it deliberately does
        // NOT claim SCAN never reaches the server, because proving that needs
        // the service, not the guard.
        var verdict = RedisCommandSafety.Classify("SCAN", ["0", "MATCH", "*"]);
        Assert.Equal(RedisVerdict.Allowed, verdict.Verdict);
    }
}
