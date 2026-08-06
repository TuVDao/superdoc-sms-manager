using System.Globalization;

namespace SuperDoc.Sms.Models;

/// <summary>
/// Maps between ISO 3166-1 alpha-2 regions and E.164 country calling codes, and recovers the
/// calling code from a number that is already in international form.
/// </summary>
/// <remarks>
/// This exists so that <see cref="PhoneNumber"/> does not have to assume a country. A number
/// typed as <c>0151…</c> means something different in every country, and guessing wrong splits
/// or merges conversations for every user outside the country that was guessed.
///
/// The table is deliberately plain data. It is not a substitute for libphonenumber: it knows
/// which digits introduce a country, not how long a national number should be or which prefixes
/// a carrier has been allocated.
/// </remarks>
public static class CallingCodes
{
    /// <summary>ISO 3166-1 alpha-2 region to E.164 calling code, without the leading '+'.</summary>
    private static readonly Dictionary<string, string> ByRegion = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AD"] = "376", ["AE"] = "971", ["AF"] = "93", ["AG"] = "1", ["AI"] = "1",
        ["AL"] = "355", ["AM"] = "374", ["AO"] = "244", ["AR"] = "54", ["AS"] = "1",
        ["AT"] = "43", ["AU"] = "61", ["AW"] = "297", ["AX"] = "358", ["AZ"] = "994",
        ["BA"] = "387", ["BB"] = "1", ["BD"] = "880", ["BE"] = "32", ["BF"] = "226",
        ["BG"] = "359", ["BH"] = "973", ["BI"] = "257", ["BJ"] = "229", ["BL"] = "590",
        ["BM"] = "1", ["BN"] = "673", ["BO"] = "591", ["BQ"] = "599", ["BR"] = "55",
        ["BS"] = "1", ["BT"] = "975", ["BW"] = "267", ["BY"] = "375", ["BZ"] = "501",
        ["CA"] = "1", ["CD"] = "243", ["CF"] = "236", ["CG"] = "242", ["CH"] = "41",
        ["CI"] = "225", ["CK"] = "682", ["CL"] = "56", ["CM"] = "237", ["CN"] = "86",
        ["CO"] = "57", ["CR"] = "506", ["CU"] = "53", ["CV"] = "238", ["CW"] = "599",
        ["CY"] = "357", ["CZ"] = "420",
        ["DE"] = "49", ["DJ"] = "253", ["DK"] = "45", ["DM"] = "1", ["DO"] = "1",
        ["DZ"] = "213",
        ["EC"] = "593", ["EE"] = "372", ["EG"] = "20", ["ER"] = "291", ["ES"] = "34",
        ["ET"] = "251",
        ["FI"] = "358", ["FJ"] = "679", ["FK"] = "500", ["FM"] = "691", ["FO"] = "298",
        ["FR"] = "33",
        ["GA"] = "241", ["GB"] = "44", ["GD"] = "1", ["GE"] = "995", ["GF"] = "594",
        ["GG"] = "44", ["GH"] = "233", ["GI"] = "350", ["GL"] = "299", ["GM"] = "220",
        ["GN"] = "224", ["GP"] = "590", ["GQ"] = "240", ["GR"] = "30", ["GT"] = "502",
        ["GU"] = "1", ["GW"] = "245", ["GY"] = "592",
        ["HK"] = "852", ["HN"] = "504", ["HR"] = "385", ["HT"] = "509", ["HU"] = "36",
        ["ID"] = "62", ["IE"] = "353", ["IL"] = "972", ["IM"] = "44", ["IN"] = "91",
        ["IO"] = "246", ["IQ"] = "964", ["IR"] = "98", ["IS"] = "354", ["IT"] = "39",
        ["JE"] = "44", ["JM"] = "1", ["JO"] = "962", ["JP"] = "81",
        ["KE"] = "254", ["KG"] = "996", ["KH"] = "855", ["KI"] = "686", ["KM"] = "269",
        ["KN"] = "1", ["KP"] = "850", ["KR"] = "82", ["KW"] = "965", ["KY"] = "1",
        ["KZ"] = "7",
        ["LA"] = "856", ["LB"] = "961", ["LC"] = "1", ["LI"] = "423", ["LK"] = "94",
        ["LR"] = "231", ["LS"] = "266", ["LT"] = "370", ["LU"] = "352", ["LV"] = "371",
        ["LY"] = "218",
        ["MA"] = "212", ["MC"] = "377", ["MD"] = "373", ["ME"] = "382", ["MF"] = "590",
        ["MG"] = "261", ["MH"] = "692", ["MK"] = "389", ["ML"] = "223", ["MM"] = "95",
        ["MN"] = "976", ["MO"] = "853", ["MP"] = "1", ["MQ"] = "596", ["MR"] = "222",
        ["MS"] = "1", ["MT"] = "356", ["MU"] = "230", ["MV"] = "960", ["MW"] = "265",
        ["MX"] = "52", ["MY"] = "60", ["MZ"] = "258",
        ["NA"] = "264", ["NC"] = "687", ["NE"] = "227", ["NF"] = "672", ["NG"] = "234",
        ["NI"] = "505", ["NL"] = "31", ["NO"] = "47", ["NP"] = "977", ["NR"] = "674",
        ["NU"] = "683", ["NZ"] = "64",
        ["OM"] = "968",
        ["PA"] = "507", ["PE"] = "51", ["PF"] = "689", ["PG"] = "675", ["PH"] = "63",
        ["PK"] = "92", ["PL"] = "48", ["PM"] = "508", ["PR"] = "1", ["PS"] = "970",
        ["PT"] = "351", ["PW"] = "680", ["PY"] = "595",
        ["QA"] = "974",
        ["RE"] = "262", ["RO"] = "40", ["RS"] = "381", ["RU"] = "7", ["RW"] = "250",
        ["SA"] = "966", ["SB"] = "677", ["SC"] = "248", ["SD"] = "249", ["SE"] = "46",
        ["SG"] = "65", ["SH"] = "290", ["SI"] = "386", ["SJ"] = "47", ["SK"] = "421",
        ["SL"] = "232", ["SM"] = "378", ["SN"] = "221", ["SO"] = "252", ["SR"] = "597",
        ["SS"] = "211", ["ST"] = "239", ["SV"] = "503", ["SX"] = "1", ["SY"] = "963",
        ["SZ"] = "268",
        ["TC"] = "1", ["TD"] = "235", ["TG"] = "228", ["TH"] = "66", ["TJ"] = "992",
        ["TK"] = "690", ["TL"] = "670", ["TM"] = "993", ["TN"] = "216", ["TO"] = "676",
        ["TR"] = "90", ["TT"] = "1", ["TV"] = "688", ["TW"] = "886", ["TZ"] = "255",
        ["UA"] = "380", ["UG"] = "256", ["US"] = "1", ["UY"] = "598", ["UZ"] = "998",
        ["VA"] = "39", ["VC"] = "1", ["VE"] = "58", ["VG"] = "1", ["VI"] = "1",
        ["VN"] = "84", ["VU"] = "678",
        ["WF"] = "681", ["WS"] = "685",
        ["YE"] = "967", ["YT"] = "262",
        ["ZA"] = "27", ["ZM"] = "260", ["ZW"] = "263",
    };

    /// <summary>Every distinct calling code, for longest-prefix matching.</summary>
    private static readonly HashSet<string> KnownCodes = [.. ByRegion.Values];

    /// <summary>The longest calling code in the table, which bounds the prefix search.</summary>
    private const int LongestCode = 3;

    /// <summary>
    /// The calling code for an ISO 3166-1 alpha-2 region, or empty when the region is unknown.
    /// </summary>
    public static string ForRegion(string? isoRegion)
    {
        if (string.IsNullOrWhiteSpace(isoRegion))
        {
            return string.Empty;
        }

        return ByRegion.TryGetValue(isoRegion.Trim(), out var code) ? code : string.Empty;
    }

    /// <summary>
    /// The calling code Windows' current region implies, or empty when it cannot be determined.
    /// </summary>
    public static string ForCurrentRegion()
    {
        try
        {
            return ForRegion(RegionInfo.CurrentRegion.TwoLetterISORegionName);
        }
        catch (Exception)
        {
            // RegionInfo throws on an invariant culture, which is what a service account gets.
            return string.Empty;
        }
    }

    /// <summary>
    /// Recovers the calling code from a number already in international form (<c>+84…</c>,
    /// <c>0084…</c> or bare <c>84…</c> digits). Returns empty when no code matches.
    /// </summary>
    /// <remarks>
    /// The longest match wins so that <c>+376…</c> reads as Andorra rather than stopping at a
    /// shorter code. Codes are prefix-distinct in practice, so one pass is enough.
    /// </remarks>
    public static string FromInternationalNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var digits = Digits(raw);

        // Strip the international access prefix if the caller passed 00 rather than +.
        if (digits.StartsWith("00", StringComparison.Ordinal))
        {
            digits = digits[2..];
        }

        for (var length = Math.Min(LongestCode, digits.Length); length >= 1; length--)
        {
            var candidate = digits[..length];
            if (KnownCodes.Contains(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>True when the value is a plausible calling code, i.e. one this table knows.</summary>
    public static bool IsKnownCode(string? code) =>
        !string.IsNullOrEmpty(code) && KnownCodes.Contains(code);

    private static string Digits(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;
        foreach (var ch in value)
        {
            if (char.IsDigit(ch))
            {
                buffer[length++] = ch;
            }
        }

        return new string(buffer[..length]);
    }
}
