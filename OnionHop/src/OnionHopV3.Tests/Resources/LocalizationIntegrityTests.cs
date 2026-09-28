using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace OnionHopV3.Tests.Resources;

/// <summary>
/// The UI strings live in Strings.&lt;lang&gt;.axaml resource dictionaries, and two kinds of mistake in
/// them compile cleanly and only fail at runtime: a key defined twice makes the dictionary throw while
/// loading, which takes the whole app down at startup (the Home redesign briefly did exactly that),
/// and a translation that drops or invents a "{0}" makes string.Format throw the moment the string
/// is shown. These tests read the files straight from the source tree, so they need no UI assembly.
/// </summary>
public sealed class LocalizationIntegrityTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly Regex Placeholder = new(@"\{\d+\}", RegexOptions.Compiled);

    internal static string ResourcesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "OnionHopV3.App", "Resources");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.Combine(dir.FullName, "OnionHopV3.App", "Resources");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate OnionHopV3.App/Resources from " + AppContext.BaseDirectory);
    }

    public static IEnumerable<object[]> Languages() =>
        Directory.GetFiles(ResourcesDirectory(), "Strings.*.axaml")
            .Select(path => new object[] { Path.GetFileName(path) })
            .OrderBy(item => (string)item[0]);

    private static List<(string Key, string Value)> Entries(string fileName)
    {
        var doc = XDocument.Load(Path.Combine(ResourcesDirectory(), fileName));
        return doc.Root!
            .Elements()
            .Where(e => e.Attribute(X + "Key") != null)
            .Select(e => (e.Attribute(X + "Key")!.Value, e.Value))
            .ToList();
    }

    [Fact]
    public void Every_language_file_is_found()
    {
        // Guards the tests below against silently checking nothing if the files move.
        Assert.True(Languages().Count() >= 8, "expected at least 8 Strings.*.axaml files");
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void No_key_is_defined_twice(string fileName)
    {
        var duplicates = Entries(fileName)
            .GroupBy(entry => entry.Key)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.True(duplicates.Count == 0,
            $"{fileName} defines these keys more than once, which crashes the app at startup: {string.Join(", ", duplicates)}");
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Translations_only_use_keys_that_exist_in_english(string fileName)
    {
        if (fileName == "Strings.en.axaml")
        {
            return;
        }

        var english = Entries("Strings.en.axaml").Select(e => e.Key).ToHashSet();
        var orphans = Entries(fileName).Select(e => e.Key).Where(key => !english.Contains(key)).ToList();

        Assert.True(orphans.Count == 0,
            $"{fileName} has keys English does not, so they can never be shown: {string.Join(", ", orphans)}");
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Translations_keep_the_same_format_placeholders(string fileName)
    {
        if (fileName == "Strings.en.axaml")
        {
            return;
        }

        var english = Entries("Strings.en.axaml").ToDictionary(e => e.Key, e => e.Value);
        var mismatched = new List<string>();
        foreach (var (key, value) in Entries(fileName))
        {
            if (!english.TryGetValue(key, out var source))
            {
                continue;
            }

            var expected = Placeholder.Matches(source).Select(m => m.Value).Distinct().OrderBy(v => v);
            var actual = Placeholder.Matches(value).Select(m => m.Value).Distinct().OrderBy(v => v);
            if (!expected.SequenceEqual(actual))
            {
                mismatched.Add(key);
            }
        }

        Assert.True(mismatched.Count == 0,
            $"{fileName} changes the {{n}} placeholders of these strings, which makes string.Format throw when shown: {string.Join(", ", mismatched)}");
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Letters_come_from_the_script_the_language_is_written_in(string fileName)
    {
        // A Devanagari letter once sat in the middle of a Kurdish word, where it renders as a stray
        // glyph and nothing else catches it. Latin is allowed everywhere: product names, Tor, URLs.
        var language = fileName["Strings.".Length..^".axaml".Length];
        Func<char, bool> native = language switch
        {
            "ru" => c => c is >= 'Ѐ' and <= 'ӿ',
            "zh" => c => c is >= '　' and <= '〿' or >= '㐀' and <= '䶿' or >= '一' and <= '鿿' or >= '＀' and <= '￯',
            "fa" or "azb" or "ckb" => c => c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿',
            _ => _ => false
        };

        var offenders = Entries(fileName)
            .Where(entry => entry.Value.Any(c => char.IsLetter(c) && !IsLatin(c) && !native(c)))
            .Select(entry => entry.Key)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{fileName} has letters from another script in: {string.Join(", ", offenders)}");
    }

    private static bool IsLatin(char c) => c <= 'ɏ' || c is >= 'Ḁ' and <= 'ỿ';
}
