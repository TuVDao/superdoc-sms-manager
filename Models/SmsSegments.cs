namespace SuperDoc.Sms.Models;

/// <summary>How a message body will be billed once it reaches the network.</summary>
public readonly record struct SmsSegmentInfo(int Characters, int Segments, bool RequiresUnicode);

/// <summary>
/// Works out how many segments a message costs, without touching the modem.
/// </summary>
/// <remarks>
/// Worth showing the user, because the arithmetic is unintuitive: one emoji in an otherwise
/// plain message forces UCS-2, which cuts capacity from 160 characters to 70 and can turn a
/// one-segment message into three. Each segment is billed separately.
///
/// Counting is in UTF-16 code units, which is what UCS-2 SMS actually counts - so an emoji
/// outside the basic plane costs two, and a sequence such as a flag or a skin-tone variant
/// costs more still. That matches what the carrier charges for, rather than what the eye sees.
/// </remarks>
public static class SmsSegments
{
    // A concatenated message spends part of each segment on a user-data header, which is why
    // the per-segment capacity drops once a message no longer fits in one.
    private const int GsmSingle = 160;
    private const int GsmConcatenated = 153;
    private const int UnicodeSingle = 70;
    private const int UnicodeConcatenated = 67;

    public static SmsSegmentInfo Measure(string? body)
    {
        var text = body ?? string.Empty;
        var unicode = RequiresUnicode(text);
        var length = text.Length;

        if (length == 0)
        {
            return new SmsSegmentInfo(0, 0, unicode);
        }

        var single = unicode ? UnicodeSingle : GsmSingle;
        if (length <= single)
        {
            return new SmsSegmentInfo(length, 1, unicode);
        }

        var perSegment = unicode ? UnicodeConcatenated : GsmConcatenated;
        var segments = (length + perSegment - 1) / perSegment;
        return new SmsSegmentInfo(length, segments, unicode);
    }

    /// <summary>
    /// True when the body cannot be sent as GSM-7. Anything above ASCII forces UCS-2 here:
    /// the GSM alphabet does cover a few extras, but assuming it does not is the safe direction -
    /// it never under-reports the cost.
    /// </summary>
    public static bool RequiresUnicode(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        foreach (var ch in body)
        {
            if (ch > 127)
            {
                return true;
            }
        }

        return false;
    }
}
