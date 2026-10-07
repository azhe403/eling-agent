using Eling.Core.Codebase;

namespace Eling.Core.Tests;

/// <summary>
/// The scanner decides where a chunk may end without splitting a declaration.
/// It must ignore braces that only appear inside strings and comments, and it
/// must not mistake a nested control-flow block for a member.
/// </summary>
public class DeclarationScannerTests
{
    [Fact]
    public void FindMemberBoundaries_TwoMethodsInAClass_ReportsBothMethodStarts()
    {
        var lines = new[]
        {
            "public sealed class Sample",
            "{",
            "    public void First()",
            "    {",
            "        DoWork();",
            "    }",
            "",
            "    public void Second()",
            "    {",
            "        DoOther();",
            "    }",
            "}",
        };

        Assert.Equal([2, 7], DeclarationScanner.FindMemberBoundaries(lines));
    }

    [Fact]
    public void FindMemberBoundaries_MethodsHoldingNestedBlocks_ReportsOnlyTheOuterMembers()
    {
        var lines = new[]
        {
            "public sealed class Sample",
            "{",
            "    public void First()",
            "    {",
            "        if (ready)",
            "        {",
            "            Go();",
            "        }",
            "    }",
            "",
            "    public void Second()",
            "    {",
            "        try",
            "        {",
            "            Go();",
            "        }",
            "        catch",
            "        {",
            "            Recover();",
            "        }",
            "    }",
            "}",
        };

        // The `if`, `try` and `catch` blocks are nested inside the methods. A cut
        // after any of them lands mid-method, which is the failure being tested.
        Assert.Equal([2, 10], DeclarationScanner.FindMemberBoundaries(lines));
    }

    [Fact]
    public void FindMemberBoundaries_MethodCarriesDocCommentAndAttribute_ReportsTheDocComment()
    {
        var lines = new[]
        {
            "class Sample",
            "{",
            "    void A()",
            "    {",
            "        Go();",
            "    }",
            "",
            "    /// <summary>Does the thing.</summary>",
            "    [Obsolete]",
            "    void B()",
            "    {",
            "        Go();",
            "    }",
            "}",
        };

        // Index 7, not 9: the docs and the attribute describe the method below
        // them and have to travel with it.
        Assert.Equal([2, 7], DeclarationScanner.FindMemberBoundaries(lines));
    }

    [Fact]
    public void FindMemberBoundaries_StatementAfterASemicolon_IsNotABoundary()
    {
        var lines = new[]
        {
            "class Sample",
            "{",
            "    void A(int n)",
            "    {",
            "        var first = n;",
            "        var second = n;",
            "        var third = n;",
            "    }",
            "}",
        };

        var boundaries = DeclarationScanner.FindMemberBoundaries(lines);

        Assert.Equal([2], boundaries);
        Assert.DoesNotContain(5, boundaries);
    }

    [Fact]
    public void FindMemberBoundaries_BracesInsideStringsAndComments_AreNotCounted()
    {
        var lines = new[]
        {
            "class Sample",
            "{",
            "    void Run()",
            "    {",
            "        var text = \"}\";",
            "        var verbatim = @\"{ }\";",
            "        // } not a brace",
            "        /* } not a brace either */",
            "    }",
            "    void Next()",
            "    {",
            "        Go();",
            "    }",
            "}",
        };

        Assert.Equal([2, 9], DeclarationScanner.FindMemberBoundaries(lines));
    }

    [Fact]
    public void FindMemberBoundaries_TemplateLiteralBraces_AreNotCounted()
    {
        var lines = new[]
        {
            "class Sample",
            "{",
            "    void Run()",
            "    {",
            "        var t = `a } b`;",
            "    }",
            "}",
        };

        Assert.Equal([2], DeclarationScanner.FindMemberBoundaries(lines));
    }

    [Fact]
    public void FindMemberBoundaries_UnclosedBrace_ReturnsEmpty()
    {
        var lines = new[]
        {
            "public sealed class Sample",
            "{",
            "    public void Run()",
            "    {",
            "        DoWork();",
        };

        Assert.Empty(DeclarationScanner.FindMemberBoundaries(lines));
    }

    [Fact]
    public void FindMemberBoundaries_UnterminatedBlockComment_ReturnsEmpty()
    {
        var lines = new[]
        {
            "class Sample",
            "{",
            "    void Run()",
            "    {",
            "        Go();",
            "    }",
            "    /* never closed",
        };

        Assert.Empty(DeclarationScanner.FindMemberBoundaries(lines));
    }

    [Fact]
    public void FindMemberBoundaries_CrlfLines_ReportTheSameBoundariesAsLf()
    {
        var lines = new[]
        {
            "class Sample",
            "{",
            "    void A()",
            "    {",
            "        Go();",
            "    }",
            "",
            "    void B()",
            "    {",
            "        Go();",
            "    }",
            "}",
        };

        var lf = DeclarationScanner.FindMemberBoundaries(lines);
        var crlf = DeclarationScanner.FindMemberBoundaries(lines.Select(l => l + "\r").ToArray());

        Assert.Equal(lf, crlf);
    }

    [Fact]
    public void FindMemberBoundaries_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(DeclarationScanner.FindMemberBoundaries([]));
    }
}