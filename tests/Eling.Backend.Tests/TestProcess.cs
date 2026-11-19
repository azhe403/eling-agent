using System.Collections.Concurrent;
using System.Diagnostics;

namespace Eling.Backend.Tests;

/// <summary>
/// Handles an isolated MCP runtime child as spawned by
/// <see cref="TestProcesses.Start"/>: the CliWrap process handle (kept for
/// Kill/HasExited/WaitForExitAsync) and the pumped stdout/stderr line queues
/// the tests read instead of touching raw pipe streams. Stdin writes go
/// through <see cref="Stdin"/>.
/// </summary>
public sealed record TestProcess(
    Process Process,
    BlockingCollection<string> Stdout,
    BlockingCollection<string> Stderr,
    TestStdinStream Stdin);
