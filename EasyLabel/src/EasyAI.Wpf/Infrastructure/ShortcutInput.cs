using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;

namespace EasyAI.Desktop.Infrastructure;

/// <summary>Reads key events and the keyboard focus for <see cref="ShortcutTable"/> (the WPF side of the table).</summary>
public static class ShortcutInput
{
    /// <summary>The shortcut the key event stands for, or null. Alt combinations arrive as <c>Key.System</c>.</summary>
    public static ShortcutEntry? Match(KeyEventArgs e)
    {
        Key key = e.Key;
        if (key == Key.System)
        {
            key = e.SystemKey;
        }
        else if (key == Key.ImeProcessed)
        {
            key = e.ImeProcessedKey;
        }

        return ShortcutTable.Find(key.ToString(), CurrentModifiers());
    }

    public static ShortcutModifiers CurrentModifiers()
    {
        ModifierKeys mods = Keyboard.Modifiers;
        ShortcutModifiers result = ShortcutModifiers.None;
        if ((mods & ModifierKeys.Control) != 0) result |= ShortcutModifiers.Ctrl;
        if ((mods & ModifierKeys.Shift) != 0) result |= ShortcutModifiers.Shift;
        if ((mods & ModifierKeys.Alt) != 0) result |= ShortcutModifiers.Alt;
        if ((mods & ModifierKeys.Windows) != 0) result |= ShortcutModifiers.Meta;
        return result;
    }

    /// <summary>True while a text box, combo box or slider has the focus: they keep every key for themselves.</summary>
    public static bool BlocksShortcuts(IInputElement? focused)
    {
        return focused is TextBoxBase
            || focused is PasswordBox
            || focused is ComboBox
            || focused is ComboBoxItem
            || focused is MenuItem
            || focused is RangeBase;
    }
}

/// <summary>XAML access to the table: <c>{infra:Shortcut OpenImages}</c> is "Ctrl+O"; with <c>Text</c> it is "caption (Ctrl+O)".</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class ShortcutExtension : MarkupExtension
{
    public ShortcutExtension()
    {
    }

    public ShortcutExtension(ShortcutId id)
    {
        Id = id;
    }

    [ConstructorArgument("id")]
    public ShortcutId Id { get; set; }

    /// <summary>Caption put in front of the shortcut (tooltips). Null gives the bare shortcut text (menu items).</summary>
    public string? Text { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return Text == null ? ShortcutTable.TextOf(Id) : ShortcutTable.WithShortcut(Text, Id);
    }
}
