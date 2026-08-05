using System.ComponentModel;

namespace Message_T480s.WinUI;

/// <summary>
/// An entry in a ComboBox whose label may follow the active language.
/// </summary>
/// <remarks>
/// The instances are created once and kept. Rebuilding the collection on every language change
/// made the ComboBox lose the item it had selected and write a different one back through its
/// two-way binding — which silently changed the user's language a moment after start-up.
/// Only the label changes now, so the selected item is never invalidated.
///
/// A literal name is used for language entries: each language is listed in its own script
/// ("Deutsch", "日本語"), which must not change when the interface language does.
/// </remarks>
public sealed class PickerOption<T> : INotifyPropertyChanged
{
    private readonly Strings? _loc;
    private readonly string _key;
    private readonly string? _literal;

    /// <summary>An entry whose label is translated.</summary>
    public PickerOption(Strings loc, string key, T value)
    {
        _loc = loc;
        _key = key;
        Value = value;
    }

    /// <summary>An entry whose label is fixed, whatever the interface language.</summary>
    public PickerOption(string literalName, T value)
    {
        _key = literalName;
        _literal = literalName;
        Value = value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public T Value { get; }

    public string Name => _literal ?? _loc?.Text(_key) ?? _key;

    public void RefreshName()
    {
        if (_literal is null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    public override string ToString() => Name;
}
