namespace ClaudeDashboard.Tests.Architecture;

/// <summary>
/// The scanner every source guard reads through (PR #66 review). A guard that says "this file
/// writes nothing" is only as good as the scanner's idea of where code is.
/// </summary>
public sealed class GuardScanTests
{
    /// <summary>
    /// <strong>Code after a raw string literal is still code.</strong> Before this, the raw JSON in
    /// <c>HookPlugin.cs</c> made the scanner swallow the write that followed it.
    /// </summary>
    [Fact]
    public void Code_after_a_raw_string_is_kept()
    {
        const string Source = "var json = \"\"\"\n  { \"a\": \"b\" }\n  \"\"\";\nFile.WriteAllText(path, json);\n";

        var code = GuardScan.CodeOnly(Source);

        Assert.Contains("File.WriteAllText(path, json);", code, StringComparison.Ordinal);
        Assert.DoesNotContain("\"a\"", code, StringComparison.Ordinal);
    }

    /// <summary>A raw string opened by four quotes may hold three, and ends only at four.</summary>
    [Fact]
    public void A_raw_string_ends_at_its_own_quote_count()
    {
        const string Source = "var s = \"\"\"\" File.Delete(x) \"\"\" still text \"\"\"\";\nDirectory.Delete(y);";

        var code = GuardScan.CodeOnly(Source);

        Assert.DoesNotContain("File.Delete", code, StringComparison.Ordinal);
        Assert.DoesNotContain("still text", code, StringComparison.Ordinal);
        Assert.Contains("Directory.Delete(y);", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// <strong>A verbatim string's doubled quote does not end it, and its backslash escapes
    /// nothing.</strong> Read as an ordinary string, <c>@"C:\dir\"</c> would never close.
    /// </summary>
    [Fact]
    public void A_verbatim_string_is_one_literal()
    {
        const string Source = "var p = @\"C:\\dir\\\"; var q = @\"say \"\"File.Move\"\" here\"; File.Copy(a, b);";

        var code = GuardScan.CodeOnly(Source);

        Assert.DoesNotContain("File.Move", code, StringComparison.Ordinal);
        Assert.Contains("File.Copy(a, b);", code, StringComparison.Ordinal);
    }

    /// <summary>An interpolated verbatim string, either way round, is one literal too.</summary>
    [Fact]
    public void An_interpolated_verbatim_string_is_one_literal()
    {
        const string Source = "var a = $@\"x\\\"; var b = @$\"y\\\"; File.Create(c);";

        Assert.Contains("File.Create(c);", GuardScan.CodeOnly(Source), StringComparison.Ordinal);
    }
}
