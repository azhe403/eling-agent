using Eling.Backend.Bootstrap;
using Microsoft.Extensions.Hosting;
using Serilog.Debugging;

SelfLog.Enable(Console.Error);

var host = McpHostBuilder.Build();
await host.RunAsync();
return 0;