using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AutoUnlockManager.Tests.Localization;

/// <summary>
/// Comprueba que cada recurso neutro (inglés) tiene sus satélites zh y es con las mismas claves,
/// sin valores vacíos y con los mismos marcadores: un marcador perdido en una traducción es un
/// FormatException en ejecución o una propiedad de registro que desaparece.
/// </summary>
internal static partial class ResxParity
{
    private static readonly string[] Satellites = ["zh", "es"];

    // {Count}, {0}, {0:0.0}, {State,-10}: cuenta solo el nombre o el índice
    [GeneratedRegex(@"\{([A-Za-z_]\w*|\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    // Solo las carpetas Resources de los proyectos del producto (src/*/Resources): las herramientas
    // de depuración como TestCredentialProviderApp no se traducen
    public static IReadOnlyList<string> FindNeutralResources(string repoRoot) =>
        Directory.EnumerateDirectories(Path.Combine(repoRoot, "src"))
            .Select(project => Path.Combine(project, "Resources"))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.resx"))
            .Where(p => Path.GetFileNameWithoutExtension(p).IndexOf('.') < 0)
            .ToList();

    public static IReadOnlyList<string> Check(string neutralPath)
    {
        var problems = new List<string>();
        var neutral = Load(neutralPath);
        var name = Path.GetFileName(neutralPath);
        problems.AddRange(neutral.Where(e => e.Value.Length == 0).Select(e => $"{name}: empty value for {e.Key}"));

        foreach (var culture in Satellites)
        {
            var path = Path.ChangeExtension(neutralPath, $"{culture}.resx");
            var file = Path.GetFileName(path);
            if (!File.Exists(path))
            {
                problems.Add($"{file}: satellite file not found");
                continue;
            }

            var satellite = Load(path);
            problems.AddRange(neutral.Keys.Except(satellite.Keys).Select(k => $"{file}: missing key {k}"));
            problems.AddRange(satellite.Keys.Except(neutral.Keys).Select(k => $"{file}: extra key {k}"));

            foreach (var (key, value) in satellite)
            {
                if (value.Length == 0)
                {
                    problems.Add($"{file}: empty value for {key}");
                }
                else if (neutral.TryGetValue(key, out var original) && !Placeholders(original).SetEquals(Placeholders(value)))
                {
                    problems.Add($"{file}: placeholders of {key} differ from {name}");
                }
            }
        }

        return problems;
    }

    private static Dictionary<string, string> Load(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    private static HashSet<string> Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
}

/// <summary>Raíz del repositorio: la carpeta que contiene AutoUnlockMyPc.sln.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = Find();

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AutoUnlockMyPc.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("AutoUnlockMyPc.sln not found above the test output folder.");
    }
}
