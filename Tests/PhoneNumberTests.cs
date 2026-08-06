using SuperDoc.Sms.Models;
using Xunit;

namespace SuperDoc.Sms.Tests;

/// <summary>
/// Covers conversation grouping, which is the one place where a normalisation mistake is not
/// cosmetic: two spellings of the same number that produce different keys split a person's
/// history into two threads that never merge again.
/// </summary>
public class PhoneNumberTests
{
    // ----- The bug that made this file necessary ----------------------------------------

    [Theory]
    [InlineData("49", "0151234567", "49151234567")]   // Germany
    [InlineData("44", "07911123456", "447911123456")] // United Kingdom
    [InlineData("81", "09012345678", "819012345678")] // Japan
    [InlineData("84", "0901234567", "84901234567")]   // Vietnam
    public void LeadingZeroExpandsToTheConfiguredCountry(string country, string typed, string expected)
    {
        using var _ = new CountryScope(country);

        Assert.Equal(expected, PhoneNumber.ToKey(typed));
    }

    [Fact]
    public void NationalNumberIsNotAssignedToAForeignCountry()
    {
        // The original code hard-coded Vietnam, so a German number keyed as 84151234567 and
        // never matched the +49151234567 the network delivered.
        using var _ = new CountryScope("49");

        Assert.Equal(PhoneNumber.ToKey("+49151234567"), PhoneNumber.ToKey("0151234567"));
    }

    [Theory]
    [InlineData("49", "0151234567", "+49151234567", "49 151 234567", "004915 1234567")]
    [InlineData("84", "0901234567", "+84901234567", "901234567", "0084901234567")]
    [InlineData("1", "2125551234", "+1 (212) 555-1234", "12125551234", "0012125551234")]
    public void EverySpellingOfOneNumberSharesAKey(string country, params string[] spellings)
    {
        using var _ = new CountryScope(country);

        var keys = spellings.Select(PhoneNumber.ToKey).Distinct().ToList();

        Assert.True(
            keys.Count == 1,
            $"Expected one key for {string.Join(" / ", spellings)} but got: {string.Join(", ", keys)}");
    }

    // ----- Refusing to guess -------------------------------------------------------------

    [Fact]
    public void WithNoCountryKnownNationalNumbersAreLeftAlone()
    {
        using var _ = new CountryScope(null);

        // Not grouped with +49151234567, but not filed under a country it does not belong to
        // either. An ungrouped conversation can still be merged later; a wrong one cannot.
        Assert.Equal("0151234567", PhoneNumber.ToKey("0151234567"));
    }

    [Fact]
    public void AnUnknownCountryCodeIsRejectedRatherThanStored()
    {
        using var _ = new CountryScope("9999");

        Assert.Equal(string.Empty, PhoneNumber.DefaultCountryCode);
    }

    [Fact]
    public void InternationalNumbersDoNotDependOnTheConfiguredCountry()
    {
        using (new CountryScope("84"))
        {
            Assert.Equal("49151234567", PhoneNumber.ToKey("+49151234567"));
        }

        using (new CountryScope("1"))
        {
            Assert.Equal("49151234567", PhoneNumber.ToKey("+49151234567"));
        }
    }

    // ----- Senders that are not numbers ---------------------------------------------------

    [Theory]
    [InlineData("MOBI_KM")]
    [InlineData("CATP_HaNoi")]
    [InlineData("BCD QUOCGIA")]
    public void AlphanumericSendersSurviveNormalisation(string sender)
    {
        using var _ = new CountryScope("84");

        Assert.NotEqual(string.Empty, PhoneNumber.ToKey(sender));
        Assert.Equal(sender, PhoneNumber.ToDisplay(sender));
    }

    [Fact]
    public void AlphanumericSendersAreMatchedRegardlessOfCaseAndSpacing()
    {
        Assert.Equal(PhoneNumber.ToKey("MOBI_KM"), PhoneNumber.ToKey("mobi_km"));
        Assert.Equal(PhoneNumber.ToKey("BCD QUOCGIA"), PhoneNumber.ToKey("BCDQUOCGIA"));
    }

    // ----- Short codes ---------------------------------------------------------------------

    [Theory]
    [InlineData("9199")]
    [InlineData("199")]
    [InlineData("191")]
    public void ShortCodesNeverReceiveACountryCode(string code)
    {
        using var _ = new CountryScope("84");

        // Giving 9199 a country code would merge two unrelated services in different countries.
        Assert.Equal(code, PhoneNumber.ToKey(code));
        Assert.Equal(code, PhoneNumber.ToDisplay(code));
    }

    // ----- Display -------------------------------------------------------------------------

    [Fact]
    public void DisplayUsesInternationalFormOnceTheCountryIsKnown()
    {
        using var _ = new CountryScope("49");

        Assert.Equal("+49151234567", PhoneNumber.ToDisplay("0151234567"));
    }

    [Fact]
    public void DisplayDoesNotClaimInternationalFormItCannotJustify()
    {
        using var _ = new CountryScope(null);

        // A leading '+' here would assert a country the app has not established.
        Assert.Equal("0151234567", PhoneNumber.ToDisplay("0151234567"));
    }

    // ----- Input that is not a number at all -----------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInputProducesAnEmptyKey(string? raw)
    {
        Assert.Equal(string.Empty, PhoneNumber.ToKey(raw));
    }

    [Fact]
    public void SeparatorsPeoplePasteAreIgnored()
    {
        using var _ = new CountryScope("84");

        Assert.Equal(PhoneNumber.ToKey("0901234567"), PhoneNumber.ToKey(" (090) 123-4567 "));
    }
}
