namespace Eling.Core.Memory;

public sealed class SmartSaveOptions
{
    public double DuplicateThreshold { get; set; } = 0.60;

    public double CrossScopeDuplicateThreshold { get; set; } = 0.75;

    public double NearMatchThreshold { get; set; } = 0.35;

    public bool EnableFuzzyMatch { get; set; } = true;
}
