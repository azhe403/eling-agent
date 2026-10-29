namespace Eling.Core.Database;

public static class RedisCommandSafety
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        // string
        "GET","MGET","STRLEN","GETRANGE","TYPE","TTL","PTTL","EXISTS",
        // hash
        "HGET","HMGET","HGETALL","HKEYS","HVALS","HLEN","HEXISTS","HRANDFIELD",
        // list
        "LRANGE","LLEN","LINDEX","LPOS",
        // set
        "SMEMBERS","SCARD","SISMEMBER","SRANDMEMBER","SSCAN",
        // sorted set
        "ZRANGE","ZRANGEBYSCORE","ZREVRANGE","ZCARD","ZSCORE","ZCOUNT","ZSCAN",
        // stream
        "XRANGE","XREVRANGE","XLEN","XINFO",
        // server/keyspace
        "SCAN","DBSIZE","PING","ECHO","TIME","RANDOMKEY",
        // memory/object
        "MEMORY","OBJECT",
        // info
        "INFO",
    };

    private static readonly HashSet<string> DeniedVerbs = new(StringComparer.Ordinal)
    {
        "KEYS","MONITOR","SUBSCRIBE","PSUBSCRIBE","SSUBSCRIBE","PSYNC","SYNC",
        "WAIT","CLIENT","CLUSTER","COMMAND","LATENCY","SLOWLOG","DEBUG","CONFIG",
        "ACL","SCRIPT","FUNCTION","EVAL","EVALSHA","FCALL","SHUTDOWN","FLUSHALL",
        "FLUSHDB","SWAPDB","MIGRATE","RESTORE","DUMP","BITFIELD","GETDEL","GETEX",
    };

    private const int MaxArgs = 16;
    private const int MaxArgBytes = 512;

    public static RedisClassification Classify(string? command, IReadOnlyList<string>? args)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new RedisClassification(RedisVerdict.Rejected, string.Empty, "empty command");
        }

        var cmd = command.Trim().ToUpperInvariant();
        // Normalize multi-word like "MEMORY USAGE" stays two tokens in command? We accept command as first token;
        // callers may pass "MEMORY" with args ["USAGE", ...]. Handle "OBJECT ENCODING" similarly.

        if (DeniedVerbs.Contains(cmd))
        {
            return new RedisClassification(RedisVerdict.Rejected, cmd, $"command not allowed: {cmd}");
        }

        // Handle compound commands passed as single string "MEMORY USAGE"
        var parts = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1)
        {
            // If the whole thing is a denied verb prefix, reject.
            if (DeniedVerbs.Contains(parts[0]))
            {
                return new RedisClassification(RedisVerdict.Rejected, cmd, $"command not allowed: {parts[0]}");
            }
        }

        var verb = parts[0];
        if (!Allowed.Contains(verb))
        {
            return new RedisClassification(RedisVerdict.Rejected, cmd, $"command not allowed: {cmd}");
        }

        // Sub-command validation for MEMORY / OBJECT / INFO
        if (verb == "MEMORY")
        {
            var sub = parts.Length > 1 ? parts[1] : (args is { Count: > 0 } ? args[0].ToUpperInvariant() : string.Empty);
            if (sub != "USAGE")
            {
                return new RedisClassification(RedisVerdict.Rejected, cmd, "command not allowed: MEMORY " + sub);
            }
        }
        if (verb == "OBJECT")
        {
            var sub = parts.Length > 1 ? parts[1] : (args is { Count: > 0 } ? args[0].ToUpperInvariant() : string.Empty);
            if (sub is not ("ENCODING" or "IDLETIME" or "FREQ"))
            {
                return new RedisClassification(RedisVerdict.Rejected, cmd, "command not allowed: OBJECT " + sub);
            }
        }
        if (verb == "INFO")
        {
            // section filtering happens at execution; allow any INFO here.
        }

        // SCAN via console is not executed raw: reject if COUNT too large? We allow SCAN but service rewrites.
        // Arg sanity
        if (args is not null)
        {
            if (args.Count > MaxArgs)
            {
                return new RedisClassification(RedisVerdict.Rejected, cmd, "too many arguments");
            }
            foreach (var a in args)
            {
                if (a is null) continue;
                if (a.Contains('\0'))
                {
                    return new RedisClassification(RedisVerdict.Rejected, cmd, "argument contains NUL");
                }
                var bytes = System.Text.Encoding.UTF8.GetByteCount(a);
                if (bytes > MaxArgBytes)
                {
                    return new RedisClassification(RedisVerdict.Rejected, cmd, "argument too large");
                }
            }
        }

        // INFO section restriction note: handled by caller filtering; allow here.
        return new RedisClassification(RedisVerdict.Allowed, cmd, null);
    }
}