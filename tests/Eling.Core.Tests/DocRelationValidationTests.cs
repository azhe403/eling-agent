namespace Eling.Core.Tests;

using Eling.Core.Docs;
using Xunit;

public class DocRelationValidationTests
{
    [Fact]
    public void ValidateRelation_Implements_PlanToSpec_Succeeds()
    {
        var method = typeof(DocsService).GetMethod("ValidateRelation",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        method!.Invoke(null, new object[] { DocKind.Plan, DocKind.Spec, RelationKind.Implements });
    }

    [Fact]
    public void ValidateRelation_Implements_SpecToPlan_Throws()
    {
        var method = typeof(DocsService).GetMethod("ValidateRelation",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            method!.Invoke(null, new object[] { DocKind.Spec, DocKind.Plan, RelationKind.Implements }));
        
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("Implements relation requires source to be Plan", ex.InnerException!.Message);
    }

    [Fact]
    public void ValidateRelation_Implements_DocToSpec_Throws()
    {
        var method = typeof(DocsService).GetMethod("ValidateRelation",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            method!.Invoke(null, new object[] { DocKind.Doc, DocKind.Spec, RelationKind.Implements }));
        
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("Implements relation requires source to be Plan", ex.InnerException!.Message);
    }

    [Fact]
    public void ValidateRelation_Supersedes_SameKind_Succeeds()
    {
        var method = typeof(DocsService).GetMethod("ValidateRelation",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        method!.Invoke(null, new object[] { DocKind.Spec, DocKind.Spec, RelationKind.Supersedes });
        method!.Invoke(null, new object[] { DocKind.Plan, DocKind.Plan, RelationKind.Supersedes });
        method!.Invoke(null, new object[] { DocKind.Doc, DocKind.Doc, RelationKind.Supersedes });
        method!.Invoke(null, new object[] { DocKind.Scratch, DocKind.Scratch, RelationKind.Supersedes });
    }

    [Fact]
    public void ValidateRelation_Supersedes_DifferentKind_Throws()
    {
        var method = typeof(DocsService).GetMethod("ValidateRelation",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        var ex = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            method!.Invoke(null, new object[] { DocKind.Spec, DocKind.Plan, RelationKind.Supersedes }));
        
        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Contains("Supersedes relation requires both documents to be same kind", ex.InnerException!.Message);
    }

    [Fact]
    public void ValidateRelation_References_AnyCombination_Succeeds()
    {
        var method = typeof(DocsService).GetMethod("ValidateRelation",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        
        method!.Invoke(null, new object[] { DocKind.Spec, DocKind.Plan, RelationKind.References });
        method!.Invoke(null, new object[] { DocKind.Plan, DocKind.Spec, RelationKind.References });
        method!.Invoke(null, new object[] { DocKind.Doc, DocKind.Scratch, RelationKind.References });
        method!.Invoke(null, new object[] { DocKind.Scratch, DocKind.Doc, RelationKind.References });
    }
}