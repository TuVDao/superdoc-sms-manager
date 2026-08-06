using System.Text;

namespace SuperDoc.Sms.Models;

/// <summary>
/// Turns the many shapes a peer address arrives in into one stable key, so that messages to and
/// from the same person land in the same conversation.
/// </summary>
/// <remarks>
/// The same subscriber can appear as <c>+49151234567</c> (inbound, international format from the
/// network), <c>0151234567</c> (typed by the user) or <c>151234567</c>. Grouping on the raw string
/// would split one person into three conversations.
///
/// Reading a leading zero as "my country" only works once the country is known, and it differs per
/// SIM. <see cref="DefaultCountryCode"/> therefore has to be supplied by the host — see
/// <c>SmsManager</c>, which resolves it from the SIM, then Windows' region, then a stored override.
/// Until it is set, national-format numbers are left exactly as typed rather than being assigned
/// to a guessed country: an ungrouped conversation is recoverable, a wrongly grouped one is not.
///
/// Peers are not always numbers: operators and public services send from alphanumeric sender
/// IDs such as <c>MOBI_KM</c>, <c>CATP_HaNoi</c> or <c>BCD QUOCGIA</c>. Those must survive
/// untouched rather than being stripped down to nothing by digit-only normalisation.
/// </remarks>
public static class PhoneNumber
{
    /// <summary>Characters people paste around a number that carry no meaning.</summary>
    private const string Separators = " -.() ";

    /// <summary>Shorter than this is a service short code, not a subscriber number.</summary>
    private const int ShortCodeMaxLength = 6;

    /// <summary>The range of lengths a bare national number plausibly falls in.</summary>
    private const int NationalMinLength = 7;
    private const int NationalMaxLength = 11;

    /// <summary>
    /// The E.164 calling code (no leading '+') that bare and leading-zero numbers belong to.
    /// Empty means "unknown", in which case such numbers are left as typed.
    /// </summary>
    public static string DefaultCountryCode { get; private set; } = string.Empty;

    /// <summary>
    /// Sets the country used to expand national-format numbers.
    /// </summary>
    /// <returns>
    /// True when the value actually changed. Every <see cref="ToKey"/> result that was derived
    /// from a national-format number changes with it, so a caller that has persisted keys must
    /// recompute them when this returns true.
    /// </returns>
    public static bool SetDefaultCountryCode(string? code)
    {
        var digits = OnlyDigits(code?.Trim() ?? string.Empty);
        var resolved = CallingCodes.IsKnownCode(digits) ? digits : string.Empty;

        if (string.Equals(resolved, DefaultCountryCode, StringComparison.Ordinal))
        {
            return false;
        }

        DefaultCountryCode = resolved;
        return true;
    }

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
        if (digits.Length <= ShortCodeMaxLength)
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

        // Explicitly international: the number already says which country it belongs to.
        if (hadPlus)
        {
            return digits;
        }

        var countryCode = DefaultCountryCode;
        if (countryCode.Length == 0)
        {
            // No country known. Leaving the digits alone keeps identical spellings together and
            // avoids inventing a country the subscriber does not belong to.
            return digits;
        }

        // National format: a single leading zero stands in for the country code.
        if (digits.StartsWith('0'))
        {
            return countryCode + digits.TrimStart('0');
        }

        // Already carries the country code, with enough digits left over to be a real number.
        if (digits.StartsWith(countryCode, StringComparison.Ordinal) &&
            digits.Length - countryCode.Length >= ShortCodeMaxLength)
        {
            return digits;
        }

        // A bare national number, e.g. 901234567 in Vietnam or 2125551234 in the US.
        if (digits.Length is >= NationalMinLength and <= NationalMaxLength)
        {
            return countryCode + digits;
        }

        return digits;
    }

    /// <summary>
    /// A tidy form for display when no contact name is known: alphanumeric senders as they came,
    /// numbers in international <c>+…</c> form once the country is known.
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
        if (key.Length <= ShortCodeMaxLength)
        {
            return key;
        }

        // Only claim international form when the leading digits really are a calling code;
        // otherwise the number is national and a '+' would misrepresent it.
        return CallingCodes.FromInternationalNumber(key).Length > 0 ? "+" + key : key;
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
