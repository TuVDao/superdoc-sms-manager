using Microsoft.Extensions.Logging;
using Windows.Networking.NetworkOperators;

namespace SuperDoc.Sms.Services;

/// <summary>What the carrier said when asked about the account, or why it could not be asked.</summary>
public readonly record struct CarrierBalanceResult(bool Succeeded, string Text, string? Error)
{
    public static CarrierBalanceResult Ok(string text) => new(true, text, null);

    public static CarrierBalanceResult Failed(string error) => new(false, string.Empty, error);
}

/// <summary>
/// Asks the carrier for the account balance over USSD.
/// </summary>
/// <remarks>
/// This exists because of a failure that took a long time to explain. Sending stopped working
/// with nothing but <c>modem=Other, network=0, transport=0, transient=False</c> — the modem was
/// connected, registered and had good signal, and incoming messages kept arriving normally. The
/// cause was an exhausted prepaid balance: receiving costs nothing, so only the sending half
/// stops, and the modem is told "no" without being told why.
///
/// The reply is deliberately **not parsed**. Every carrier words it differently and in its own
/// language, and a parser that guesses wrong about whether the balance is zero is worse than no
/// parser at all. The carrier's own sentence is shown to the user verbatim.
/// </remarks>
public sealed class CarrierBalance(ILogger? logger = null)
{
    private readonly ILogger? _logger = logger;

    /// <summary>Setting holding the USSD code to dial; carrier-specific, so the user can edit it.</summary>
    public const string UssdCodeSetting = "carrier.balanceUssd";

    /// <summary>
    /// A balance-enquiry code for a country and optional subscriber/SMSC number hint, or empty when none is known.
    /// </summary>
    /// <remarks>
    /// Deliberately tiny. A balance code belongs to a carrier, not a country, and dialling a
    /// wrong one is not merely useless — USSD codes do things. Only codes that are the same
    /// across a country's networks and are known to be read-only enquiries belong here;
    /// everything else is left for the user to fill in.
    /// </remarks>
    public static string DefaultCodeForCountry(string? countryCallingCode, string? subscriberOrSmsc = null)
    {
        // First check subscriber or SMSC directly for known Ethiopian carriers
        if (IsSafaricomEthiopia(subscriberOrSmsc))
        {
            return "*704#";
        }

        if (IsEthioTelecom(subscriberOrSmsc))
        {
            return "*804#";
        }

        if (string.Equals(countryCallingCode, "251", StringComparison.Ordinal))
        {
            return IsSafaricomEthiopia(subscriberOrSmsc) ? "*704#" : "*804#";
        }

        return countryCallingCode switch
        {
            // Viettel, MobiFone and VinaPhone all use *101# for a balance enquiry.
            "84" => "*101#",
            _ => string.Empty
        };
    }

    /// <summary>Backward-compatible overload without subscriber hint.</summary>
    public static string DefaultCodeForCountry(string? countryCallingCode)
        => DefaultCodeForCountry(countryCallingCode, null);

    /// <summary>True if the address, phone number or SMSC belongs to Safaricom Ethiopia (07... or +2517...).</summary>
    public static bool IsSafaricomEthiopia(string? numberOrSmsc)
    {
        if (string.IsNullOrWhiteSpace(numberOrSmsc))
        {
            return false;
        }

        var digits = new string(numberOrSmsc.Where(char.IsDigit).ToArray());
        return digits.StartsWith("07", StringComparison.Ordinal)
            || digits.StartsWith("2517", StringComparison.Ordinal)
            || (digits.Length == 9 && digits.StartsWith("7", StringComparison.Ordinal));
    }

    /// <summary>True if the address, phone number or SMSC belongs to Ethio Telecom (09... or +2519...).</summary>
    public static bool IsEthioTelecom(string? numberOrSmsc)
    {
        if (string.IsNullOrWhiteSpace(numberOrSmsc))
        {
            return false;
        }

        var digits = new string(numberOrSmsc.Where(char.IsDigit).ToArray());
        return digits.StartsWith("09", StringComparison.Ordinal)
            || digits.StartsWith("2519", StringComparison.Ordinal)
            || (digits.Length == 9 && digits.StartsWith("9", StringComparison.Ordinal));
    }

    /// <summary>Preset USSD shortcuts for known carriers in the region.</summary>
    public static IReadOnlyList<(string Label, string Code)> GetPresetsForCountry(string? countryCallingCode, string? subscriberOrSmsc = null)
    {
        if (string.Equals(countryCallingCode, "251", StringComparison.Ordinal))
        {
            if (IsSafaricomEthiopia(subscriberOrSmsc))
            {
                return
                [
                    ("Safaricom Balance (*704#)", "*704#"),
                    ("Safaricom Services / Packages (*777#)", "*777#"),
                    ("Ethio Telecom Balance (*804#)", "*804#"),
                    ("Ethio Telecom Telebirr / Packages (*999#)", "*999#")
                ];
            }

            return
            [
                ("Ethio Telecom Balance (*804#)", "*804#"),
                ("Ethio Telecom Telebirr / Packages (*999#)", "*999#"),
                ("Safaricom Balance (*704#)", "*704#"),
                ("Safaricom Services / Packages (*777#)", "*777#")
            ];
        }

        if (string.Equals(countryCallingCode, "84", StringComparison.Ordinal))
        {
            return [("Tra cứu số dư (*101#)", "*101#")];
        }

        return [];
    }

    /// <summary>True when the value looks like a USSD code and not something else entirely.</summary>
    public static bool IsPlausibleCode(string? code)
    {
        var trimmed = code?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 34)
        {
            return false;
        }

        // A USSD string is * or # delimited digits: *101#, *101*1#, #123#. Anything else is a
        // typo, and this is not a field to be relaxed about - it is dialled at the network.
        if (!trimmed.StartsWith('*') && !trimmed.StartsWith('#'))
        {
            return false;
        }

        return trimmed.EndsWith('#') && trimmed.All(c => char.IsDigit(c) || c is '*' or '#');
    }

    /// <summary>
    /// Dials <paramref name="code"/> and returns the carrier's reply.
    /// </summary>
    public async Task<CarrierBalanceResult> QueryAsync(string code)
    {
        if (!IsPlausibleCode(code))
        {
            return CarrierBalanceResult.Failed($"'{code}' is not a USSD code, e.g. *101#.");
        }

        UssdSession? session = null;
        try
        {
            var accounts = MobileBroadbandAccount.AvailableNetworkAccountIds;
            if (accounts.Count == 0)
            {
                return CarrierBalanceResult.Failed("No mobile broadband account is available.");
            }

            session = UssdSession.CreateFromNetworkAccountId(accounts[0]);
            var reply = await session.SendMessageAndGetReplyAsync(new UssdMessage(code.Trim()));

            var text = reply.Message?.PayloadAsText?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                if (reply.ResultCode is UssdResultCode.NoActionRequired or UssdResultCode.ActionRequired)
                {
                    return CarrierBalanceResult.Ok("USSD request sent. The carrier will display your balance or send a summary message via SMS shortly.");
                }

                return CarrierBalanceResult.Failed($"The network replied with no text ({reply.ResultCode}).");
            }

            // The reply carries the subscriber's own number and balance, so it is never logged.
            _logger?.LogInformation(
                "Balance enquiry {Code} returned {Length} characters ({ResultCode}).",
                code, text.Length, reply.ResultCode);

            return CarrierBalanceResult.Ok(text);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Balance enquiry {Code} failed.", code);
            return CarrierBalanceResult.Failed(ex.Message);
        }
        finally
        {
            try
            {
                session?.Close();
            }
            catch (Exception)
            {
                // Closing a session the network already tore down is not worth reporting.
            }
        }
    }
}
