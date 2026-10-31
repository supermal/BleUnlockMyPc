using System.Text.RegularExpressions;
using AutoUnlockManager.Core.Resources;

namespace AutoUnlockManager.Core.Rules;

public sealed record RuleParam(string Name, string Value, string Effect, bool Changed, string DefaultText);

/// <summary>Elemento de lista; <see cref="Key"/> es la etiqueta en negrita (null si no tiene).</summary>
public sealed record RuleItem(string? Key, string Text);

/// <summary>Fila de la tabla de la sección 4. Tone: ok | warn | vacío.</summary>
public sealed record RuleRow(int Number, string Key, string Text, string Result, string Tone);

public sealed record RuleStepGroup(string Title, IReadOnlyList<string> Steps);

public sealed record RuleSection(
    int Number,
    string Title,
    string? Lead,
    IReadOnlyList<RuleParam> Params,
    IReadOnlyList<RuleItem> Items,
    IReadOnlyList<RuleRow> Rows,
    IReadOnlyList<RuleStepGroup> StepGroups);

/// <summary>
/// Texto de la página de reglas en el idioma actual. Las plantillas de UiStrings llevan marcadores
/// con nombre ({lock}, {prompt}…) que se sustituyen por los valores vigentes; las claves siguen la
/// estructura Rules_S{sección}_I{n}_K/_V, _R{n}_K/_V/_Res y _B{n}_Title/_{n}.
/// </summary>
public static partial class RulesContent
{
    private static readonly string[] ParamKeys =
        ["lock", "unlock", "prompt", "confirm", "ucount", "interval", "grace", "timeout", "reject", "snooze"];

    // Forma de cada sección: lleva entradilla, cuántos elementos de lista, filas de tabla y pasos por grupo
    private static readonly (bool Lead, int Items, int Rows, int[] Groups)[] Shape =
    [
        (true, 0, 0, []),
        (false, 4, 0, []),
        (false, 7, 0, []),
        (true, 4, 0, []),
        (true, 3, 7, []),
        (true, 2, 0, []),
        (false, 3, 0, []),
        (false, 5, 0, []),
        (false, 0, 0, [6, 2]),
    ];

    [GeneratedRegex(@"\{([A-Za-z]\w*)\}")]
    private static partial Regex Marker();

    public static IReadOnlyList<RuleSection> Build(RuleValues values)
    {
        var map = Placeholders(values);
        return Shape.Select((shape, n) => new RuleSection(
            n,
            Text($"Rules_S{n}_Title", map),
            shape.Lead ? Text($"Rules_S{n}_Lead", map) : null,
            n == 0 ? Params(values, map) : [],
            Enumerable.Range(1, shape.Items).Select(i => new RuleItem(
                TryText($"Rules_S{n}_I{i}_K", map), Text($"Rules_S{n}_I{i}_V", map))).ToList(),
            Enumerable.Range(1, shape.Rows).Select(r => new RuleRow(
                r,
                Text($"Rules_S{n}_R{r}_K", map),
                Text($"Rules_S{n}_R{r}_V", map),
                Text($"Rules_S{n}_R{r}_Res", map),
                r switch { 1 => "ok", 6 => "warn", _ => "" })).ToList(),
            shape.Groups.Select((count, g) => new RuleStepGroup(
                Text($"Rules_S{n}_B{g + 1}_Title", map),
                Enumerable.Range(1, count).Select(s => Text($"Rules_S{n}_B{g + 1}_{s}", map)).ToList())).ToList()))
            .ToList();
    }

    /// <summary>Sustituye los marcadores de una plantilla de UiStrings por los valores vigentes.</summary>
    public static string Fill(string key, RuleValues values) => Text(key, Placeholders(values));

    private static IReadOnlyList<RuleParam> Params(RuleValues values, IReadOnlyDictionary<string, string> map)
    {
        var defaults = Placeholders(RuleValues.Defaults);
        return ParamKeys.Select(k =>
        {
            var changed = map[k] != defaults[k];
            var defaultValue = Text($"Rules_P_{k}_Value", defaults);
            return new RuleParam(
                Text($"Rules_P_{k}_Name", map),
                Text($"Rules_P_{k}_Value", map),
                Text($"Rules_P_{k}_Effect", map),
                changed,
                Text("Rules_DefaultValue", new Dictionary<string, string> { ["value"] = defaultValue }));
        }).ToList();
    }

    private static IReadOnlyDictionary<string, string> Placeholders(RuleValues v)
    {
        var ex = RuleExamples.From(v);
        return new Dictionary<string, string>
        {
            ["lock"] = RuleValues.Number(v.LockDbm),
            ["unlock"] = RuleValues.Number(v.UnlockDbm),
            ["prompt"] = RuleValues.Number(v.PromptCount),
            ["confirm"] = RuleValues.Number(v.ConfirmCount),
            ["ucount"] = RuleValues.Number(v.UnlockCount),
            ["interval"] = RuleValues.Number(v.SampleIntervalMs),
            ["grace"] = RuleValues.Number(v.GraceSeconds),
            ["timeout"] = RuleValues.Number(v.UnlockTimeoutSeconds),
            ["reject"] = RuleValues.Number(v.RejectSnoozeSeconds),
            ["snooze"] = RuleValues.Number(v.SnoozeMinutes),
            ["trig"] = RuleValues.Number(ex.Trigger),
            ["back"] = RuleValues.Number(ex.Back),
            ["w1"] = RuleValues.Number(ex.Withdraw[0]),
            ["w2"] = RuleValues.Number(ex.Withdraw[1]),
            ["w3"] = RuleValues.Number(ex.Withdraw[2]),
            ["w4"] = RuleValues.Number(ex.Withdraw[3]),
        };
    }

    private static string Text(string key, IReadOnlyDictionary<string, string> map) =>
        TryText(key, map) ?? $"!{key}!";

    private static string? TryText(string key, IReadOnlyDictionary<string, string> map)
    {
        var template = UiStrings.ResourceManager.GetString(key, UiStrings.Culture);
        return template is null
            ? null
            : Marker().Replace(template, m => map.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);
    }
}
