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
        Assert.True(files.Count >= 5, $"Expected at least 5 language files, found {files.Count}.");
    }
}
