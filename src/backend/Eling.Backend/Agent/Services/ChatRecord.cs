using System;
using System.Collections.Generic;
using Eling.Backend.Agent.Ports;

namespace Eling.Backend.Agent.Services;

/// <summary>
/// A stored conversation as it is persisted: the row fields plus the flattened
/// message history, both of which the store round-trips through JSON.
/// </summary>
public sealed class ChatRecord
{
    public string Id { get; set; } = string.Empty;
    public string Workspace { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
    public List<AgentMessage> Messages { get; set; } = [];
}
