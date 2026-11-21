using Microsoft.Extensions.Logging;
using Windows.Networking.NetworkOperators;

namespace SuperDoc.Sms.Services;

/// <summary>Result of one turn in an interactive USSD session.</summary>
public sealed record UssdTurnResult(string Text, bool ExpectsReply, bool Success, string? Error);

/// <summary>
/// Manages a multi-turn interactive USSD session (e.g. for package purchasing *777# or *999#,
/// airtime recharge, or carrier service menus).
/// </summary>
public sealed class UssdInteractiveSession : IDisposable
{
    private readonly ILogger? _logger;
    private UssdSession? _session;
    private string? _currentNetworkAccountId;

    public UssdInteractiveSession(ILogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>True while an active network USSD session is open waiting for user response.</summary>
    public bool IsActive => _session is not null;

    /// <summary>
    /// Sends a USSD string to initiate or continue a multi-turn session.
    /// </summary>
    public async Task<UssdTurnResult> SendAsync(string input)
    {
        var trimmed = input?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(trimmed))
        {
            return new UssdTurnResult(string.Empty, false, false, "Input cannot be empty.");
        }

        try
        {
            if (_session is null)
            {
                var accounts = MobileBroadbandAccount.AvailableNetworkAccountIds;
                if (accounts.Count == 0)
                {
                    return new UssdTurnResult(string.Empty, false, false, "No mobile broadband account is available.");
                }

                _currentNetworkAccountId = accounts[0];
                _session = UssdSession.CreateFromNetworkAccountId(_currentNetworkAccountId);
            }

            var reply = await _session.SendMessageAndGetReplyAsync(new UssdMessage(trimmed));
            var text = reply.Message?.PayloadAsText?.Trim() ?? string.Empty;

            // If another local client had the modem locked, wait briefly and retry once
            if (reply.ResultCode == UssdResultCode.OtherLocalClient && text.Length == 0)
            {
                _logger?.LogInformation("USSD turn returned OtherLocalClient; resetting session and retrying in 600ms...");
                Close();
                await Task.Delay(600);

                try
                {
                    _session = UssdSession.CreateFromNetworkAccountId(_currentNetworkAccountId);
                    reply = await _session.SendMessageAndGetReplyAsync(new UssdMessage(trimmed));
                    text = reply.Message?.PayloadAsText?.Trim() ?? string.Empty;
                    _logger?.LogInformation("USSD retry returned {ResultCode}, {Length} chars.", reply.ResultCode, text.Length);
                }
                catch (Exception retryEx)
                {
                    _logger?.LogWarning(retryEx, "USSD retry attempt failed.");
                }
            }

            _logger?.LogInformation(
                "USSD turn '{Input}' returned code {ResultCode}, {Length} chars.",
                trimmed, reply.ResultCode, text.Length);

            if (reply.ResultCode == UssdResultCode.ActionRequired)
            {
                // Network expects a follow-up answer (e.g. menu option 1, 2, 3...)
                return new UssdTurnResult(text, true, true, null);
            }

            // Session completed by network (NoActionRequired or other terminal code)
            Close();

            if (text.Length == 0)
            {
                if (reply.ResultCode is UssdResultCode.NoActionRequired or UssdResultCode.OtherLocalClient)
                {
                    text = $"USSD request sent ({trimmed}). The carrier processed the request and will deliver your balance or package details via SMS shortly.";
                }
                else
                {
                    return new UssdTurnResult(string.Empty, false, false, $"The network session ended with no response ({reply.ResultCode}).");
                }
            }

            return new UssdTurnResult(text, false, true, null);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "USSD turn '{Input}' failed.", trimmed);
            Close();
            return new UssdTurnResult(string.Empty, false, false, ex.Message);
        }
    }

    /// <summary>Closes any active USSD session.</summary>
    public void Close()
    {
        try
        {
            _session?.Close();
        }
        catch (Exception)
        {
            // Ignore teardown errors on already closed sessions
        }
        finally
        {
            _session = null;
        }
    }

    public void Dispose() => Close();
}
