/// <summary>
/// Test entry point that lets WebApplicationFactory&lt;Program&gt; boot the
/// ASP.NET dashboard in Eling.Dashboard.Tests.
///
/// Must be in the global namespace so it merges with the
/// top-level-statement-generated Program class in Eling.Dashboard.
///
/// Note: MCP stdio is not disabled here — the dashboard never runs MCP.
/// Only Eling.Host starts the MCP server (AddElingMcpServerStdio), and its
/// process tests keep stdout clean via the --no-dashboard flag and isolated
/// test ports instead.
/// </summary>
public partial class Program { }
