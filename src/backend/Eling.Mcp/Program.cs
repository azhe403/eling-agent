using Eling.Application;
using Eling.Index;
using Eling.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

// Ensure all console logging goes to stderr so stdout remains dedicated to JSON-RPC
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var elingDir = Path.Combine(Directory.GetCurrentDirectory(), ".eling");
Directory.CreateDirectory(elingDir);

// Register storage and index (uses default file system paths — .eling directory in working dir)
builder.Services.AddSingleton<IMemoryStorage>(new FileSystemMemoryStorage(elingDir));
builder.Services.AddSingleton<IMemoryIndex>(new SqliteMemoryIndex(Path.Combine(elingDir, "index.db")));
builder.Services.AddScoped<IMemoryService, MemoryService>();

// MCP server with stdio transport
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var host = builder.Build();
await host.RunAsync();
