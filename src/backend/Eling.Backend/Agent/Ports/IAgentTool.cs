namespace Eling.Backend.Agent.Ports;

public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    string ParametersJsonSchema { get; }
    Task<string> ExecuteAsync(string argumentsJson, CancellationToken ct);
}
