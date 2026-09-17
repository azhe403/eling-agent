using Eling.Backend.Dtos;

namespace Eling.Backend.Agent.Services;

public abstract record TurnStreamEvent;

public record TurnStarted(string ChatId) : TurnStreamEvent;

public record TurnTextDelta(string Delta) : TurnStreamEvent;

public record TurnToolCall(ToolCallDto Call) : TurnStreamEvent;

public record TurnDone(TurnResponse Response) : TurnStreamEvent;

public record TurnFailed(string Message) : TurnStreamEvent;
