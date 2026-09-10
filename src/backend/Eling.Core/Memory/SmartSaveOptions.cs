namespace Eling.Core;

public sealed class SmartSaveOptions
{
    public double DuplicateThreshold { get; set; } = 0.7;

    public bool EnableFuzzyMatch { get; set; } = true;
}
