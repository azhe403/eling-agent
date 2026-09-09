namespace Eling.Desktop.Models;

public record RuntimeDto(string ProjectRoot, string DataDirectory)
{
    public string Name => System.IO.Path.GetFileName(ProjectRoot.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : ProjectRoot;
}
