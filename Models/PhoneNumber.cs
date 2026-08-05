using System.Text;

namespace MyApp.Models;

/// <summary>
/// Turns the many shapes a peer address arrives in into one stable key, so that messages to and
/// from the same person land in the same conversation.
/// </summary>
/// <remarks>
/// The same Vietnamese subscriber can appear as <c>+84901234567</c> (inbound, international
/// format from the network), <c>0901234567</c> (typed by the user) or <c>901234567</c>. Grouping
/// on the raw string would split one person into three conversations.
///
/// Peers are not always numbers: operators and public services send from alphanumeric sender
/// IDs such as <c>MOBI_KM</c>, <c>CATP_HaNoi</c> or <c>BCD QUOCGIA</c>. Those must survive
/// untouched rather than being stripped down to nothing by digit-only normalisation.
/// </remarks>
public static class PhoneNumber
{
    private const string VietnamCountryCode = "84";

    /// <summary>Characters people paste around a number that carry no meaning.</summary>
    private const string Separators = " -.() ";

    /// <summary>
    /// The canonical form used for grouping and for matching a contact. Never shown to the user.
    /// </summary>
    public static string ToKey(string? raw)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return string.Empty;
        }

        if (IsAlphanumericSender(trimmed))
        {
            // Case- and space-insensitive, so "MOBI_KM" and "mobi_km" are one sender.
            var collapsed = new StringBuilder(trimmed.Length);
            foreach (var ch in trimmed)
            {
                if (!char.IsWhiteSpace(ch))
                {
                    collapsed.Append(char.ToUpperInvariant(ch));
                }
            }

            return collapsed.ToString();
        }

        var digits = OnlyDigits(trimmed);
        if (digits.Length == 0)
        {
            return string.Empty;
        }

        // Short codes (1900xxxx, 9199, 199) are not subscriber numbers and must not be given a
        // country code, or two different services would collide.
        if (digits.Length <= 6)
        {
            return digits;
        }

        var hadPlus = trimmed.StartsWith('+');

        // 00 is the international access prefix; treat it exactly like a leading '+'.
        if (!hadPlus && digits.StartsWith("00", StringComparison.Ordinal))
        {
            digits = digits[2..];
            hadPlus = true;
        }

        if (hadPlus)
        {
            return digits;
        }

        // National format: a single leading zero stands in for the country code.
        if (digits.StartsWith('0'))
        {
            return VietnamCountryCode + digits.TrimStart('0');
        }

        // Already carries the country code.
        if (digits.StartsWith(VietnamCountryCode, StringComparison.Ordinal) && digits.Length >= 10)
        {
            return digits;
        }

        // A bare subscriber number, e.g. 901234567.
        if (digits.Length is 9 or 10)
        {
            return VietnamCountryCode + digits;
        }

        return digits;
    }

    /// <summary>
    /// A tidy form for display when no contact name is known: alphanumeric senders as they came,
    /// numbers in international <c>+84…</c> form.
    /// </summary>
    public static string ToDisplay(string? raw)
    {
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return string.Empty;
        }

        if (IsAlphanumericSender(trimmed))
        {
            return trimmed;
        }

        var key = ToKey(trimmed);
        if (key.Length == 0)
        {
            return trimmed;
        }

        // Short codes read better without a plus.
        return key.Length <= 6 ? key : "+" + key;
    }

    /// <summary>True when the address contains letters, i.e. it is a sender ID and not a number.</summary>
    public static bool IsAlphanumericSender(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsLetter(ch))
            {
                return true;
            }
        }

        return false;
    }

    private static string OnlyDigits(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsDigit(ch))
            {
                sb.Append(ch);
            }
            else if (ch != '+' && !Separators.Contains(ch))
            {
                // Anything else means this is not a plain number after all.
                return string.Empty;
            }
        }

        return sb.ToString();
    }
}
