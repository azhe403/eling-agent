using Eling.Application;
using Eling.Index;
using Eling.Mcp;
using Eling.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddElingLogging();

builder.Services.AddSingleton<IMemoryStorage>(new FileSystemMemoryStorage(".eling"));
builder.Services.AddSingleton<IMemoryIndex>(new SqliteMemoryIndex(Path.Combine(".eling", "index.db")));
builder.Services.AddScoped<IMemoryService, MemoryService>();

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
