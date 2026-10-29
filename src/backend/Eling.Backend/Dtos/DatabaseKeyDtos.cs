namespace Eling.Backend.Dtos;

public sealed record RedisCommandRequest(
    int Database = 0,
    string Command = "",
    List<string>? Args = null);