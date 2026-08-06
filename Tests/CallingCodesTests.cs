using SuperDoc.Sms.Models;
using Xunit;

namespace SuperDoc.Sms.Tests;

/// <summary>
/// The table decides which country a leading zero belongs to, so an error here is silently
/// inherited by every conversation key.
/// </summary>
public class CallingCodesTests
{
    [Theory]
    [InlineData("VN", "84")]
    [InlineData("DE", "49")]
    [InlineData("US", "1")]
    [InlineData("GB", "44")]
    [InlineData("JP", "81")]
    [InlineData("AU", "61")]
    [InlineData("BR", "55")]
    [InlineData("IN", "91")]
    public void RegionsMapToTheirCallingCode(string region, string expected)
    {
        Assert.Equal(expected, CallingCodes.ForRegion(region));
    }

    [Fact]
    public void RegionLookupIsCaseInsensitive()
    {
        Assert.Equal(CallingCodes.ForRegion("DE"), CallingCodes.ForRegion("de"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ZZ")]
    [InlineData("NOT A REGION")]
    public void UnknownRegionsYieldNothingRatherThanAGuess(string? region)
    {
        Assert.Equal(string.Empty, CallingCodes.ForRegion(region));
    }

    // ----- Reading the country back out of a number ---------------------------------------

    [Theory]
    [InlineData("+84123456", "84")]      // an SMSC-shaped address
    [InlineData("+4915112345678", "49")]
    [InlineData("+12125551234", "1")]
    [InlineData("004915112345678", "49")]
    [InlineData("84123456", "84")]
    public void TheCountryIsRecoveredFromAnInternationalNumber(string number, string expected)
    {
        Assert.Equal(expected, CallingCodes.FromInternationalNumber(number));
    }

    [Fact]
    public void TheLongestMatchingCodeWins()
    {
        // Andorra is 376. Stopping at a shorter prefix would file it under the wrong country.
        Assert.Equal("376", CallingCodes.FromInternationalNumber("+376123456"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0151234567")]  // national format carries no country
    public void NumbersWithoutACountryYieldNothing(string? number)
    {
        Assert.Equal(string.Empty, CallingCodes.FromInternationalNumber(number));
    }

    [Fact]
    public void CodesProducedByTheTableAreAcceptedBackByIt()
    {
        foreach (var region in new[] { "VN", "DE", "US", "GB", "JP", "AD", "KZ", "RU" })
        {
            var code = CallingCodes.ForRegion(region);
            Assert.True(CallingCodes.IsKnownCode(code), $"{region} produced an unusable code: {code}");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("9999")]
    public void ImplausibleCodesAreNotAccepted(string code)
    {
        Assert.False(CallingCodes.IsKnownCode(code));
    }
}
