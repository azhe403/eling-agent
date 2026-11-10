namespace Eling.Backend.Dtos;

/// <summary>
/// A recheck for several keys at once.
///
/// The dashboard collects keys whose countdown reached zero for a moment and
/// then asks about all of them together: a database with short-lived keys
/// expires them in bunches, and one request per key would turn that instant
/// into a burst of calls.
/// </summary>
public sealed record RedisKeyProbeBatchRequest(
    int Database = 0,
    List<string>? Keys = null);
