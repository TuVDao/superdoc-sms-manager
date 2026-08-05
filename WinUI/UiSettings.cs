using Microsoft.Extensions.Logging;
using MyApp.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Message_T480s.WinUI;

/// <summary>
/// User interface preferences - text size and panel geometry - persisted in the database so they
/// survive a restart and a republish.
/// </summary>
/// <remarks>
/// Sizes are exposed as separate bindable properties rather than being computed in XAML, because
/// WinUI has no arithmetic in bindings and multi-binding converters would have to be wired to
/// every element individually.
/// </remarks>
public sealed class UiSettings : INotifyPropertyChanged
{
    private const string FontSizeKey = "ui.fontSize";
    private const string PanelWidthKey = "ui.leftPanelWidth";
    /// <summary>
    /// Version-bumped key. The first release shipped a composer twice this tall, which looked
    /// unbalanced; a new key makes existing installs pick up the smaller default instead of
    /// keeping the old stored value forever.
    /// </summary>
    private const string ComposerHeightKey = "ui.composerHeight.v2";
    private const string LanguageKey = "ui.language";

    /// <summary>Roomier than the WinUI default of 14: the previous UI read as cramped.</summary>
    private const double DefaultFontSize = 15;

    private const double DefaultPanelWidth = 320;
    private const double DefaultComposerHeight = 110;

    private readonly SmsManager _smsManager;
    private readonly ILogger _logger;
    private readonly Strings _loc;
    private string _languageCode = string.Empty;

    private double _fontSize = DefaultFontSize;
    private double _leftPanelWidth = DefaultPanelWidth;
    private double _composerHeight = DefaultComposerHeight;
    private bool _isLeftPanelVisible = true;
    private bool _loaded;

    public UiSettings(SmsManager smsManager, ILogger logger, Strings loc)
    {
        _smsManager = smsManager;
        _logger = logger;
        _loc = loc;

        Load();

        // Built once, after Load has settled the language, and never replaced afterwards.
        FontSizeOptions =
        [
            new(loc, "FontSmall", 13),
            new(loc, "FontMedium", 15),
            new(loc, "FontLarge", 17),
            new(loc, "FontXLarge", 20),
            new(loc, "FontXXLarge", 24)
        ];

        // "Automatic" is translated; every real language is listed under its own endonym
        // ("Deutsch", "日本語"), which must not change with the interface language.
        LanguageOptions = [new PickerOption<string>(loc, "LanguageAuto", string.Empty)];
        foreach (var pack in loc.Available)
        {
            LanguageOptions.Add(new PickerOption<string>(pack.Name, pack.Code));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PickerOption<double>> FontSizeOptions { get; }

    public ObservableCollection<PickerOption<string>> LanguageOptions { get; }

    /// <summary>Empty follows the machine's display language; a code pins that language.</summary>
    public string LanguageCode
    {
        get => _languageCode;
        set
        {
            if (SetField(ref _languageCode, value ?? string.Empty))
            {
                _loc.Apply(string.IsNullOrEmpty(_languageCode) ? null : _languageCode);
                RefreshLocalisedOptions();
                Save(LanguageKey, _languageCode);
                OnPropertyChanged(nameof(SelectedLanguageOption));
            }
        }
    }

    public PickerOption<string>? SelectedLanguageOption
    {
        get => LanguageOptions.FirstOrDefault(o => o.Value == LanguageCode);
        set
        {
            if (value is not null)
            {
                LanguageCode = value.Value;
            }
        }
    }

    /// <summary>
    /// Relabels the picker entries in the new language. The objects themselves are never
    /// replaced: doing that invalidated the ComboBox's selection, which then wrote a different
    /// value back through its two-way binding and changed the language on its own.
    /// </summary>
    private void RefreshLocalisedOptions()
    {
        foreach (var option in FontSizeOptions)
        {
            option.RefreshName();
        }

        foreach (var option in LanguageOptions)
        {
            option.RefreshName();
        }

        OnPropertyChanged(nameof(TogglePanelTooltip));
    }

    /// <summary>Base text size; every other size is derived from it.</summary>
    public double FontSize
    {
        get => _fontSize;
        set
        {
            var clamped = Math.Clamp(value, 11, 28);
            if (SetField(ref _fontSize, clamped))
            {
                OnPropertyChanged(nameof(SmallFontSize));
                OnPropertyChanged(nameof(HeaderFontSize));
                OnPropertyChanged(nameof(TitleFontSize));
                OnPropertyChanged(nameof(SelectedFontSizeOption));
                Save(FontSizeKey, clamped);
            }
        }
    }

    /// <summary>Secondary text. Kept close to the body size so it stays comfortably readable.</summary>
    public double SmallFontSize => Math.Max(11, FontSize - 2);

    public double HeaderFontSize => FontSize + 2;

    public double TitleFontSize => FontSize + 6;

    /// <summary>Two-way bound to the picker; snaps to the nearest listed size.</summary>
    public PickerOption<double>? SelectedFontSizeOption
    {
        get => FontSizeOptions.FirstOrDefault(o => Math.Abs(o.Value - FontSize) < 0.5)
               ?? FontSizeOptions.OrderBy(o => Math.Abs(o.Value - FontSize)).FirstOrDefault();
        set
        {
            if (value is not null)
            {
                FontSize = value.Value;
            }
        }
    }

    public double LeftPanelWidth
    {
        get => _leftPanelWidth;
        set
        {
            var clamped = Math.Clamp(value, 180, 640);
            if (SetField(ref _leftPanelWidth, clamped))
            {
                Save(PanelWidthKey, clamped);
            }
        }
    }

    public bool IsLeftPanelVisible
    {
        get => _isLeftPanelVisible;
        set
        {
            if (SetField(ref _isLeftPanelVisible, value))
            {
                OnPropertyChanged(nameof(TogglePanelGlyph));
                OnPropertyChanged(nameof(TogglePanelTooltip));
            }
        }
    }

    public string TogglePanelGlyph => IsLeftPanelVisible ? "" : "";

    public string TogglePanelTooltip => IsLeftPanelVisible ? _loc.HideList : _loc.ShowList;

    /// <summary>Height of the message composer, dragged by the sizer above it.</summary>
    public double ComposerHeight
    {
        get => _composerHeight;
        set
        {
            var clamped = Math.Clamp(value, 90, 600);
            if (SetField(ref _composerHeight, clamped))
            {
                Save(ComposerHeightKey, clamped);
            }
        }
    }

    public void ToggleLeftPanel() => IsLeftPanelVisible = !IsLeftPanelVisible;

    /// <summary>
    /// Translates the setting written by the earlier enum-based version ("Vietnamese", "English",
    /// "Auto") into a language code. Without this an existing install would silently revert to
    /// the machine's language the first time it ran a build with translation files.
    /// </summary>
    private static string MigrateLanguageSetting(string? stored) => stored switch
    {
        null or "" or "Auto" => string.Empty,
        "Vietnamese" => "vi",
        "English" => "en",
        _ => stored
    };

    private void Load()
    {
        try
        {
            _languageCode = MigrateLanguageSetting(_smsManager.GetSetting(LanguageKey));
            _loc.Apply(string.IsNullOrEmpty(_languageCode) ? null : _languageCode);

            _fontSize = ReadDouble(FontSizeKey, DefaultFontSize, 11, 28);
            _leftPanelWidth = ReadDouble(PanelWidthKey, DefaultPanelWidth, 180, 640);
            _composerHeight = ReadDouble(ComposerHeightKey, DefaultComposerHeight, 90, 600);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read UI settings; using defaults.");
        }
        finally
        {
            // Only now may writes happen - loading must not save the values straight back.
            _loaded = true;
        }
    }

    private double ReadDouble(string key, double fallback, double min, double max)
    {
        var raw = _smsManager.GetSetting(key);
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }

    private void Save(string key, string value)
    {
        if (!_loaded)
        {
            return;
        }

        try
        {
            _smsManager.SetSetting(key, value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save UI setting {Key}.", key);
        }
    }

    private void Save(string key, double value)
    {
        if (!_loaded)
        {
            return;
        }

        try
        {
            _smsManager.SetSetting(key, value.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save UI setting {Key}.", key);
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
