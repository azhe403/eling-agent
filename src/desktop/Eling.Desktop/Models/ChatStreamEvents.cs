namespace Eling.Desktop.Models;

public abstract record ChatStreamEvent;

public record StreamStarted(string ChatId) : ChatStreamEvent;

public record StreamDelta(string Delta) : ChatStreamEvent;

public record StreamTool(string Name, string Arguments) : ChatStreamEvent;

public record StreamDone(TurnDto Turn) : ChatStreamEvent;

public record StreamFailed(string Message) : ChatStreamEvent;
