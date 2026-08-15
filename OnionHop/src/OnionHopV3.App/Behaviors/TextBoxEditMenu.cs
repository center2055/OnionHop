using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace OnionHopV3.App.Behaviors;

/// <summary>
/// Opt-in right-click edit menu for text boxes: Cut, Copy, Paste, Delete and Select all, in the
/// user's own language. The stock text-box menu has no Delete, so clearing a pasted bridge list
/// meant selecting it and reaching for the keyboard; Select all followed by Delete now does it
/// entirely from the menu (tester request).
///
/// Each box builds its own flyout whose items close over that box, so there is no shared
/// "which box was right-clicked" state that could act on the wrong one.
/// </summary>
public static class TextBoxEditMenu
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("IsEnabled", typeof(TextBoxEditMenu));

    public static void SetIsEnabled(TextBox target, bool value) => target.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(TextBox target) => target.GetValue(IsEnabledProperty);

    static TextBoxEditMenu()
    {
        IsEnabledProperty.Changed.AddClassHandler<TextBox>(OnIsEnabledChanged);
    }

    private static void OnIsEnabledChanged(TextBox box, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.GetNewValue<bool>())
        {
            box.ContextFlyout = BuildFlyout(box);
        }
        else if (box.ContextFlyout is MenuFlyout)
        {
            // Only drop a flyout we installed; leave a hand-written one alone.
            box.ContextFlyout = null;
        }
    }

    private static MenuFlyout BuildFlyout(TextBox box)
    {
        var flyout = new MenuFlyout();
        flyout.Items.Add(Item("Edit.Cut", () => { if (!box.IsReadOnly) { box.Cut(); } }));
        flyout.Items.Add(Item("Edit.Copy", () => box.Copy()));
        flyout.Items.Add(Item("Edit.Paste", () => { if (!box.IsReadOnly) { box.Paste(); } }));
        flyout.Items.Add(Item("Edit.Delete", () => DeleteSelection(box)));
        flyout.Items.Add(new Separator());
        flyout.Items.Add(Item("Edit.SelectAll", () => box.SelectAll()));
        return flyout;
    }

    private static MenuItem Item(string headerKey, Action invoke)
    {
        var item = new MenuItem();
        // DynamicResource rather than a one-off lookup so the menu follows a language change.
        item.Bind(MenuItem.HeaderProperty, new DynamicResourceExtension(headerKey));
        item.Click += (_, _) => invoke();
        return item;
    }

    /// <summary>
    /// Delete removes the selected text, matching the standard Windows edit menu: with nothing
    /// selected there is nothing to delete, and it must never wipe the whole box by surprise.
    /// </summary>
    private static void DeleteSelection(TextBox box)
    {
        if (box.IsReadOnly || box.SelectionStart == box.SelectionEnd)
        {
            return;
        }

        box.SelectedText = string.Empty;
    }
}
