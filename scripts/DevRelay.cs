using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

// Enforce UTF-8 without BOM on console streams
Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);

var repoRoot = ResolveRepoRoot();
var backendCsproj = Path.Combine(repoRoot, "src", "backend", "Eling.Backend", "Eling.Backend.csproj");
var binOpencode = Path.Combine(repoRoot, ".bin-opencode");
var slotA = Path.Combine(repoRoot, ".bin-dev-a");
var slotB = Path.Combine(repoRoot, ".bin-dev-b");
var logPath = Path.Combine(repoRoot, ".bin-dev-relay.log");

string currentSlot = "a";
Process? currentChild = null;
StreamWriter? activeChildIn = null;
string? cachedInitialize = null;
bool isRestarting = false;
bool isBuilding = false;

var relayLock = new SemaphoreSlim(1, 1);
var pendingQueue = new ConcurrentQueue<string>();
var outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

void Log(string message)
{
    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
    try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
    Console.Error.WriteLine(line);
}

Log($"[DevRelay] Starting. Repo root: {repoRoot}");

// Ensure initial binary exists in slot A
EnsureInitialSlot(binOpencode, slotA, backendCsproj, repoRoot);

// Graceful cleanup on process exit
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    try
    {
        if (currentChild != null && !currentChild.HasExited)
        {
            currentChild.Kill(entireProcessTree: true);
        }
    }
    catch { }
};

// 1. Start outbox consumer (writes to OpenCode stdout)
_ = Task.Run(async () =>
{
    var clientOut = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
    await foreach (var line in outbox.Reader.ReadAllAsync())
    {
        await clientOut.WriteLineAsync(line);
    }
});

// 2. Start initial child backend
await StartChildAsync(currentSlot, slotA, isReplay: false);

// 3. Keep FileSystemWatcher and Timer rooted in top-level variables (prevent GC collection!)
var backendDir = Path.Combine(repoRoot, "src", "backend");
FileSystemWatcher? watcher = null;
Timer? debounceTimer = null;
var timerLock = new object();

if (Directory.Exists(backendDir))
{
    watcher = new FileSystemWatcher(backendDir)
    {
        IncludeSubdirectories = true,
        EnableRaisingEvents = true,
        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
    };

    void OnChange(object sender, FileSystemEventArgs e)
    {
        var path = e.FullPath;

        // Ignore build artifacts, temp files, obj/bin folders
        if (path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var ext = Path.GetExtension(path);
        if (!string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(ext, ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (timerLock)
        {
            debounceTimer?.Dispose();
            debounceTimer = new Timer(_ =>
            {
                _ = TriggerRebuildAsync();
            }, null, 600, Timeout.Infinite);
        }
    }

    watcher.Changed += OnChange;
    watcher.Created += OnChange;
    watcher.Deleted += OnChange;
    watcher.Renamed += (s, e) => OnChange(s, e);
    Log($"[DevRelay] FileSystemWatcher active on {backendDir}");
}
else
{
    Log($"[DevRelay] WARNING: Backend dir not found: {backendDir}");
}

// 4. Main inbox loop (reads from OpenCode stdin)
var clientIn = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
while (await clientIn.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line)) continue;

    if (cachedInitialize == null && line.Contains("\"method\"") && line.Contains("\"initialize\""))
    {
        cachedInitialize = line;
        Log("[DevRelay] Captured MCP initialize handshake from client.");
    }

    await relayLock.WaitAsync();
    try
    {
        if (activeChildIn != null && !isRestarting)
        {
            await activeChildIn.WriteLineAsync(line);
            await activeChildIn.FlushAsync();
        }
        else
        {
            pendingQueue.Enqueue(line);
        }
    }
    finally
    {
        relayLock.Release();
    }
}

// Client disconnected: clean up child
try
{
    if (currentChild != null && !currentChild.HasExited)
    {
        currentChild.Kill(entireProcessTree: true);
    }
}
catch { }

Log("[DevRelay] OpenCode disconnected. Shutting down.");
return 0;

// ============================================================================
// Helper Methods
// ============================================================================

async Task TriggerRebuildAsync()
{
    if (isBuilding)
    {
        Log("[DevRelay] Build already in progress, skipping duplicate trigger.");
        return;
    }

    isBuilding = true;
    try
    {
        var nextSlot = (currentSlot == "a") ? "b" : "a";
        var nextDir = (nextSlot == "a") ? slotA : slotB;

        Log($"[DevRelay] File change detected. Compiling into slot {nextSlot.ToUpper()}...");

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(backendCsproj);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Debug");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(nextDir);
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("q");
        psi.ArgumentList.Add("--nologo");

        using var proc = Process.Start(psi);
        if (proc == null) return;

        // Read stdout and stderr concurrently to prevent pipe buffer deadlocks!
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        await Task.WhenAll(outTask, errTask, proc.WaitForExitAsync());

        var outText = await outTask;
        var errText = await errTask;

        if (proc.ExitCode != 0)
        {
            Log($"[DevRelay] Build FAILED (exit code {proc.ExitCode}). Current backend on slot {currentSlot.ToUpper()} remains active.");
            if (!string.IsNullOrWhiteSpace(errText)) Log($"[DevRelay Build Err] {errText.Trim()}");
            if (!string.IsNullOrWhiteSpace(outText)) Log($"[DevRelay Build Out] {outText.Trim()}");
            return;
        }

        Log($"[DevRelay] Build SUCCEEDED. Auto-applying changes to slot {nextSlot.ToUpper()}...");
        await RestartChildAsync(nextSlot, nextDir);
    }
    catch (Exception ex)
    {
        Log($"[DevRelay] Error during rebuild: {ex.Message}");
    }
    finally
    {
        isBuilding = false;
    }
}

async Task StartChildAsync(string slot, string slotDir, bool isReplay)
{
    await relayLock.WaitAsync();
    try
    {
        currentSlot = slot;
        var psi = CreateChildPsi(slotDir, repoRoot);
        currentChild = Process.Start(psi) ?? throw new InvalidOperationException("Failed to spawn backend process.");
        activeChildIn = new StreamWriter(currentChild.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };

        Log($"[DevRelay] Spawned backend on slot {slot.ToUpper()} (PID {currentChild.Id})");
        AttachChildPipes(currentChild, isReplay);

        if (isReplay && !string.IsNullOrEmpty(cachedInitialize))
        {
            await activeChildIn.WriteLineAsync(cachedInitialize);
            await activeChildIn.FlushAsync();
        }
    }
    finally
    {
        relayLock.Release();
    }
}

async Task RestartChildAsync(string nextSlot, string nextDir)
{
    await relayLock.WaitAsync();
    isRestarting = true;
    try
    {
        if (currentChild != null && !currentChild.HasExited)
        {
            try
            {
                currentChild.Kill(entireProcessTree: true);
                await currentChild.WaitForExitAsync();
            }
            catch { }
        }

        currentSlot = nextSlot;
        var psi = CreateChildPsi(nextDir, repoRoot);
        currentChild = Process.Start(psi) ?? throw new InvalidOperationException("Failed to spawn backend process.");
        activeChildIn = new StreamWriter(currentChild.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true };

        Log($"[DevRelay] Restarted backend on slot {nextSlot.ToUpper()} (PID {currentChild.Id})");

        if (!string.IsNullOrEmpty(cachedInitialize))
        {
            AttachChildPipes(currentChild, isReplay: true);
            await activeChildIn.WriteLineAsync(cachedInitialize);
            await activeChildIn.FlushAsync();
        }
        else
        {
            AttachChildPipes(currentChild, isReplay: false);
        }
    }
    finally
    {
        isRestarting = false;
        relayLock.Release();
    }
}

void AttachChildPipes(Process child, bool isReplay)
{
    // Stderr forwarding
    _ = Task.Run(async () =>
    {
        try
        {
            var errReader = child.StandardError;
            while (await errReader.ReadLineAsync() is { } errLine)
            {
                await Console.Error.WriteLineAsync(errLine);
            }
        }
        catch { }
    });

    // Stdout forwarding
    _ = Task.Run(async () =>
    {
        try
        {
            var outReader = child.StandardOutput;
            var needsReplayHandling = isReplay;

            while (await outReader.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (needsReplayHandling)
                {
                    needsReplayHandling = false;
                    Log("[DevRelay] Received initialize response from new child. Swallowing and sending initialized notification.");

                    await relayLock.WaitAsync();
                    try
                    {
                        if (activeChildIn != null)
                        {
                            await activeChildIn.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
                            await activeChildIn.FlushAsync();

                            while (pendingQueue.TryDequeue(out var pending))
                            {
                                await activeChildIn.WriteLineAsync(pending);
                            }
                            await activeChildIn.FlushAsync();
                        }
                    }
                    finally
                    {
                        relayLock.Release();
                    }

                    // Tell OpenCode tools may have changed
                    outbox.Writer.TryWrite("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}");
                    Log("[DevRelay] Emitted notifications/tools/list_changed to OpenCode.");
                    continue;
                }

                outbox.Writer.TryWrite(line);
            }
        }
        catch { }
    });
}

ProcessStartInfo CreateChildPsi(string slotDir, string root)
{
    var exePath = Path.Combine(slotDir, "eling-backend.exe");
    var dllPath = Path.Combine(slotDir, "eling-backend.dll");

    var psi = new ProcessStartInfo();
    if (File.Exists(exePath))
    {
        psi.FileName = exePath;
    }
    else
    {
        psi.FileName = "dotnet";
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(dllPath);
    }

    psi.WorkingDirectory = root;
    psi.RedirectStandardInput = true;
    psi.RedirectStandardOutput = true;
    psi.RedirectStandardError = true;
    psi.UseShellExecute = false;

    var port = Environment.GetEnvironmentVariable("ELING_DASHBOARD_PORT") ?? "4417";
    psi.EnvironmentVariables["ELING_DASHBOARD_PORT"] = port;
    psi.EnvironmentVariables["ELING_OUTPUT_ROOT"] = Path.GetFileName(slotDir);
    psi.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";
    psi.EnvironmentVariables["ELING_WATCH_DASHBOARD"] = Environment.GetEnvironmentVariable("ELING_WATCH_DASHBOARD") ?? "1";

    return psi;
}

void EnsureInitialSlot(string sourceBin, string targetSlot, string csproj, string root)
{
    if (Directory.Exists(targetSlot) && (File.Exists(Path.Combine(targetSlot, "eling-backend.exe")) || File.Exists(Path.Combine(targetSlot, "eling-backend.dll"))))
    {
        return;
    }

    if (Directory.Exists(sourceBin) && (File.Exists(Path.Combine(sourceBin, "eling-backend.exe")) || File.Exists(Path.Combine(sourceBin, "eling-backend.dll"))))
    {
        Log("[DevRelay] Initializing slot A from existing .bin-opencode...");
        CopyDirectory(sourceBin, targetSlot);
        return;
    }

    Log("[DevRelay] Performing initial build into slot A...");
    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        WorkingDirectory = root,
        UseShellExecute = false
    };
    psi.ArgumentList.Add("build");
    psi.ArgumentList.Add(csproj);
    psi.ArgumentList.Add("-c");
    psi.ArgumentList.Add("Debug");
    psi.ArgumentList.Add("-o");
    psi.ArgumentList.Add(targetSlot);
    using var p = Process.Start(psi)!;
    p.WaitForExit();
}

void CopyDirectory(string sourceDir, string targetDir)
{
    Directory.CreateDirectory(targetDir);
    foreach (var file in Directory.GetFiles(sourceDir))
    {
        try { File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true); } catch { }
    }
    foreach (var dir in Directory.GetDirectories(sourceDir))
    {
        try { CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir))); } catch { }
    }
}

string ResolveRepoRoot()
{
    var dir = Directory.GetCurrentDirectory();
    while (!string.IsNullOrEmpty(dir))
    {
        if (File.Exists(Path.Combine(dir, "Eling.slnx")) || Directory.Exists(Path.Combine(dir, "src", "backend")))
        {
            return dir;
        }
        var parent = Directory.GetParent(dir);
        if (parent == null) break;
        dir = parent.FullName;
    }
    return Directory.GetCurrentDirectory();
}
