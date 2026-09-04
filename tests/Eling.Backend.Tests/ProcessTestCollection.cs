using System.Collections.Generic;
using System.Text.Json;

namespace Eling.Backend.Tests;

/// <summary>
/// Process-based tests contend for 127.0.0.1:4317 — they must never run in
/// parallel with each other.
/// </summary>
[CollectionDefinition("ProcessTests", DisableParallelization = true)]
public sealed class ProcessTestCollection;
