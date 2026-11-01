using Xunit;

namespace BleUnlockManager.Tests.Localization;

public sealed class ResourceParityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("resx-parity-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, params (string Key, string Value)[] entries)
    {
        var path = Path.Combine(_dir, name);
        var data = string.Concat(entries.Select(e =>
            $"<data name=\"{e.Key}\" xml:space=\"preserve\"><value>{e.Value}</value></data>"));
        File.WriteAllText(path, $"<?xml version=\"1.0\" encoding=\"utf-8\"?><root>{data}</root>");
        return path;
    }

    [Fact]
    public void Check_reports_missing_satellite()
    {
        var neutral = Write("S.resx", ("A", "a"));
        Write("S.zh.resx", ("A", "甲"));

        Assert.Contains(ResxParity.Check(neutral), p => p.Contains("S.es.resx"));
    }

    [Fact]
    public void Check_reports_missing_and_extra_keys()
    {
        var neutral = Write("S.resx", ("A", "a"), ("B", "b"));
        Write("S.zh.resx", ("A", "甲"), ("B", "乙"));
        Write("S.es.resx", ("A", "a"), ("C", "c"));

        var problems = ResxParity.Check(neutral);

        Assert.Contains(problems, p => p.Contains("missing") && p.Contains("B"));
        Assert.Contains(problems, p => p.Contains("extra") && p.Contains("C"));
    }

    [Fact]
    public void Check_reports_placeholder_mismatch_ignoring_format()
    {
        var neutral = Write("S.resx", ("A", "{Count} items, {0:0.0} dBm"));
        Write("S.zh.resx", ("A", "{0:0} dBm，{Count} 条"));
        Write("S.es.resx", ("A", "{Total} elementos, {0} dBm"));

        var problems = ResxParity.Check(neutral);

        Assert.Single(problems);
        Assert.Contains("S.es.resx", problems[0]);
        Assert.Contains("A", problems[0]);
    }

    [Fact]
    public void Check_reports_empty_value()
    {
        var neutral = Write("S.resx", ("A", "a"));
        Write("S.zh.resx", ("A", ""));
        Write("S.es.resx", ("A", "a"));

        Assert.Contains(ResxParity.Check(neutral), p => p.Contains("empty"));
    }

    [Fact]
    public void All_repository_resources_are_complete_and_consistent()
    {
        var neutrals = ResxParity.FindNeutralResources(RepoPaths.Root);
        Assert.NotEmpty(neutrals);

        var problems = neutrals.SelectMany(ResxParity.Check).ToList();

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
