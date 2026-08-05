using System.ComponentModel;
using System.Globalization;

namespace Message_T480s.WinUI;

public enum AppLanguage
{
    /// <summary>Follow the machine's display language.</summary>
    Auto,
    Vietnamese,
    English
}

/// <summary>
/// A choice in a picker whose label follows the active language.
/// </summary>
/// <remarks>
/// The instances are created once and kept. Rebuilding the collection on every language change
/// made the ComboBox lose the item it had selected and write a different one back through its
/// two-way binding — which silently changed the user's language a moment after start-up.
/// Only the label changes now, so the selected item is never invalidated.
/// </remarks>
public sealed class PickerOption<T> : INotifyPropertyChanged
{
    private readonly Strings _loc;
    private readonly string _key;

    public PickerOption(Strings loc, string key, T value)
    {
        _loc = loc;
        _key = key;
        Value = value;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public T Value { get; }

    public string Name => _loc.Text(_key);

    public void RefreshName() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));

    public override string ToString() => Name;
}

/// <summary>
/// Every string the user sees, in Vietnamese and English.
/// </summary>
/// <remarks>
/// Deliberately not .resw + x:Uid. That is the conventional WinUI route, but it resolves
/// resources through MRT at load time, which makes switching language without restarting
/// awkward, and it leans on resources.pri — the same machinery that already needed careful
/// handling for the taskbar icons. A plain dictionary keeps the switch instant and behaves
/// identically packaged or unpackaged.
///
/// Raising PropertyChanged with an empty name tells the binding engine every property changed,
/// so one event re-reads the whole window and the UI swaps language live.
///
/// Log messages stay English: they are diagnostics, not interface.
/// </remarks>
public sealed class Strings : INotifyPropertyChanged
{
    private Dictionary<string, string> _active;
    private AppLanguage _resolved;

    public Strings(AppLanguage language = AppLanguage.Auto)
    {
        _active = Vietnamese;
        _resolved = AppLanguage.Vietnamese;
        Apply(language);
        Current = this;
    }

    /// <summary>
    /// The instance value converters read from. They are constructed by the XAML parser and get
    /// no constructor arguments, so there is nowhere else to reach the active language from.
    /// </summary>
    public static Strings Current { get; private set; } = new(AppLanguage.English);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The language actually in use once <see cref="AppLanguage.Auto"/> is resolved.</summary>
    public AppLanguage Resolved => _resolved;

    public void Apply(AppLanguage language)
    {
        var resolved = language == AppLanguage.Auto ? DetectFromSystem() : language;

        _resolved = resolved;
        _active = resolved == AppLanguage.English ? English : Vietnamese;

        // Empty name = "all properties"; one event refreshes every bound string.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>
    /// Vietnamese when the machine's display language is Vietnamese, English for anything else.
    /// </summary>
    public static AppLanguage DetectFromSystem()
    {
        try
        {
            var culture = CultureInfo.CurrentUICulture;
            while (culture is not null && !string.IsNullOrEmpty(culture.Name))
            {
                if (culture.TwoLetterISOLanguageName.Equals("vi", StringComparison.OrdinalIgnoreCase))
                {
                    return AppLanguage.Vietnamese;
                }

                culture = culture.Parent;
            }
        }
        catch (Exception)
        {
            // Fall through to the default below.
        }

        return AppLanguage.English;
    }

    private string Get(string key) => _active.TryGetValue(key, out var value) ? value : key;

    // ---- Shell ----------------------------------------------------------------------
    public string Messages => Get(nameof(Messages));
    public string Contacts => Get(nameof(Contacts));
    public string FontSizeTooltip => Get(nameof(FontSizeTooltip));
    public string LanguageTooltip => Get(nameof(LanguageTooltip));
    public string ShowList => Get(nameof(ShowList));
    public string HideList => Get(nameof(HideList));
    public string CheckingModem => Get(nameof(CheckingModem));

    // ---- Conversations --------------------------------------------------------------
    public string NewMessage => Get(nameof(NewMessage));
    public string SearchPlaceholder => Get(nameof(SearchPlaceholder));
    public string SaveToContacts => Get(nameof(SaveToContacts));
    public string SelectConversation => Get(nameof(SelectConversation));
    public string NewMessageTitle => Get(nameof(NewMessageTitle));
    public string EnterRecipient => Get(nameof(EnterRecipient));
    public string RecipientHeader => Get(nameof(RecipientHeader));
    public string RecipientPlaceholder => Get(nameof(RecipientPlaceholder));
    public string MessagePlaceholder => Get(nameof(MessagePlaceholder));
    public string Send => Get(nameof(Send));
    public string Retry => Get(nameof(Retry));
    public string ErrorBadge => Get(nameof(ErrorBadge));
    public string PanelSizerTooltip => Get(nameof(PanelSizerTooltip));
    public string ComposerSizerTooltip => Get(nameof(ComposerSizerTooltip));
    public string NoRecipient => Get(nameof(NoRecipient));

    // ---- Message status -------------------------------------------------------------
    public string StatusPending => Get(nameof(StatusPending));
    public string StatusSending => Get(nameof(StatusSending));
    public string StatusSent => Get(nameof(StatusSent));
    public string StatusFailed => Get(nameof(StatusFailed));

    // ---- Contacts -------------------------------------------------------------------
    public string AddContact => Get(nameof(AddContact));
    public string ContactInfo => Get(nameof(ContactInfo));
    public string ContactTitle => Get(nameof(ContactTitle));
    public string ContactTitlePlaceholder => Get(nameof(ContactTitlePlaceholder));
    public string FamilyName => Get(nameof(FamilyName));
    public string FamilyNamePlaceholder => Get(nameof(FamilyNamePlaceholder));
    public string GivenName => Get(nameof(GivenName));
    public string GivenNamePlaceholder => Get(nameof(GivenNamePlaceholder));
    public string PhoneNumber => Get(nameof(PhoneNumber));
    public string PhonePlaceholder => Get(nameof(PhonePlaceholder));
    public string Address => Get(nameof(Address));
    public string AddressPlaceholder => Get(nameof(AddressPlaceholder));
    public string Note => Get(nameof(Note));
    public string NotePlaceholder => Get(nameof(NotePlaceholder));
    public string Save => Get(nameof(Save));
    public string Delete => Get(nameof(Delete));
    public string Cancel => Get(nameof(Cancel));
    public string ContactNeedsPhone => Get(nameof(ContactNeedsPhone));
    public string ContactDeleted => Get(nameof(ContactDeleted));

    // ---- Tray and notifications -----------------------------------------------------
    public string TrayOpen => Get(nameof(TrayOpen));
    public string TrayTestNotification => Get(nameof(TrayTestNotification));
    public string TrayExit => Get(nameof(TrayExit));
    public string TestNotificationBody => Get(nameof(TestNotificationBody));
    public string UnknownNumber => Get(nameof(UnknownNumber));

    // ---- Dialogs --------------------------------------------------------------------
    public string DeleteMessagesTitle => Get(nameof(DeleteMessagesTitle));
    public string DeleteConversationsTitle => Get(nameof(DeleteConversationsTitle));

    // ---- Formatted ------------------------------------------------------------------
    public string DeleteNMessages(int n) => string.Format(Get("DeleteNMessages"), n);

    public string DeleteNConversations(int n) => string.Format(Get("DeleteNConversations"), n);

    public string ConfirmDeleteMessages(int n) => string.Format(Get("ConfirmDeleteMessages"), n);

    public string ConfirmDeleteConversations(string names) =>
        string.Format(Get("ConfirmDeleteConversations"), names);

    public string AndNMore(int n) => string.Format(Get("AndNMore"), n);

    public string Queued(long id, string to, int segments) =>
        string.Format(Get("Queued"), id, to, segments);

    public string SendFailed(string error) => string.Format(Get("SendFailed"), error);

    public string RetryQueued(long id) => string.Format(Get("RetryQueued"), id);

    public string RetryNotFailed(long id) => string.Format(Get("RetryNotFailed"), id);

    public string RetryFailed(string error) => string.Format(Get("RetryFailed"), error);

    public string LoadListFailed(string error) => string.Format(Get("LoadListFailed"), error);

    public string LoadContactsFailed(string error) => string.Format(Get("LoadContactsFailed"), error);

    public string DeletedMessages(int n) => string.Format(Get("DeletedMessages"), n);

    public string DeletedConversations(int threads, int messages) =>
        string.Format(Get("DeletedConversations"), threads, messages);

    public string DeleteFailed(string error) => string.Format(Get("DeleteFailed"), error);

    public string ContactSaved(string name) => string.Format(Get("ContactSaved"), name);

    public string SaveFailed(string error) => string.Format(Get("SaveFailed"), error);

    public string InvalidPhone(string value) => string.Format(Get("InvalidPhone"), value);

    public string ModemReady(string status, string number, string cellularClass) =>
        string.Format(Get("ModemReady"), status, number, cellularClass);

    public string SendAndReceive(string mode) => string.Format(Get("SendAndReceive"), mode);

    public string SendOnly(string diagnostic) => string.Format(Get("SendOnly"), diagnostic);

    public string ModemUnavailable(string diagnostic) => string.Format(Get("ModemUnavailable"), diagnostic);

    public string ModemStatusFailed(string error) => string.Format(Get("ModemStatusFailed"), error);

    /// <summary>Looks a string up by key, for text chosen at runtime rather than at compile time.</summary>
    public string Text(string key) => Get(key);

    // ---------------------------------------------------------------------------------

    private static readonly Dictionary<string, string> Vietnamese = new()
    {
        [nameof(Messages)] = "Tin nhắn",
        [nameof(Contacts)] = "Danh bạ",
        [nameof(FontSizeTooltip)] = "Cỡ chữ",
        [nameof(LanguageTooltip)] = "Ngôn ngữ",
        [nameof(ShowList)] = "Hiện danh sách",
        [nameof(HideList)] = "Ẩn danh sách",
        [nameof(CheckingModem)] = "Đang kiểm tra modem...",

        [nameof(NewMessage)] = "+ Tin nhắn mới",
        [nameof(SearchPlaceholder)] = "Tìm tên hoặc số...",
        [nameof(SaveToContacts)] = "Lưu vào danh bạ",
        [nameof(SelectConversation)] = "Chọn một hội thoại",
        [nameof(NewMessageTitle)] = "Tin nhắn mới",
        [nameof(EnterRecipient)] = "Nhập số điện thoại người nhận",
        [nameof(RecipientHeader)] = "Số người nhận",
        [nameof(RecipientPlaceholder)] = "+84... hoặc 0...",
        [nameof(MessagePlaceholder)] = "Nhập nội dung tin nhắn...",
        [nameof(Send)] = "Gửi",
        [nameof(Retry)] = "Gửi lại",
        [nameof(ErrorBadge)] = "Lỗi",
        [nameof(PanelSizerTooltip)] = "Kéo để đổi độ rộng danh sách",
        [nameof(ComposerSizerTooltip)] = "Kéo lên để mở rộng ô soạn tin",
        [nameof(NoRecipient)] = "Chưa có số người nhận.",

        [nameof(StatusPending)] = "Đang chờ",
        [nameof(StatusSending)] = "Đang gửi",
        [nameof(StatusSent)] = "Đã gửi",
        [nameof(StatusFailed)] = "Gửi lỗi",

        [nameof(AddContact)] = "+ Thêm liên hệ",
        [nameof(ContactInfo)] = "Thông tin liên hệ",
        [nameof(ContactTitle)] = "Danh xưng",
        [nameof(ContactTitlePlaceholder)] = "Anh / Chị / Ông / Bà / BS. / TS.",
        [nameof(FamilyName)] = "Họ",
        [nameof(FamilyNamePlaceholder)] = "Nguyễn",
        [nameof(GivenName)] = "Tên",
        [nameof(GivenNamePlaceholder)] = "Văn Tú",
        [nameof(PhoneNumber)] = "Số điện thoại",
        [nameof(PhonePlaceholder)] = "+84... hoặc 0...",
        [nameof(Address)] = "Địa chỉ",
        [nameof(AddressPlaceholder)] = "Số nhà, đường, phường, tỉnh/thành...",
        [nameof(Note)] = "Ghi chú",
        [nameof(NotePlaceholder)] = "Ghi chú về người này...",
        [nameof(Save)] = "Lưu",
        [nameof(Delete)] = "Xoá",
        [nameof(Cancel)] = "Huỷ",
        [nameof(ContactNeedsPhone)] = "Cần có số điện thoại.",
        [nameof(ContactDeleted)] = "Đã xoá liên hệ.",

        [nameof(TrayOpen)] = "Mở SUPERDOC SMS Manager",
        [nameof(TrayTestNotification)] = "Gửi thông báo thử",
        [nameof(TrayExit)] = "Thoát",
        [nameof(TestNotificationBody)] = "Thông báo thử - nếu bạn thấy dòng này thì thông báo đang hoạt động.",
        [nameof(UnknownNumber)] = "Số không xác định",

        [nameof(DeleteMessagesTitle)] = "Xoá tin nhắn",
        [nameof(DeleteConversationsTitle)] = "Xoá hội thoại",

        ["DeleteNMessages"] = "Xoá {0} tin",
        ["DeleteNConversations"] = "Xoá {0} hội thoại",
        ["ConfirmDeleteMessages"] = "Xoá vĩnh viễn {0} tin nhắn đã chọn? Thao tác này không thể hoàn tác.",
        ["ConfirmDeleteConversations"] = "Xoá vĩnh viễn toàn bộ tin nhắn với {0}? Thao tác này không thể hoàn tác.",
        ["AndNMore"] = "và {0} hội thoại khác",
        ["Queued"] = "Đã xếp hàng tin #{0} tới {1} ({2} phần)",
        ["SendFailed"] = "Gửi lỗi: {0}",
        ["RetryQueued"] = "Đã gửi lại tin #{0}",
        ["RetryNotFailed"] = "Tin #{0} không ở trạng thái lỗi.",
        ["RetryFailed"] = "Gửi lại lỗi: {0}",
        ["LoadListFailed"] = "Không tải được danh sách: {0}",
        ["LoadContactsFailed"] = "Không tải được danh bạ: {0}",
        ["DeletedMessages"] = "Đã xoá {0} tin nhắn.",
        ["DeletedConversations"] = "Đã xoá {0} hội thoại ({1} tin nhắn).",
        ["DeleteFailed"] = "Xoá lỗi: {0}",
        ["ContactSaved"] = "Đã lưu {0}",
        ["SaveFailed"] = "Lưu lỗi: {0}",
        ["InvalidPhone"] = "'{0}' không phải số hợp lệ.",
        ["ModemReady"] = "Modem {0} - {1} ({2})",
        ["SendAndReceive"] = " - gửi + nhận ({0})",
        ["SendOnly"] = " - chỉ gửi: {0}",
        ["ModemUnavailable"] = "Modem không khả dụng: {0}",
        ["ModemStatusFailed"] = "Không đọc được trạng thái modem: {0}",

        ["FontSmall"] = "Nhỏ",
        ["FontMedium"] = "Vừa",
        ["FontLarge"] = "Lớn",
        ["FontXLarge"] = "Rất lớn",
        ["FontXXLarge"] = "Cực lớn",

        ["LanguageAuto"] = "Tự động",
        ["LanguageVietnamese"] = "Tiếng Việt",
        ["LanguageEnglish"] = "English"
    };

    private static readonly Dictionary<string, string> English = new()
    {
        [nameof(Messages)] = "Messages",
        [nameof(Contacts)] = "Contacts",
        [nameof(FontSizeTooltip)] = "Text size",
        [nameof(LanguageTooltip)] = "Language",
        [nameof(ShowList)] = "Show list",
        [nameof(HideList)] = "Hide list",
        [nameof(CheckingModem)] = "Checking modem...",

        [nameof(NewMessage)] = "+ New message",
        [nameof(SearchPlaceholder)] = "Search name or number...",
        [nameof(SaveToContacts)] = "Save to contacts",
        [nameof(SelectConversation)] = "Select a conversation",
        [nameof(NewMessageTitle)] = "New message",
        [nameof(EnterRecipient)] = "Enter the recipient's number",
        [nameof(RecipientHeader)] = "Recipient",
        [nameof(RecipientPlaceholder)] = "+84... or 0...",
        [nameof(MessagePlaceholder)] = "Type a message...",
        [nameof(Send)] = "Send",
        [nameof(Retry)] = "Retry",
        [nameof(ErrorBadge)] = "Failed",
        [nameof(PanelSizerTooltip)] = "Drag to resize the list",
        [nameof(ComposerSizerTooltip)] = "Drag up to enlarge the composer",
        [nameof(NoRecipient)] = "No recipient number.",

        [nameof(StatusPending)] = "Pending",
        [nameof(StatusSending)] = "Sending",
        [nameof(StatusSent)] = "Sent",
        [nameof(StatusFailed)] = "Failed",

        [nameof(AddContact)] = "+ Add contact",
        [nameof(ContactInfo)] = "Contact details",
        [nameof(ContactTitle)] = "Title",
        [nameof(ContactTitlePlaceholder)] = "Mr / Ms / Dr / Prof.",
        [nameof(FamilyName)] = "Family name",
        [nameof(FamilyNamePlaceholder)] = "Nguyen",
        [nameof(GivenName)] = "Given name",
        [nameof(GivenNamePlaceholder)] = "Van Tu",
        [nameof(PhoneNumber)] = "Phone number",
        [nameof(PhonePlaceholder)] = "+84... or 0...",
        [nameof(Address)] = "Address",
        [nameof(AddressPlaceholder)] = "Street, ward, city...",
        [nameof(Note)] = "Note",
        [nameof(NotePlaceholder)] = "Notes about this person...",
        [nameof(Save)] = "Save",
        [nameof(Delete)] = "Delete",
        [nameof(Cancel)] = "Cancel",
        [nameof(ContactNeedsPhone)] = "A phone number is required.",
        [nameof(ContactDeleted)] = "Contact deleted.",

        [nameof(TrayOpen)] = "Open SUPERDOC SMS Manager",
        [nameof(TrayTestNotification)] = "Send a test notification",
        [nameof(TrayExit)] = "Exit",
        [nameof(TestNotificationBody)] = "Test notification - if you can see this, notifications are working.",
        [nameof(UnknownNumber)] = "Unknown number",

        [nameof(DeleteMessagesTitle)] = "Delete messages",
        [nameof(DeleteConversationsTitle)] = "Delete conversations",

        ["DeleteNMessages"] = "Delete {0} message(s)",
        ["DeleteNConversations"] = "Delete {0} conversation(s)",
        ["ConfirmDeleteMessages"] = "Permanently delete the {0} selected message(s)? This cannot be undone.",
        ["ConfirmDeleteConversations"] = "Permanently delete every message exchanged with {0}? This cannot be undone.",
        ["AndNMore"] = "and {0} more conversation(s)",
        ["Queued"] = "Queued message #{0} to {1} ({2} segment(s))",
        ["SendFailed"] = "Send failed: {0}",
        ["RetryQueued"] = "Retry queued for message #{0}",
        ["RetryNotFailed"] = "Message #{0} is not in a failed state.",
        ["RetryFailed"] = "Retry failed: {0}",
        ["LoadListFailed"] = "Could not load the list: {0}",
        ["LoadContactsFailed"] = "Could not load contacts: {0}",
        ["DeletedMessages"] = "Deleted {0} message(s).",
        ["DeletedConversations"] = "Deleted {0} conversation(s) ({1} message(s)).",
        ["DeleteFailed"] = "Delete failed: {0}",
        ["ContactSaved"] = "Saved {0}",
        ["SaveFailed"] = "Save failed: {0}",
        ["InvalidPhone"] = "'{0}' is not a valid number.",
        ["ModemReady"] = "Modem {0} - {1} ({2})",
        ["SendAndReceive"] = " - send + receive ({0})",
        ["SendOnly"] = " - send only: {0}",
        ["ModemUnavailable"] = "Modem unavailable: {0}",
        ["ModemStatusFailed"] = "Could not read modem status: {0}",

        ["FontSmall"] = "Small",
        ["FontMedium"] = "Medium",
        ["FontLarge"] = "Large",
        ["FontXLarge"] = "Extra large",
        ["FontXXLarge"] = "Huge",

        ["LanguageAuto"] = "Automatic",
        ["LanguageVietnamese"] = "Tiếng Việt",
        ["LanguageEnglish"] = "English"
    };
}
