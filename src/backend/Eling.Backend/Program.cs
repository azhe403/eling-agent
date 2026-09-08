using Eling.Backend.Bootstrap;
using Microsoft.Extensions.Hosting;

var host = McpHostBuilder.Build();
await host.RunAsync();
return 0;