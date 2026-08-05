namespace Message_T480s.WinUI;

/// <summary>
/// The emoji offered by the composer's picker.
/// </summary>
/// <remarks>
/// Deliberately a short, curated list rather than the full Unicode set. Every emoji costs
/// UTF-16 code units out of a 70-character segment, so the ones worth one tap are the common
/// ones; anything else is still reachable through the Windows emoji panel (Win + period), which
/// works in any text box.
///
/// Kept to single-code-point emoji where possible. Sequences such as skin-tone variants and
/// flags cost four or more units each, which is a large bite out of a segment.
/// </remarks>
public static class EmojiCatalog
{
    public static IReadOnlyList<EmojiGroup> Groups { get; } =
    [
        new("🙂", "Smileys",
        [
            "😀", "😃", "😄", "😁", "😆", "😅", "🤣", "😂", "🙂", "🙃",
            "😉", "😊", "😇", "🥰", "😍", "😘", "😗", "😚", "😋", "😛",
            "🤪", "😜", "🤗", "🤔", "🤐", "😐", "😑", "😶", "😏", "😒",
            "🙄", "😬", "😌", "😔", "😪", "😴", "😷", "🤒", "🥳", "🥺",
            "😢", "😭", "😤", "😠", "😡", "😳", "😱", "😨", "😰", "😥"
        ]),

        new("👍", "Gestures",
        [
            "👍", "👎", "👌", "✌️", "🤞", "🤟", "🤘", "👏", "🙌", "👐",
            "🤝", "🙏", "💪", "👋", "🤙", "☝️", "👆", "👇", "👈", "👉",
            "✋", "🖐️", "🤚", "👊", "✊", "🫰", "🫡", "🤲", "💅", "👀"
        ]),

        new("❤️", "Hearts",
        [
            "❤️", "🧡", "💛", "💚", "💙", "💜", "🖤", "🤍", "🤎", "💔",
            "❣️", "💕", "💞", "💓", "💗", "💖", "💘", "💝", "💟", "✨"
        ]),

        new("🎉", "Objects",
        [
            "🎉", "🎊", "🎁", "🎂", "🍰", "☕", "🍵", "🍺", "🍻", "🥂",
            "🍕", "🍔", "🍜", "🍚", "🍎", "🍌", "🌸", "🌹", "🌻", "🌴",
            "⭐", "🌟", "🔥", "💧", "☀️", "🌙", "☁️", "🌈", "⚡", "❄️"
        ]),

        new("📱", "Work",
        [
            "📱", "💻", "⌨️", "🖨️", "📞", "☎️", "📧", "📩", "📨", "📬",
            "📅", "📆", "⏰", "⏳", "📌", "📎", "✅", "❌", "⚠️", "❗",
            "❓", "💡", "🔒", "🔑", "🔍", "📊", "📈", "📉", "💰", "🏦",
            "🚗", "✈️", "🏠", "🏢", "🎯", "🔔", "🔕", "♻️", "🆗", "🆕"
        ])
    ];
}

/// <summary>One tab of the emoji picker.</summary>
public sealed record EmojiGroup(string Icon, string Name, IReadOnlyList<string> Emoji);
