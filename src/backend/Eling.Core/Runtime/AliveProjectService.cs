using Eling.Core.Memory;

namespace Eling.Core.Runtime;

/// <summary>One live project runtime paired with its memory service.</summary>
public sealed record AliveProjectService(
    RuntimeInfo Runtime,
    IMemoryService Service);
