using System;
using System.Collections.Generic;

namespace OnionHopV3.App.Services;

/// <summary>
/// Parsing/formatting for the Hybrid split-tunnel app lists (HybridTorApps / HybridBypassApps).
/// Mirrors the Core process-name parsing: splits on newlines/commas/etc, strips paths/quotes, and
/// appends ".exe" on Windows so picker entries and hand-typed names compare consistently.
/// </summary>
public static class HybridAppList
{
    private static readonly char[] Separators = { '\n', '\r', ',', ';', '\t', ' ' };

    public static List<string> Parse(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = raw.Trim().Trim('"');
            var lastSep = Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/'));
            if (lastSep >= 0)
            {
                name = name[(lastSep + 1)..];
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name += ".exe";
            }

            if (seen.Add(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    public static string Join(IEnumerable<string> names) => string.Join(Environment.NewLine, names);
}
