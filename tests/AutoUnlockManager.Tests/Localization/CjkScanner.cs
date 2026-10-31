namespace AutoUnlockManager.Tests.Localization;

/// <summary>
/// Busca caracteres chinos fuera de comentarios: en el código del producto solo pueden quedar en
/// literales, y todo literal visible debe venir de los recursos. Los comentarios en chino son legítimos.
/// </summary>
internal static class CjkScanner
{
    private static readonly string[] ProductProjects =
        ["AutoUnlockManager", "AutoUnlockManager.Core", "AutoUnlockService", "AutoUnlockHelper"];

    public static List<string> ScanProduct(string repoRoot)
    {
        var hits = new List<string>();
        foreach (var project in ProductProjects)
        {
            var dir = Path.Combine(repoRoot, "src", project);
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(dir, file);
                var first = relative.Split(Path.DirectorySeparatorChar)[0];
                if (first is "obj" or "bin" or "Resources")
                {
                    continue;
                }

                var lines = Path.GetExtension(file) switch
                {
                    ".cs" => ScanCSharp(File.ReadAllText(file)),
                    ".xaml" => ScanXaml(File.ReadAllText(file)),
                    _ => [],
                };
                hits.AddRange(lines.Select(line => $"{project}{Path.DirectorySeparatorChar}{relative}:{line}"));
            }
        }

        return hits;
    }

    public static List<int> ScanCSharp(string code)
    {
        var hits = new SortedSet<int>();
        var line = 1;
        var i = 0;

        void Report() => hits.Add(line);

        // Avanza un carácter llevando la cuenta de líneas; informa si es chino y se pide
        char Next(bool report)
        {
            var c = code[i++];
            if (c == '\n')
            {
                line++;
            }
            else if (report && IsCjk(c))
            {
                Report();
            }

            return c;
        }

        bool At(string s) => string.CompareOrdinal(code, i, s, 0, s.Length) == 0;

        while (i < code.Length)
        {
            if (At("//"))
            {
                while (i < code.Length && code[i] != '\n') Next(report: false);
            }
            else if (At("/*"))
            {
                i += 2;
                while (i < code.Length && !At("*/")) Next(report: false);
                i = Math.Min(i + 2, code.Length);
            }
            else if (At("\"\"\""))
            {
                // Literal sin formato: se cierra con la misma cantidad de comillas
                var quotes = 0;
                while (i < code.Length && code[i] == '"') { quotes++; i++; }
                var close = new string('"', quotes);
                while (i < code.Length && !At(close)) Next(report: true);
                i = Math.Min(i + quotes, code.Length);
            }
            else if (At("@\"") || At("$@\"") || At("@$\""))
            {
                while (code[i] != '"') i++;
                i++;
                while (i < code.Length)
                {
                    if (At("\"\"")) { i += 2; continue; }
                    if (code[i] == '"') { i++; break; }
                    Next(report: true);
                }
            }
            else if (code[i] == '"')
            {
                i++;
                while (i < code.Length && code[i] != '"' && code[i] != '\n')
                {
                    if (code[i] == '\\') i++;
                    if (i < code.Length) Next(report: true);
                }

                if (i < code.Length && code[i] == '"') i++;
            }
            else if (code[i] == '\'')
            {
                i++;
                while (i < code.Length && code[i] != '\'' && code[i] != '\n')
                {
                    if (code[i] == '\\') i++;
                    if (i < code.Length) Next(report: true);
                }

                if (i < code.Length && code[i] == '\'') i++;
            }
            else
            {
                Next(report: true);
            }
        }

        return [.. hits];
    }

    public static List<int> ScanXaml(string xaml)
    {
        var hits = new SortedSet<int>();
        var line = 1;
        var inComment = false;
        for (var i = 0; i < xaml.Length; i++)
        {
            if (!inComment && string.CompareOrdinal(xaml, i, "<!--", 0, 4) == 0)
            {
                inComment = true;
            }
            else if (inComment && string.CompareOrdinal(xaml, i, "-->", 0, 3) == 0)
            {
                inComment = false;
                i += 2;
                continue;
            }

            if (xaml[i] == '\n')
            {
                line++;
            }
            else if (!inComment && IsCjk(xaml[i]))
            {
                hits.Add(line);
            }
        }

        return [.. hits];
    }

    // Ideogramas CJK y la puntuación de ancho completo que acompaña a los textos chinos
    private static bool IsCjk(char c) => c is >= '一' and <= '鿿' or >= '　' and <= '〿' or >= '＀' and <= '￯';
}
