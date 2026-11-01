using Xunit;

namespace BleUnlockManager.Tests.Localization;

public sealed class NoHardcodedChineseTests
{
    [Fact]
    public void Comments_are_ignored()
    {
        var code = """
            // 注释
            /// <summary>说明</summary>
            var a = 1; /* 块注释
               第二行 */ var b = 2;
            """;

        Assert.Empty(CjkScanner.ScanCSharp(code));
    }

    [Fact]
    public void String_literals_are_reported_with_line()
    {
        var code = "var a = 1;\nvar s = \"中文\";";

        Assert.Equal([2], CjkScanner.ScanCSharp(code));
    }

    [Fact]
    public void Url_inside_string_does_not_start_a_comment()
    {
        var code = "var s = \"http://x\" + \"中\";";

        Assert.Equal([1], CjkScanner.ScanCSharp(code));
    }

    [Fact]
    public void Raw_and_verbatim_strings_are_reported()
    {
        var code = "var r = \"\"\"\n  标题\n  \"\"\";\nvar v = @\"a\"\"b 文\";";

        Assert.Equal([2, 4], CjkScanner.ScanCSharp(code));
    }

    [Fact]
    public void Comment_marker_inside_string_then_real_comment()
    {
        var code = "var s = \"/* no */\"; // 注释";

        Assert.Empty(CjkScanner.ScanCSharp(code));
    }

    [Fact]
    public void Xaml_comments_are_ignored_and_values_reported()
    {
        var xaml = "<!--  注释  -->\n<TextBlock Text=\"标题\" />\n<!--\n多行\n-->";

        Assert.Equal([2], CjkScanner.ScanXaml(xaml));
    }

    [Fact]
    public void Product_sources_have_no_hardcoded_chinese()
    {
        var hits = CjkScanner.ScanProduct(RepoPaths.Root);

        Assert.True(hits.Count == 0, $"{hits.Count} lines:{Environment.NewLine}{string.Join(Environment.NewLine, hits.Take(200))}");
    }
}
