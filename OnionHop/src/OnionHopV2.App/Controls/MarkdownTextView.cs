using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OnionHopV2.App.Controls;

public sealed class MarkdownTextView : UserControl
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownTextView, string?>(nameof(Markdown));

    private static readonly Regex LinkRegex = new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);
    private readonly StackPanel _panel;

    public MarkdownTextView()
    {
        _panel = new StackPanel
        {
            Spacing = 8
        };

        Content = _panel;
    }

    static MarkdownTextView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownTextView>((view, _) => view.RenderMarkdown());
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private void RenderMarkdown()
    {
        _panel.Children.Clear();

        var raw = Markdown;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        var normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        var lines = normalized.Split('\n');
        var paragraphLines = new List<string>();
        var codeLines = new List<string>();
        var inCodeBlock = false;

        foreach (var originalLine in lines)
        {
            var line = originalLine.TrimEnd();
            var trimmed = line.Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph(paragraphLines);

                if (inCodeBlock)
                {
                    AddCodeBlock(codeLines);
                    codeLines.Clear();
                    inCodeBlock = false;
                }
                else
                {
                    inCodeBlock = true;
                }

                continue;
            }

            if (inCodeBlock)
            {
                codeLines.Add(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                FlushParagraph(paragraphLines);
                continue;
            }

            if (TryAddHeading(trimmed))
            {
                FlushParagraph(paragraphLines);
                continue;
            }

            if (TryAddListItem(trimmed))
            {
                FlushParagraph(paragraphLines);
                continue;
            }

            paragraphLines.Add(trimmed);
        }

        FlushParagraph(paragraphLines);
        AddCodeBlock(codeLines);
    }

    private void FlushParagraph(List<string> paragraphLines)
    {
        if (paragraphLines.Count == 0)
        {
            return;
        }

        var text = string.Join(" ", paragraphLines);
        _panel.Children.Add(new TextBlock
        {
            Text = StripInlineMarkdown(text),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.92
        });

        paragraphLines.Clear();
    }

    private bool TryAddHeading(string line)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        if (level == 0 || level > 6 || line.Length <= level || !char.IsWhiteSpace(line[level]))
        {
            return false;
        }

        var title = StripInlineMarkdown(line[(level + 1)..]);
        _panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = level switch
            {
                1 => 20,
                2 => 18,
                3 => 16,
                _ => 15
            },
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });

        return true;
    }

    private bool TryAddListItem(string line)
    {
        var content = line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)
            ? line[2..]
            : TryTrimOrderedPrefix(line);

        if (content == null)
        {
            return false;
        }

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = 8
        };

        row.Children.Add(new TextBlock
        {
            Text = "\u2022",
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Top
        });

        row.Children.Add(new TextBlock
        {
            Text = StripInlineMarkdown(content),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top
        });
        Grid.SetColumn(row.Children[1], 1);

        _panel.Children.Add(row);
        return true;
    }

    private void AddCodeBlock(List<string> codeLines)
    {
        if (codeLines.Count == 0)
        {
            return;
        }

        _panel.Children.Add(new Border
        {
            Background = Brush.Parse("#14000000"),
            BorderBrush = Brush.Parse("#26FFFFFF"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Child = new TextBlock
            {
                Text = string.Join(Environment.NewLine, codeLines),
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Cascadia Code,Consolas,Monaco,monospace"),
                Opacity = 0.92
            }
        });

        codeLines.Clear();
    }

    private static string? TryTrimOrderedPrefix(string line)
    {
        var separatorIndex = line.IndexOf(". ", StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return null;
        }

        for (var i = 0; i < separatorIndex; i++)
        {
            if (!char.IsDigit(line[i]))
            {
                return null;
            }
        }

        return line[(separatorIndex + 2)..];
    }

    private static string StripInlineMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var value = LinkRegex.Replace(text.Trim(), "$1");
        value = value.Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal);

        return CollapseWhitespace(value);
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousWasWhitespace = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (previousWasWhitespace)
                {
                    continue;
                }

                builder.Append(' ');
                previousWasWhitespace = true;
                continue;
            }

            builder.Append(ch);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }
}
