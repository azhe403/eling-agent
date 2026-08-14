using Eling.Mcp;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddElingLogging();
builder.Services.AddElingCoreServices();
builder.Services.AddElingMcpServer();

await builder.Build().RunAsync();
