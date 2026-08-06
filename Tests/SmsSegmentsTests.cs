using SuperDoc.Sms.Models;
using Xunit;

namespace SuperDoc.Sms.Tests;

/// <summary>
/// The composer shows this arithmetic to the user before they send, so an error here
/// under-reports what the carrier will charge.
/// </summary>
public class SmsSegmentsTests
{
    [Fact]
    public void AnEmptyBodyCostsNothing()
    {
        var info = SmsSegments.Measure("");

        Assert.Equal(0, info.Characters);
        Assert.Equal(0, info.Segments);
    }

    // ----- GSM-7: 160 alone, 153 once concatenated ------------------------------------------

    [Theory]
    [InlineData(1, 1)]
    [InlineData(160, 1)]
    [InlineData(161, 2)]
    [InlineData(306, 2)]   // 2 x 153
    [InlineData(307, 3)]
    public void PlainTextUsesTheGsmCapacities(int length, int expectedSegments)
    {
        var info = SmsSegments.Measure(new string('a', length));

        Assert.False(info.RequiresUnicode);
        Assert.Equal(length, info.Characters);
        Assert.Equal(expectedSegments, info.Segments);
    }

    // ----- UCS-2: 70 alone, 67 once concatenated --------------------------------------------

    [Theory]
    [InlineData(1, 1)]
    [InlineData(70, 1)]
    [InlineData(71, 2)]
    [InlineData(134, 2)]   // 2 x 67
    [InlineData(135, 3)]
    public void AccentedTextUsesTheUnicodeCapacities(int length, int expectedSegments)
    {
        var info = SmsSegments.Measure(new string('ế', length));

        Assert.True(info.RequiresUnicode);
        Assert.Equal(expectedSegments, info.Segments);
    }

    [Fact]
    public void OneAccentedCharacterRerateTheWholeMessage()
    {
        // 160 plain characters fit in one segment; the same length with one Vietnamese
        // character costs three. This is exactly the surprise the counter exists to prevent.
        Assert.Equal(1, SmsSegments.Measure(new string('a', 160)).Segments);
        Assert.Equal(3, SmsSegments.Measure(new string('a', 159) + "ế").Segments);
    }

    [Fact]
    public void EmojiAreCountedInUtf16UnitsBecauseThatIsWhatIsBilled()
    {
        // U+1F600 is a surrogate pair: two units on the air, one glyph on screen.
        var info = SmsSegments.Measure("😀");

        Assert.Equal(2, info.Characters);
        Assert.True(info.RequiresUnicode);
        Assert.Equal(1, info.Segments);
    }

    [Fact]
    public void AnEmojiInPlainTextForcesUnicode()
    {
        Assert.False(SmsSegments.RequiresUnicode("Meeting at 9"));
        Assert.True(SmsSegments.RequiresUnicode("Meeting at 9 👍"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plain ascii 123")]
    public void PlainAsciiNeverRequiresUnicode(string? body)
    {
        Assert.False(SmsSegments.RequiresUnicode(body));
    }
}
