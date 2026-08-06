using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace SuperDoc.Sms.WinUI;

/// <summary>One installed translation, described by the `_meta` block of its JSON file.</summary>
public sealed record LanguagePack(
    string Code,
    string Name,
    bool RightToLeft,
    bool Reviewed,
    IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Every user-visible string, loaded from <c>Strings\*.json</c> beside the executable.
/// </summary>
/// <remarks>
/// The translations deliberately live outside the code. Adding or fixing a language is then a
/// single JSON file and needs no build, which is the only realistic way to get good translations
/// for languages the authors do not speak.
///
/// English is the fallback: any key missing from a translation falls back to the English text
/// rather than showing a raw key, so a partial translation degrades gracefully instead of
/// looking broken.
///
/// Raising PropertyChanged with an empty name tells the binding engine every property changed,
/// so one event re-reads the whole window and the UI swaps language live.
///
/// Log messages stay English: they are diagnostics, not interface.
/// </remarks>
public sealed class Strings : INotifyPropertyChanged
{
    private const string FallbackCode = "en";

    private readonly Dictionary<string, LanguagePack> _packs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger? _logger;

    private IReadOnlyDictionary<string, string> _active = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> _fallback = new Dictionary<string, string>();

    public Strings(ILogger? logger = null)
    {
        _logger = logger;
        LoadPacks();
        Apply(null);
        Current = this;
    }

    /// <summary>
    /// The instance value converters read from. They are constructed by the XAML parser and get
    /// no constructor arguments, so there is nowhere else to reach the active language from.
    /// </summary>
    public static Strings Current { get; private set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Every translation found on disk, English first, then alphabetically by name.</summary>
    public IReadOnlyList<LanguagePack> Available { get; private set; } = [];

    /// <summary>The language actually in use once "automatic" has been resolved.</summary>
    public string ResolvedCode { get; private set; } = FallbackCode;

    /// <summary>True when the active language is written right to left.</summary>
    public bool IsRightToLeft { get; private set; }

    /// <summary>
    /// Applies a language by code. Pass null or an unknown code to follow the machine's display
    /// language, falling back to English.
    /// </summary>
    public void Apply(string? code)
    {
        var pack = Resolve(code);

        ResolvedCode = pack.Code;
        IsRightToLeft = pack.RightToLeft;
        _active = pack.Values;

        // Empty name = "all properties"; one event refreshes every bound string.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private LanguagePack Resolve(string? code)
    {
        if (!string.IsNullOrWhiteSpace(code) && _packs.TryGetValue(code, out var exact))
        {
            return exact;
        }

        // "Automatic": walk the culture chain, so pt-BR matches a pt pack and zh-Hans-CN
        // matches zh-Hans, then zh.
        try
        {
            var culture = CultureInfo.CurrentUICulture;
            while (culture is not null && !string.IsNullOrEmpty(culture.Name))
            {
                if (_packs.TryGetValue(culture.Name, out var byName))
                {
                    return byName;
                }

                if (_packs.TryGetValue(culture.TwoLetterISOLanguageName, out var byLanguage))
                {
                    return byLanguage;
                }

                culture = culture.Parent;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not read the current UI culture; falling back to English.");
        }

        return _packs.TryGetValue(FallbackCode, out var english)
            ? english
            : new LanguagePack(FallbackCode, "English", false, true, new Dictionary<string, string>());
    }

    private void LoadPacks()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Strings");
        if (!Directory.Exists(directory))
        {
            _logger?.LogError("No translations found at {Path}; the UI will show raw keys.", directory);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var pack = ReadPack(file);
                _packs[pack.Code] = pack;
            }
            catch (Exception ex)
            {
                // One malformed translation must not take down every other language.
                _logger?.LogError(ex, "Ignoring translation {File}: it could not be read.", file);
            }
        }

        _fallback = _packs.TryGetValue(FallbackCode, out var english)
            ? english.Values
            : new Dictionary<string, string>();

        Available = _packs.Values
            .OrderByDescending(p => p.Code == FallbackCode)
            .ThenBy(p => p.Name, StringComparer.CurrentCulture)
            .ToList();

        _logger?.LogInformation("Loaded {Count} translation(s).", _packs.Count);
    }

    private static LanguagePack ReadPack(string file)
    {
        using var stream = File.OpenRead(file);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var code = Path.GetFileNameWithoutExtension(file);
        var name = code;
        var rtl = false;
        var reviewed = false;

        if (root.TryGetProperty("_meta", out var meta))
        {
            if (meta.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
            {
                code = c.GetString() ?? code;
            }

            if (meta.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
            {
                name = n.GetString() ?? name;
            }

            rtl = meta.TryGetProperty("rtl", out var r) && r.ValueKind == JsonValueKind.True;
            reviewed = meta.TryGetProperty("reviewed", out var v) && v.ValueKind == JsonValueKind.True;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (property.NameEquals("_meta") || property.Value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            values[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return new LanguagePack(code, name, rtl, reviewed, values);
    }

    /// <summary>Looks a string up by key, falling back to English and then to the key itself.</summary>
    public string Text(string key)
    {
        if (_active.TryGetValue(key, out var value) && value.Length > 0)
        {
            return value;
        }

        return _fallback.TryGetValue(key, out var english) ? english : key;
    }

    private string Format(string key, params object[] args)
    {
        try
        {
            return string.Format(Text(key), args);
        }
        catch (FormatException)
        {
            // A translation with the wrong placeholders must not crash the window.
            _logger?.LogWarning("Translation '{Key}' has malformed placeholders in {Code}.", key, ResolvedCode);
            return string.Format(_fallback.TryGetValue(key, out var english) ? english : key, args);
        }
    }

    // ---- Shell ----------------------------------------------------------------------
    public string CheckBalance => Text(nameof(CheckBalance));

    public string BalanceCodeLabel => Text(nameof(BalanceCodeLabel));

    public string BalanceChecking => Text(nameof(BalanceChecking));

    public string BalanceNoCode => Text(nameof(BalanceNoCode));

    public string SendRefusedHint => Text(nameof(SendRefusedHint));

    public string BalanceFailed(string error) => Format("BalanceFailed", error);

    public string Messages => Text(nameof(Messages));
    public string Contacts => Text(nameof(Contacts));
    public string FontSizeTooltip => Text(nameof(FontSizeTooltip));
    public string LanguageTooltip => Text(nameof(LanguageTooltip));
    public string ShowList => Text(nameof(ShowList));
    public string HideList => Text(nameof(HideList));
    public string CheckingModem => Text(nameof(CheckingModem));

    // ---- Conversations --------------------------------------------------------------
    public string NewMessage => Text(nameof(NewMessage));
    public string SearchPlaceholder => Text(nameof(SearchPlaceholder));
    public string SaveToContacts => Text(nameof(SaveToContacts));
    public string SelectConversation => Text(nameof(SelectConversation));
    public string NewMessageTitle => Text(nameof(NewMessageTitle));
    public string EnterRecipient => Text(nameof(EnterRecipient));
    public string RecipientHeader => Text(nameof(RecipientHeader));
    public string RecipientPlaceholder => Text(nameof(RecipientPlaceholder));
    public string MessagePlaceholder => Text(nameof(MessagePlaceholder));
    public string Send => Text(nameof(Send));
    public string Retry => Text(nameof(Retry));
    public string ErrorBadge => Text(nameof(ErrorBadge));
    public string PanelSizerTooltip => Text(nameof(PanelSizerTooltip));
    public string ComposerSizerTooltip => Text(nameof(ComposerSizerTooltip));
    public string NoRecipient => Text(nameof(NoRecipient));
    public string EmojiTooltip => Text(nameof(EmojiTooltip));

    // ---- Message status -------------------------------------------------------------
    public string StatusPending => Text(nameof(StatusPending));
    public string StatusSending => Text(nameof(StatusSending));
    public string StatusSent => Text(nameof(StatusSent));
    public string StatusFailed => Text(nameof(StatusFailed));

    // ---- Contacts -------------------------------------------------------------------
    public string AddContact => Text(nameof(AddContact));
    public string ContactInfo => Text(nameof(ContactInfo));
    public string ContactTitle => Text(nameof(ContactTitle));
    public string ContactTitlePlaceholder => Text(nameof(ContactTitlePlaceholder));
    public string FamilyName => Text(nameof(FamilyName));
    public string FamilyNamePlaceholder => Text(nameof(FamilyNamePlaceholder));
    public string GivenName => Text(nameof(GivenName));
    public string GivenNamePlaceholder => Text(nameof(GivenNamePlaceholder));
    public string PhoneNumber => Text(nameof(PhoneNumber));
    public string PhonePlaceholder => Text(nameof(PhonePlaceholder));
    public string Address => Text(nameof(Address));
    public string AddressPlaceholder => Text(nameof(AddressPlaceholder));
    public string Note => Text(nameof(Note));
    public string NotePlaceholder => Text(nameof(NotePlaceholder));
    public string Save => Text(nameof(Save));
    public string Delete => Text(nameof(Delete));
    public string Cancel => Text(nameof(Cancel));
    public string ContactNeedsPhone => Text(nameof(ContactNeedsPhone));
    public string ContactDeleted => Text(nameof(ContactDeleted));

    // ---- Tray and notifications -----------------------------------------------------
    public string TrayOpen => Text(nameof(TrayOpen));
    public string TrayTestNotification => Text(nameof(TrayTestNotification));
    public string TrayExit => Text(nameof(TrayExit));
    public string TestNotificationBody => Text(nameof(TestNotificationBody));
    public string UnknownNumber => Text(nameof(UnknownNumber));

    // ---- Dialogs --------------------------------------------------------------------
    public string DeleteMessagesTitle => Text(nameof(DeleteMessagesTitle));
    public string DeleteConversationsTitle => Text(nameof(DeleteConversationsTitle));

    // ---- Composer -------------------------------------------------------------------
    public string UnicodeWarning => Text(nameof(UnicodeWarning));

    // ---- Formatted ------------------------------------------------------------------
    public string DeleteNMessages(int n) => Format("DeleteNMessages", n);

    public string DeleteNConversations(int n) => Format("DeleteNConversations", n);

    public string ConfirmDeleteMessages(int n) => Format("ConfirmDeleteMessages", n);

    public string ConfirmDeleteConversations(string names) => Format("ConfirmDeleteConversations", names);

    public string AndNMore(int n) => Format("AndNMore", n);

    public string Queued(long id, string to, int segments) => Format("Queued", id, to, segments);

    public string SendFailed(string error) => Format("SendFailed", error);

    public string RetryQueued(long id) => Format("RetryQueued", id);

    public string RetryNotFailed(long id) => Format("RetryNotFailed", id);

    public string RetryFailed(string error) => Format("RetryFailed", error);

    public string LoadListFailed(string error) => Format("LoadListFailed", error);

    public string LoadContactsFailed(string error) => Format("LoadContactsFailed", error);

    public string DeletedMessages(int n) => Format("DeletedMessages", n);

    public string DeletedConversations(int threads, int messages) =>
        Format("DeletedConversations", threads, messages);

    public string DeleteFailed(string error) => Format("DeleteFailed", error);

    public string ContactSaved(string name) => Format("ContactSaved", name);

    public string SaveFailed(string error) => Format("SaveFailed", error);

    public string InvalidPhone(string value) => Format("InvalidPhone", value);

    public string ModemReady(string status, string number, string cellularClass) =>
        Format("ModemReady", status, number, cellularClass);

    public string SendAndReceive(string mode) => Format("SendAndReceive", mode);

    public string SendOnly(string diagnostic) => Format("SendOnly", diagnostic);

    public string ModemUnavailable(string diagnostic) => Format("ModemUnavailable", diagnostic);

    public string ModemStatusFailed(string error) => Format("ModemStatusFailed", error);

    public string ComposerCounter(int characters, int segments, bool unicode) =>
        Format("ComposerCounter", characters, segments, Text(unicode ? "EncodingUnicode" : "EncodingGsm"));
}
