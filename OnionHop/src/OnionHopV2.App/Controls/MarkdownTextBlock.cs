using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace OnionHopV2.App.Controls;

public sealed class MarkdownTextBlock : StackPanel
{
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

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                Children.Add(new Border { Height = 4 });
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                Children.Add(CreateLine(line[4..], 15, FontWeight.SemiBold, "TextPrimaryBrush"));
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Children.Add(CreateLine(line[3..], 17, FontWeight.SemiBold, "TextPrimaryBrush"));
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                Children.Add(CreateLine(line[2..], 18, FontWeight.SemiBold, "TextPrimaryBrush"));
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                Children.Add(CreateLine("- " + line[2..], 14, FontWeight.Normal, "TextSecondaryBrush"));
                continue;
            }

            Children.Add(CreateLine(line, 14, FontWeight.Normal, "TextSecondaryBrush"));
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
