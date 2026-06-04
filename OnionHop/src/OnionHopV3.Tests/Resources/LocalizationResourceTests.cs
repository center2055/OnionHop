using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace OnionHopV3.Tests.Resources;

/// <summary>
/// Guards the localization resource dictionaries. A duplicate x:Key does not fail the build (the XAML
/// compiles), but it throws at runtime the moment LocalizationService loads the dictionary, aborting
/// the app on startup before any window appears — exactly the v3.0.1 crash. These tests catch that
/// class of error at test time so it can never ship again.
/// </summary>
public sealed class LocalizationResourceTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string ResourcesDir([CallerFilePath] string thisFile = "")
    {
        // thisFile = <repo>/OnionHop/src/OnionHopV3.Tests/Resources/LocalizationResourceTests.cs
        var resourcesParent = Directory.GetParent(Path.GetDirectoryName(thisFile)!)!.FullName; // .../OnionHopV3.Tests
        var srcDir = Directory.GetParent(resourcesParent)!.FullName;                            // .../src
        return Path.Combine(srcDir, "OnionHopV3.App", "Resources");
    }

    public static TheoryData<string> LanguageFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(ResourcesDir(), "Strings.*.axaml"))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LanguageFiles))]
    public void StringsFile_HasNoDuplicateKeys(string fileName)
    {
        var path = Path.Combine(ResourcesDir(), fileName);
        var doc = XDocument.Load(path);

        var duplicateKeys = doc.Descendants()
            .Select(e => e.Attribute(XamlNs + "Key")?.Value)
            .Where(k => !string.IsNullOrEmpty(k))
            .GroupBy(k => k)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(
            duplicateKeys.Count == 0,
            $"{fileName} has duplicate x:Key(s) that would crash the app on startup: {string.Join(", ", duplicateKeys)}");
    }

    [Fact]
    public void AllLanguages_AreDiscovered()
    {
        // Sanity check the path resolution itself, so a broken ResourcesDir() can't silently make the
        // duplicate-key theory pass with zero cases.
        var files = Directory.GetFiles(ResourcesDir(), "Strings.*.axaml").Select(Path.GetFileName).ToList();
        Assert.Contains("Strings.en.axaml", files);
        Assert.True(files.Count >= 8, $"Expected at least 8 language files (en/de/fr/ru/zh/fa/ckb/azb), found {files.Count}.");
    }

    private static System.Collections.Generic.HashSet<string> KeysOf(string fileName)
    {
        var doc = XDocument.Load(Path.Combine(ResourcesDir(), fileName));
        return doc.Descendants()
            .Select(e => e.Attribute(XamlNs + "Key")?.Value)
            .Where(k => !string.IsNullOrEmpty(k))
            .Select(k => k!)
            .ToHashSet();
    }

    [Theory]
    [MemberData(nameof(LanguageFiles))]
    public void StringsFile_HasNoKeysUnknownToEnglish(string fileName)
    {
        if (fileName == "Strings.en.axaml") return;

        var english = KeysOf("Strings.en.axaml");
        var unknown = KeysOf(fileName).Where(k => !english.Contains(k)).OrderBy(k => k).ToList();

        // A misspelled/hallucinated key never resolves at runtime (it silently falls back to English),
        // so guard against translators inventing keys that don't exist in the source dictionary.
        Assert.True(
            unknown.Count == 0,
            $"{fileName} has {unknown.Count} key(s) not present in Strings.en.axaml: {string.Join(", ", unknown.Take(15))}");
    }

    [Theory]
    [MemberData(nameof(LanguageFiles))]
    public void StringsFile_CoversMostEnglishKeys(string fileName)
    {
        if (fileName == "Strings.en.axaml") return;

        var english = KeysOf("Strings.en.axaml");
        var keys = KeysOf(fileName);
        var covered = english.Count(keys.Contains);
        var ratio = (double)covered / english.Count;

        // Untranslated keys fall back to English, but a file covering far fewer than the full set is a
        // sign of a truncated/incomplete translation that should not ship.
        Assert.True(
            ratio >= 0.90,
            $"{fileName} only covers {covered}/{english.Count} ({ratio:P0}) of English keys — looks truncated.");
    }
}
