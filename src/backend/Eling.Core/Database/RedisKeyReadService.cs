using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Eling.Core.Database;

public sealed class RedisKeyReadService : IRedisKeyReadService
{
    /// <summary>Spec 4.2's default page size, also the scan budget for a console SCAN.</summary>
    private const int DefaultPageSize = 200;

    private readonly IRedisConnectionPool _pool;
    private readonly IRedisReadCache _cache;
    private readonly ILogger<RedisKeyReadService>? _logger;

    public RedisKeyReadService(
        IRedisConnectionPool pool,
        IRedisReadCache cache,
        ILogger<RedisKeyReadService>? logger = null)
    {
        _pool = pool;
        _cache = cache;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RedisDatabaseInfo>> ListDatabasesAsync(
        DatabaseConnection connection, CancellationToken ct = default, bool preferCache = true)
    {
        if (preferCache
            && _cache.TryGetDatabases(connection.Id, out var snapshot, out var snapshotAt)
            && snapshot is not null)
        {
            return snapshot
                .Select(db => db with { FromCache = true, CachedAt = snapshotAt })
                .ToList();
        }

        try
        {
            var live = await ListDatabasesLiveAsync(connection, ct);
            _cache.SetDatabases(connection.Id, live);
            return live;
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            if (_cache.TryGetDatabases(connection.Id, out var cached, out var cachedAt)
                && cached is not null)
            {
                _logger?.LogWarning(ex, "Redis unavailable; serving cached database list for {Id}", connection.Id);
                return cached
                    .Select(db => db with { FromCache = true, CachedAt = cachedAt })
                    .ToList();
            }

            throw;
        }
    }

    /// <summary>
    /// A failure that means "the server could not answer", as opposed to
    /// "the server answered no". A command the server rejected (WRONGTYPE, an
    /// unknown command) is a real answer and must not be papered over with a
    /// cached one, and a rejected password is our own misconfiguration: both
    /// must reach the caller as errors.
    /// </summary>
    private static bool IsUnreachable(Exception ex) =>
        (ex is RedisConnectionException or TimeoutException) &&
        !IsCredentialFailure(ex);

    /// <summary>
    /// How long a cached inspection's key still has, measured from its absolute
    /// expiry. A key with no expiry keeps Redis's own -1.
    /// </summary>
    private static long RemainingTtl(KeyInspectResult cached)
    {
        if (cached.ExpiresAt is not { } expires) return cached.TtlSeconds;

        var left = expires - DateTimeOffset.UtcNow;
        return left <= TimeSpan.Zero ? 0 : (long)left.TotalSeconds;
    }

    /// <summary>
    /// The server refusing our credentials. It says the stored password is
    /// wrong, not that the server is down, so a cached answer would hide the
    /// thing the reader has to fix.
    /// </summary>
    private static bool IsCredentialFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e.Message.Contains("NOAUTH", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("WRONGPASS", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<IReadOnlyList<RedisDatabaseInfo>> ListDatabasesLiveAsync(
        DatabaseConnection connection, CancellationToken ct = default)
    {
        var mux = await _pool.GetAsync(connection, ct);
        var server = GetServer(mux);
        EnsureStandalone(server);

        var result = new List<RedisDatabaseInfo>();
        // Parallel: sixteen serial round-trips doubled the latency for no
        // reason, and none of them could be cancelled mid-wait.
        var probes = new List<Task<RedisDatabaseInfo?>>(RedisDatabaseIndex.Max - RedisDatabaseIndex.Min + 1);
        for (var index = RedisDatabaseIndex.Min; index <= RedisDatabaseIndex.Max; index++)
        {
            var i = index;
            probes.Add(DatabaseSizeAsync(i));
        }
        var settled = await Task.WhenAll(probes);
        foreach (var probe in settled)
        {
            if (probe is not null) result.Add(probe);
        }

        if (result.Count == 0)
        {
            // default db0
            result.Add(new RedisDatabaseInfo(0, 0));
        }
        return result;

        async Task<RedisDatabaseInfo?> DatabaseSizeAsync(int index)
        {
            var size = await server.DatabaseSizeAsync(index, CommandFlags.None);
            // A cancelled list is not a short list: letting a probe return null
            // here would cache a half-scanned keyspace as the truth for a day.
            ct.ThrowIfCancellationRequested();
            return size > 0 ? new RedisDatabaseInfo(index, size) : null;
        }
    }

    public async Task<KeyPage> ScanAsync(
        DatabaseConnection connection, RedisDatabaseIndex database, string prefix, long cursor, int pageOffset, int pageSize, CancellationToken ct = default, bool preferCache = true)
    {
        prefix ??= string.Empty;
        if (prefix.Length > 1024) throw new ArgumentException("prefix too long");

        pageSize = Math.Clamp(pageSize, 1, 500);

        if (preferCache
            && _cache.TryGetPage(connection.Id, database, prefix, cursor, pageOffset, pageSize, out var snapshot, out var snapshotAt)
            && snapshot is not null)
        {
            return PageWithCurrentTtl(snapshot) with { FromCache = true, CachedAt = snapshotAt };
        }

        try
        {
            var live = await ScanLiveAsync(connection, database, prefix, cursor, pageOffset, pageSize, ct);
            // The live scan is what the reader gets, but it is not what the tree
            // may take blindly: a key that left the keyspace comes back flagged,
            // not erased, because the page is what the reader is watching.
            var merged = _cache.SetPage(connection.Id, database, prefix, cursor, pageOffset, pageSize, live);
            return merged;
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            if (_cache.TryGetPage(connection.Id, database, prefix, cursor, pageOffset, pageSize, out var cached, out var cachedAt)
                && cached is not null)
            {
                _logger?.LogWarning(ex, "Redis unavailable; serving cached scan page for {Id}", connection.Id);
                return PageWithCurrentTtl(cached) with { FromCache = true, CachedAt = cachedAt };
            }

            throw;
        }
    }

    private async Task<KeyPage> ScanLiveAsync(
        DatabaseConnection connection, RedisDatabaseIndex database, string prefix, long cursor, int pageOffset, int pageSize, CancellationToken ct = default)
    {

        var mux = await _pool.GetAsync(connection, ct);
        var server = GetServer(mux);
        EnsureStandalone(server);

        var db = mux.GetDatabase(database.Value);
        var pattern = string.IsNullOrEmpty(prefix) ? "*" : prefix + "*";

        var nodeMap = new Dictionary<string, KeyNode>(StringComparer.Ordinal);
        var leafKeys = new List<string>();
        var leafSet = new HashSet<string>(StringComparer.Ordinal);
        int scanned = 0;
        var sw = Stopwatch.StartNew();
        long newCursor = cursor;
        int newPageOffset = pageOffset;
        bool complete = false;

        IScanningCursor? scanning = null;
        try
        {
            var enumerable = server.KeysAsync(database.Value, pattern, pageSize, cursor, pageOffset);
            await using var enumerator = enumerable.GetAsyncEnumerator(ct);
            scanning = enumerator as IScanningCursor;

            while (await enumerator.MoveNextAsync())
            {
                scanned++;
                var key = enumerator.Current.ToString();
                var seg = KeyTreeSegmenter.Segment(prefix, key);
                if (seg.IsLeaf)
                {
                    if (leafSet.Add(key)) leafKeys.Add(key);
                }
                else
                {
                    if (!nodeMap.ContainsKey(seg.NodeSegment!))
                    {
                        nodeMap[seg.NodeSegment!] = new KeyNode(seg.NodeSegment!, seg.NodePrefix!);
                    }
                }

                var items = nodeMap.Count + leafKeys.Count;
                if (items >= pageSize) break;
                if (scanned >= 2000) break;
                if (sw.ElapsedMilliseconds >= 3000) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Scan failed");
            throw;
        }

        if (scanning is not null)
        {
            newCursor = scanning.Cursor;
            newPageOffset = scanning.PageOffset;
            complete = scanning.Cursor == 0 && scanning.PageOffset == 0;
        }
        else
        {
            complete = true;
        }

        // If we broke early due to budgets, not complete.
        if (scanned >= 2000 || sw.ElapsedMilliseconds >= 3000) complete = false;

        var leafList = await ReadLeavesAsync(db, leafKeys);

        return new KeyPage(prefix, nodeMap.Values.ToList(), leafList, newCursor, newPageOffset, complete, scanned);
    }

    private static KeyPage PageWithCurrentTtl(KeyPage page)
    {
        var now = DateTimeOffset.UtcNow;
        var keys = page.Keys
            .Select(key =>
            {
                var expires = key.ExpiresAt;
                if (expires is null)
                {
                    return key with { TtlSeconds = key.TtlSeconds < 0 ? -1L : 0 };
                }

                var left = expires.Value - now;
                return key with { TtlSeconds = left <= TimeSpan.Zero ? 0 : (long)left.TotalSeconds };
            })
            .ToList();

        return page with { Keys = keys };
    }

    /// <summary>
    /// TYPE and TTL for each leaf, pipelined in one batch. A key that vanished
    /// between SCAN and TYPE (type None) is dropped rather than shown.
    /// </summary>
    private static async Task<List<KeyLeaf>> ReadLeavesAsync(IDatabase db, List<string> leafKeys)
    {
        var leafList = new List<KeyLeaf>();
        if (leafKeys.Count == 0) return leafList;

        var batch = db.CreateBatch();
        var typeTasks = new Dictionary<string, Task<RedisType>>();
        var ttlTasks = new Dictionary<string, Task<TimeSpan?>>();
        foreach (var k in leafKeys)
        {
            typeTasks[k] = batch.KeyTypeAsync(k);
            ttlTasks[k] = batch.KeyTimeToLiveAsync(k);
        }
        batch.Execute();
        var now = DateTimeOffset.UtcNow;
        foreach (var k in leafKeys)
        {
            var rt = await typeTasks[k];
            if (rt == RedisType.None) continue;
            var ttl = await ttlTasks[k];
            var ttlSec = ttl.HasValue ? (long)ttl.Value.TotalSeconds : -1L;
            var hasPositiveTtl = ttl.HasValue && ttl.Value > TimeSpan.Zero;
            DateTimeOffset? expiresAt = hasPositiveTtl ? now.Add(ttl.Value) : null;
            leafList.Add(new KeyLeaf(k, rt.ToString().ToLowerInvariant(), ttlSec, expiresAt));
        }
        return leafList;
    }

    public async Task<KeyInspectResult> InspectAsync(
        DatabaseConnection connection, RedisDatabaseIndex database, string key, CancellationToken ct = default)
    {
        try
        {
            var live = await InspectLiveAsync(connection, database, key, ct);
            _cache.SetKey(connection.Id, database, live);
            return live;
        }
        catch (KeyNotFoundException)
        {
            // The key is gone: a cached answer for it is now a lie.
            _cache.RemoveKey(connection.Id, database, key);
            throw;
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            if (_cache.TryGetKey(connection.Id, database, key, out var cached, out var cachedAt)
                && cached is not null)
            {
                _logger?.LogWarning(ex, "Redis unavailable; serving cached inspection for {Key}", key);
                return cached with
                {
                    FromCache = true,
                    CachedAt = cachedAt,
                    // The stored TTL is a snapshot of "seconds left" at write
                    // time; served an hour later it would overstate the key's
                    // life. The absolute expiry it was derived from still holds.
                    TtlSeconds = RemainingTtl(cached)
                };
            }

            throw;
        }
    }

    /// <summary>
    /// Whether a key is still there, and its TTL.
    ///
    /// One EXISTS and one TTL, live only: this answers "is the row on screen
    /// still true", so a cached answer would defeat its only purpose. A key
    /// that is gone also drops its cached inspection - the stored preview now
    /// describes a key nobody can open.
    /// </summary>
    public async Task<RedisKeyProbeResult> ProbeKeyAsync(
        DatabaseConnection connection, RedisDatabaseIndex database, string key, CancellationToken ct = default)
    {
        var mux = await _pool.GetAsync(connection, ct);
        EnsureStandalone(GetServer(mux));
        var db = mux.GetDatabase(database.Value);

        var exists = await db.KeyExistsAsync(key);
        if (!exists)
        {
            _cache.RemoveKey(connection.Id, database, key);
            return new RedisKeyProbeResult(key, false, -1L, null);
        }

        var ttl = await db.KeyTimeToLiveAsync(key);
        var ttlSec = ttl.HasValue ? (long)ttl.Value.TotalSeconds : -1L;
        DateTimeOffset? expires = ttl.HasValue && ttl.Value > TimeSpan.Zero
            ? DateTimeOffset.UtcNow.Add(ttl.Value)
            : null;

        return new RedisKeyProbeResult(key, true, ttlSec, expires);
    }

    /// <summary>
    /// The same question for many keys, in one pipelined round trip.
    ///
    /// Short-lived keys expire in bunches - a job finishing, a queue draining -
    /// and asking one at a time would turn that single moment into a burst of
    /// round trips that all say the same thing. EXISTS and TTL are both sent
    /// together in a batch, so the cost is one round trip regardless of how
    /// many keys came due.
    /// </summary>
    public async Task<IReadOnlyList<RedisKeyProbeResult>> ProbeKeysAsync(
        DatabaseConnection connection, RedisDatabaseIndex database, IReadOnlyList<string> keys, CancellationToken ct = default)
    {
        if (keys.Count == 0) return [];

        var mux = await _pool.GetAsync(connection, ct);
        EnsureStandalone(GetServer(mux));
        var db = mux.GetDatabase(database.Value);

        // A key named twice is one key: the dictionaries below are keyed by it.
        var wanted = keys.Distinct(StringComparer.Ordinal).ToList();

        var batch = db.CreateBatch();
        var existsTasks = new Dictionary<string, Task<bool>>(wanted.Count, StringComparer.Ordinal);
        var ttlTasks = new Dictionary<string, Task<TimeSpan?>>(wanted.Count, StringComparer.Ordinal);
        foreach (var key in wanted)
        {
            existsTasks[key] = batch.KeyExistsAsync(key);
            ttlTasks[key] = batch.KeyTimeToLiveAsync(key);
        }
        batch.Execute();

        var now = DateTimeOffset.UtcNow;
        var results = new List<RedisKeyProbeResult>(wanted.Count);
        foreach (var key in wanted)
        {
            if (!await existsTasks[key])
            {
                // The row on screen describes a key nobody can open any more.
                _cache.RemoveKey(connection.Id, database, key);
                results.Add(new RedisKeyProbeResult(key, false, -1L, null));
                continue;
            }

            var ttl = await ttlTasks[key];
            var ttlSec = ttl.HasValue ? (long)ttl.Value.TotalSeconds : -1L;
            DateTimeOffset? expires = ttl.HasValue && ttl.Value > TimeSpan.Zero
                ? now.Add(ttl.Value)
                : null;

            results.Add(new RedisKeyProbeResult(key, true, ttlSec, expires));
        }

        return results;
    }

    private async Task<KeyInspectResult> InspectLiveAsync(
        DatabaseConnection connection, RedisDatabaseIndex database, string key, CancellationToken ct = default)
    {
        var mux = await _pool.GetAsync(connection, ct);
        var server = GetServer(mux);
        EnsureStandalone(server);
        var db = mux.GetDatabase(database.Value);

        var type = await db.KeyTypeAsync(key, CommandFlags.None);
        if (type == RedisType.None)
        {
            throw new KeyNotFoundException("key not found");
        }

        var ttl = await db.KeyTimeToLiveAsync(key);
        var ttlSec = ttl.HasValue ? (long)ttl.Value.TotalSeconds : -1L;
        DateTimeOffset? expires = ttl.HasValue && ttl.Value > TimeSpan.Zero
            ? DateTimeOffset.UtcNow.Add(ttl.Value)
            : null;

        long length = 0;
        try
        {
            length = type switch
            {
                RedisType.String => (long)await db.StringLengthAsync(key),
                RedisType.List => await db.ListLengthAsync(key),
                RedisType.Set => await db.SetLengthAsync(key),
                RedisType.SortedSet => await db.SortedSetLengthAsync(key),
                RedisType.Hash => await db.HashLengthAsync(key),
                RedisType.Stream => await db.StreamLengthAsync(key),
                _ => 0
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            // The server stopped answering mid-read. Continuing would produce a
            // plausible-looking length of 0, and that would be cached as truth.
            throw;
        }
        catch { length = 0; }

        string? encoding = null;
        try
        {
            var res = await db.ExecuteAsync("OBJECT", "ENCODING", key);
            encoding = res.IsNull ? null : res.ToString();
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            throw;
        }
        catch { encoding = null; }

        var preview = await BuildPreview(db, type, key);

        return new KeyInspectResult(key, type.ToString().ToLowerInvariant(), ttlSec,
            expires, length, encoding, preview);
    }

    private async Task<KeyInspectPreview> BuildPreview(IDatabase db, RedisType type, string key)
    {
        const int MaxEntries = 100;
        const int MaxBytes = 64 * 1024;
        // A 64 KB cap measured in characters cuts surrogate pairs in half.
        // Count bytes like the cap is stated; the per-type entry limits
        // above keep the result small, this only guards a fat string value.
        var entries = new List<KeyInspectEntry>();
        var bytes = 0;
        bool truncated = false;

        switch (type)
        {
            case RedisType.String:
                {
                    var v = await db.StringGetAsync(key);
                    var s = v.ToString();
                    if (System.Text.Encoding.UTF8.GetByteCount(s) > MaxBytes)
                    {
                        // Walk to the largest character run that still fits
                        // the byte cap; the loop stops before a cut that
                        // would leave a lone surrogate at the end.
                        var chars = 0;
                        while (chars < s.Length)
                        {
                            var next = chars + 1;
                            if (System.Text.Encoding.UTF8.GetByteCount(s.AsSpan(0, next)) > MaxBytes) break;
                            chars = next;
                        }
                        s = s.Substring(0, chars);
                        truncated = true;
                    }
                    entries.Add(new KeyInspectEntry(null, s));
                    break;
                }
            case RedisType.List:
                {
                    var vals = await db.ListRangeAsync(key, 0, MaxEntries - 1);
                    foreach (var v in vals) { if (entries.Count >= MaxEntries) { truncated = true; break; } entries.Add(new KeyInspectEntry(null, v.ToString())); }
                    var len = await db.ListLengthAsync(key);
                    if (len > MaxEntries) truncated = true;
                    break;
                }
            case RedisType.Set:
                {
                    await foreach (var v in db.SetScanAsync(key, pageSize: MaxEntries))
                    {
                        if (entries.Count >= MaxEntries) { truncated = true; break; }
                        entries.Add(new KeyInspectEntry(null, v.ToString()));
                    }
                    break;
                }
            case RedisType.SortedSet:
                {
                    var vals = await db.SortedSetRangeByRankWithScoresAsync(key, 0, MaxEntries - 1, Order.Ascending, CommandFlags.None);
                    foreach (var v in vals)
                    {
                        if (entries.Count >= MaxEntries) { truncated = true; break; }
                        entries.Add(new KeyInspectEntry(v.Element.ToString(), v.Score.ToString()));
                    }
                    break;
                }
            case RedisType.Hash:
                {
                    var all = await db.HashGetAllAsync(key, CommandFlags.None);
                    foreach (var h in all)
                    {
                        if (entries.Count >= MaxEntries) { truncated = true; break; }
                        entries.Add(new KeyInspectEntry(h.Name.ToString(), h.Value.ToString()));
                    }
                    if (all.Length > MaxEntries) truncated = true;
                    break;
                }
            case RedisType.Stream:
                {
                    var msgs = await db.StreamRangeAsync(key, "-", "+", count: MaxEntries);
                    foreach (var m in msgs)
                    {
                        if (entries.Count >= MaxEntries) { truncated = true; break; }
                        entries.Add(new KeyInspectEntry(m.Id.ToString(), string.Join(",", m.Values.Select(v => v.Name + "=" + v.Value))));
                    }
                    break;
                }
        }

        // byte cap check
        foreach (var e in entries)
        {
            bytes += System.Text.Encoding.UTF8.GetByteCount(e.Field ?? string.Empty) + System.Text.Encoding.UTF8.GetByteCount(e.Value ?? string.Empty);
        }
        if (bytes > MaxBytes) truncated = true;

        return new KeyInspectPreview(type.ToString().ToLowerInvariant(), entries, truncated);
    }

    public async Task<RedisKeyDeleteResult> DeleteKeyAsync(
        DatabaseConnection connection, RedisDatabaseIndex database, string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("key must not be blank");
        }

        try
        {
            var mux = await _pool.GetAsync(connection, ct);
            var server = GetServer(mux);
            EnsureStandalone(server);
            var db = mux.GetDatabase(database.Value);

            var deleted = await db.KeyDeleteAsync(key, CommandFlags.None);
            // A cached inspection of a gone key is a lie either way: evict on the
            // way out so an offline read cannot resurrect it.
            _cache.RemoveKey(connection.Id, database, key);
            if (!deleted)
            {
                throw new KeyNotFoundException("key not found");
            }

            return new RedisKeyDeleteResult(true);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            // Deletes have no cached answer to fall back to, so the failure is
            // classified and surfaced like the reads': unreachable and
            // credential failures both reach the route ladder as themselves.
            _logger?.LogWarning(ex, "Redis unavailable during key delete for {Key}", key);
            throw;
        }
    }

    public async Task<RedisPrefixPreviewResult> PreviewPrefixAsync(
        DatabaseConnection connection, RedisDatabaseIndex database, string prefix, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new ArgumentException("prefix must not be blank");
        }

        try
        {
            var mux = await _pool.GetAsync(connection, ct);
            var server = GetServer(mux);
            EnsureStandalone(server);

            var pattern = prefix + "*";
            var keys = new List<string>();
            long total = 0;
            var truncated = false;
            var sw = Stopwatch.StartNew();

            var enumerable = server.KeysAsync(database.Value, pattern, DefaultPageSize, 0, 0);
            await using var enumerator = enumerable.GetAsyncEnumerator(ct);
            while (await enumerator.MoveNextAsync())
            {
                total++;
                var key = enumerator.Current.ToString();
                if (keys.Count < 100)
                {
                    keys.Add(key);
                }
                else
                {
                    truncated = true;
                }

                if (total >= 2000)
                {
                    truncated = true;
                    break;
                }

                if (sw.ElapsedMilliseconds >= 3000)
                {
                    truncated = true;
                    break;
                }
            }

            return new RedisPrefixPreviewResult(prefix, keys, total, truncated);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            // A preview is a read, but the deletes' budget scan has no cache
            // fallback either: the unreachable failure surfaces as itself so
            // the route ladder answers it.
            _logger?.LogWarning(ex, "Redis unavailable during prefix preview for {Prefix}", prefix);
            throw;
        }
    }

    public async Task<RedisPrefixDeleteResult> DeletePrefixAsync(
        DatabaseConnection connection, RedisDatabaseIndex database, string prefix, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new ArgumentException("prefix must not be blank");
        }

        try
        {
            var mux = await _pool.GetAsync(connection, ct);
            var server = GetServer(mux);
            EnsureStandalone(server);
            var db = mux.GetDatabase(database.Value);

            var pattern = prefix + "*";
            long scanned = 0;
            long deleted = 0;
            var truncated = false;
            var sw = Stopwatch.StartNew();

            var enumerable = server.KeysAsync(database.Value, pattern, DefaultPageSize, 0, 0);
            await using var enumerator = enumerable.GetAsyncEnumerator(ct);
            while (await enumerator.MoveNextAsync())
            {
                scanned++;
                var key = enumerator.Current.ToString();
                if (await db.KeyDeleteAsync(key, CommandFlags.None))
                {
                    deleted++;
                    _cache.RemoveKey(connection.Id, database, key);
                }

                if (scanned >= 2000)
                {
                    truncated = true;
                    break;
                }

                if (sw.ElapsedMilliseconds >= 3000)
                {
                    truncated = true;
                    break;
                }
            }

            return new RedisPrefixDeleteResult(deleted, truncated);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            // A subtree delete cannot be finished from a cache, so the
            // unreachable failure surfaces as itself; work already done stays
            // deleted and its cache entries stay evicted.
            _logger?.LogWarning(ex, "Redis unavailable during prefix delete for {Prefix}", prefix);
            throw;
        }
    }

    /// <summary>
    /// Answers a console <c>SCAN</c> from the service's own bounded paging path
    /// (spec 6 rule 6) instead of executing it. The console may name a
    /// <c>MATCH</c> pattern and a <c>COUNT</c>; the <c>COUNT</c> is ignored
    /// because the page size is the budget, which is the whole point of not
    /// running the command raw. A cursor argument is honoured so a multi-page
    /// walk still works, but the reply carries the cursor to use next.
    /// </summary>
    private async Task<RedisCommandResult> ScanAsCommandAsync(
        DatabaseConnection connection,
        RedisDatabaseIndex database,
        IReadOnlyList<string>? args,
        CancellationToken ct)
    {
        long cursor = 0;
        var pattern = string.Empty;

        for (var i = 1; i < (args?.Count ?? 0); i++)
        {
            var arg = args![i];
            if (arg.Equals("MATCH", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                pattern = args[++i];
            }
            else if (arg.Equals("COUNT", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
            {
                // Deliberately not read: the page size is the budget.
                i++;
            }
            else if (i == 1 && long.TryParse(arg, out var parsedCursor))
            {
                cursor = parsedCursor;
            }
        }

        // "user:*" and "user:" are the two shapes a console user types; the
        // paging path takes a prefix and appends its own wildcard.
        //
        // The glob check runs BEFORE the service asks the pool for a
        // connection. Validating after the await would make the refusal depend
        // on the server being reachable, so a bad pattern would surface as a
        // timeout instead of the message telling the caller where to go.
        var prefix = pattern.TrimEnd('*');
        if (prefix.Contains('?') || prefix.Contains('[') || (pattern.Contains('*') && !pattern.EndsWith('*')))
            throw new ArgumentException("SCAN via the console supports a prefix pattern only; use the key tree for globs");

        var page = await ScanAsync(connection, database, prefix, cursor, 0, DefaultPageSize, ct, preferCache: false);

        // Spec 5.2: these nodes are not a complete level, only the ones this
        // one page's scan happened to cross - the rest arrive on a later page.
        // Concatenating them with the identical segment from another page would
        // list one folder twice.
        var keys = new List<object?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var leaf in page.Keys)
        {
            if (seen.Add(leaf.Key)) keys.Add(leaf.Key);
        }

        return new RedisCommandResult(
            "scan",
            new { cursor = page.Complete ? 0 : page.Cursor, keys, complete = page.Complete });
    }

    public async Task<RedisCommandResult> ExecuteCommandAsync(
        DatabaseConnection connection, RedisDatabaseIndex database, string command, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var classification = RedisCommandSafety.Classify(command, args);
        if (classification.Verdict != RedisVerdict.Allowed)
        {
            throw new ArgumentException(classification.Reason ?? "command not allowed");
        }

        var cmd = command.Trim().ToUpperInvariant();

        // Spec 6 rule 6: SCAN never runs raw through the console. Executed as
        // written, "SCAN 0 COUNT 100000000" is a denial of service, and the
        // cursor it returns is meaningless to a client that does not own the
        // paging loop. It is answered from the service's own bounded scan
        // instead, so the console gets a real page and cannot ask for more.
        //
        // This branch sits ahead of the pool lookup on purpose: below it, a bad
        // MATCH pattern would ask for a connection first and surface the pool's
        // failure instead of the message telling the caller where to go.
        if (cmd == "SCAN")
            return await ScanAsCommandAsync(connection, database, args, ct);

        var mux = await _pool.GetAsync(connection, ct);
        var server = GetServer(mux);

        EnsureStandalone(server);

        // INFO section restriction
        var execArgs = args?.ToList() ?? new List<string>();
        if (cmd == "INFO")
        {
            var allowedSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "server","clients","memory","persistence","stats","replication","keyspace" };
            if (execArgs.Count == 0)
            {
                execArgs = allowedSections.ToList();
            }
            else
            {
                execArgs = execArgs.Where(a => allowedSections.Contains(a)).ToList();
                if (execArgs.Count == 0) throw new ArgumentException("no allowed INFO sections");
            }
        }

        var db = mux.GetDatabase(database.Value);
        var result = await db.ExecuteAsync(command, execArgs.ToArray());

        return Convert(result);
    }

    private static RedisCommandResult Convert(RedisResult r)
    {
        if (r.IsNull) return new RedisCommandResult("nil", null);
        if (r.Resp2Type == ResultType.Array || r.Resp2Type == ResultType.Push || r.Resp2Type == ResultType.Set)
        {
            var list = new List<object?>();
            for (int i = 0; i < r.Length; i++) list.Add(ConvertValue(r[i]));
            return new RedisCommandResult("array", list);
        }
        return new RedisCommandResult("scalar", ConvertValue(r));
    }

    private static object? ConvertValue(RedisResult r)
    {
        if (r.IsNull) return null;
        if (r.Resp2Type == ResultType.Array || r.Resp2Type == ResultType.Push || r.Resp2Type == ResultType.Set)
        {
            var list = new List<object?>();
            for (int i = 0; i < r.Length; i++) list.Add(ConvertValue(r[i]));
            return list;
        }
        return r.ToString();
    }

    private static IServer GetServer(IConnectionMultiplexer mux)
    {
        var endpoints = mux.GetEndPoints();
        if (endpoints.Length == 0) throw new InvalidOperationException("no endpoints");
        return mux.GetServer(endpoints[0]);
    }

    private static void EnsureStandalone(IServer server)
    {
        var type = server.ServerType;
        if (type == ServerType.Cluster || type == ServerType.Sentinel)
        {
            throw new NotSupportedException(RedisRequirement.StandaloneOnly);
        }
    }
}
