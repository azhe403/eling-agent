namespace Eling.Core.Tests;

using Eling.Core.Docs;
using Xunit;

public class ExternalDocInferenceTests
{
    [Fact]
    public void Infer_SpecsFolder_ReturnsSpecKind()
    {
        var result = ExternalDocInference.Infer("docs/specs/my-spec.md", "# Title");
        
        Assert.Equal(DocKind.Spec, result.Kind);
        Assert.Equal("specs", result.Topic);
    }

    [Fact]
    public void Infer_PlansFolder_ReturnsPlanKind()
    {
        var result = ExternalDocInference.Infer("docs/plans/my-plan.md", "# Title");
        
        Assert.Equal(DocKind.Plan, result.Kind);
        Assert.Equal("plans", result.Topic);
    }

    [Fact]
    public void Infer_ExplicitKind_OverridesFolder()
    {
        var result = ExternalDocInference.Infer("docs/random/file.md", "# Title", "plan");
        
        Assert.Equal(DocKind.Plan, result.Kind);
    }

    [Fact]
    public void Infer_FilenameOrder_ExtractsNumericPrefix()
    {
        var result = ExternalDocInference.Infer("docs/specs/03-third-spec.md", "# Title");
        
        Assert.Equal(3, result.Order);
    }

    [Fact]
    public void Infer_NoNumericPrefix_ReturnsZeroOrder()
    {
        var result = ExternalDocInference.Infer("docs/specs/alpha-spec.md", "# Title");
        
        Assert.Equal(0, result.Order);
    }
}