using Eling.Core.Scope;

namespace Eling.Core.Memory;

/// <summary>One scope-chain level: the scope and its dedicated memory service.</summary>
public sealed record ProjectLevel(ProjectScope Scope, IMemoryService Service);
