using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace OnionHopV3.App.Controls;

public sealed class MarkdownTextBlock : StackPanel
{
    // Subtle, theme-agnostic table chrome (resource keys vary by theme, so use fixed translucent tones).
    private static readonly IBrush TableGridLine = new SolidColorBrush(Color.FromArgb(45, 255, 255, 255));
    private static readonly IBrush TableHeaderFill = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));

    public static readonly StyledProperty<string> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, string>(nameof(Markdown), string.Empty);

    public MarkdownTextBlock()
    {
        Spacing = 6;
    }

    static MarkdownTextBlock()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownTextBlock>((control, _) => control.RenderMarkdown());
    }

    public string Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void RenderMarkdown()
    {
        Children.Clear();

        var lines = (Markdown ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');

        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i].TrimEnd();

            // GitHub-flavored table: a header row immediately followed by a |---|---| separator row.
            if (IsTableRow(line) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                var header = ParseTableRow(line);
                var rows = new List<string[]>();
                i += 2;
                while (i < lines.Length && IsTableRow(lines[i].TrimEnd()) && !IsTableSeparator(lines[i]))
                {
                    rows.Add(ParseTableRow(lines[i].TrimEnd()));
                    i++;
                }

                Children.Add(BuildTable(header, rows));
                continue;
            }

            RenderLine(line);
            i++;
        }
    }

    private void RenderLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            Children.Add(new Border { Height = 4 });
            return;
        }

        if (line.StartsWith("### ", StringComparison.Ordinal))
        {
            Children.Add(CreateLine(line[4..], 15, FontWeight.SemiBold, "TextPrimaryBrush"));
            return;
        }

        if (line.StartsWith("## ", StringComparison.Ordinal))
        {
            Children.Add(CreateLine(line[3..], 17, FontWeight.SemiBold, "TextPrimaryBrush"));
            return;
        }

        if (line.StartsWith("# ", StringComparison.Ordinal))
        {
            Children.Add(CreateLine(line[2..], 18, FontWeight.SemiBold, "TextPrimaryBrush"));
            return;
        }

        if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
        {
            Children.Add(CreateLine("- " + line[2..], 14, FontWeight.Normal, "TextSecondaryBrush"));
            return;
        }

        Children.Add(CreateLine(line, 14, FontWeight.Normal, "TextSecondaryBrush"));
    }

    private static bool IsTableRow(string line)
    {
        var t = line.Trim();
        return t.Length > 0 && t.Contains('|', StringComparison.Ordinal);
    }

    private static bool IsTableSeparator(string line)
    {
        var cells = ParseTableRow(line);
        if (cells.Length == 0)
        {
            return false;
        }

        foreach (var cell in cells)
        {
            var c = cell.Trim();
            if (c.Length == 0 || !c.Contains('-', StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var ch in c)
            {
                if (ch != '-' && ch != ':')
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string[] ParseTableRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith("|", StringComparison.Ordinal))
        {
            t = t[1..];
        }

        if (t.EndsWith("|", StringComparison.Ordinal))
        {
            t = t[..^1];
        }

        var cells = t.Split('|');
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i] = cells[i].Trim();
        }

        return cells;
    }

    private Control BuildTable(string[] header, List<string[]> rows)
    {
        var columns = Math.Max(1, header.Length);
        var grid = new Grid();
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        foreach (var _ in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        AddTableRow(grid, 0, header, columns, isHeader: true);
        for (var r = 0; r < rows.Count; r++)
        {
            AddTableRow(grid, r + 1, rows[r], columns, isHeader: false);
        }

        return new Border
        {
            BorderBrush = TableGridLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 2, 0, 2),
            Child = grid
        };
    }

    private void AddTableRow(Grid grid, int rowIndex, string[] cells, int columns, bool isHeader)
    {
        var lastRow = rowIndex == grid.RowDefinitions.Count - 1;
        for (var c = 0; c < columns; c++)
        {
            var text = c < cells.Length ? cells[c] : string.Empty;
            var textBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                FontWeight = isHeader ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = GetBrush(isHeader ? "TextPrimaryBrush" : "TextSecondaryBrush"),
                Inlines = new InlineCollection()
            };
            AppendInlineRuns(textBlock.Inlines, text);

            var cell = new Border
            {
                BorderBrush = TableGridLine,
                BorderThickness = new Thickness(0, 0, c < columns - 1 ? 1 : 0, lastRow ? 0 : 1),
                Padding = new Thickness(10, 6),
                Background = isHeader ? TableHeaderFill : null,
                Child = textBlock
            };
            Grid.SetRow(cell, rowIndex);
            Grid.SetColumn(cell, c);
            grid.Children.Add(cell);
        }
    }

    private TextBlock CreateLine(string text, double fontSize, FontWeight weight, string foregroundKey)
    {
        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = GetBrush(foregroundKey)
        };

        textBlock.Inlines = new InlineCollection();
        AppendInlineRuns(textBlock.Inlines, text);
        return textBlock;
    }

    private static void AppendInlineRuns(InlineCollection inlines, string text)
    {
        var cursor = 0;
        while (cursor < text.Length)
        {
            var bold = text.IndexOf("**", cursor, StringComparison.Ordinal);
            var code = text.IndexOf('`', cursor);
            var next = MinPositive(bold, code);

            if (next < 0)
            {
                inlines.Add(new Run(text[cursor..]));
                return;
            }

            if (next > cursor)
            {
                inlines.Add(new Run(text[cursor..next]));
            }

            if (next == bold)
            {
                var end = text.IndexOf("**", next + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    inlines.Add(new Run(text[next..]));
                    return;
                }

                inlines.Add(new Run(text[(next + 2)..end]) { FontWeight = FontWeight.SemiBold });
                cursor = end + 2;
                continue;
            }

            var codeEnd = text.IndexOf('`', next + 1);
            if (codeEnd < 0)
            {
                inlines.Add(new Run(text[next..]));
                return;
            }

            inlines.Add(new Run(text[(next + 1)..codeEnd]) { FontFamily = FontFamily.Parse("Consolas") });
            cursor = codeEnd + 1;
        }
    }

    private static int MinPositive(int left, int right)
    {
        if (left < 0)
        {
            return right;
        }

        if (right < 0)
        {
            return left;
        }

        return Math.Min(left, right);
    }

    private static IBrush GetBrush(string key)
    {
        return (Application.Current?.TryFindResource(key, out var resource) == true
                ? resource as IBrush
                : null)
               ?? Brushes.White;
    }
}
