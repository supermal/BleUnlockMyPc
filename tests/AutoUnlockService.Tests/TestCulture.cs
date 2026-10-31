using System.Runtime.CompilerServices;
using AutoUnlockService.Resources;
using Xunit;

namespace AutoUnlockService.Tests;

// Las clases de recursos tienen un idioma de proceso (Culture estático): sin fijarlo, el
// resultado de los tests dependería del idioma de Windows del equipo que los ejecuta.
// Se fija chino, el idioma en que están escritas las aserciones existentes
internal static class TestCulture
{
    public const string LanguageCollection = "Language";

    [ModuleInitializer]
    internal static void Init() => Reset();

    public static void Reset() => ServiceStrings.Culture = new System.Globalization.CultureInfo("zh");
}

// Los tests que cambian de idioma no pueden correr en paralelo con el resto
[CollectionDefinition(TestCulture.LanguageCollection, DisableParallelization = true)]
public sealed class LanguageCollection;
