using Microsoft.Extensions.Logging;
using SuperDoc.Sms.Models;
using SuperDoc.Sms.Storage;
using Windows.Devices.Sms;

namespace SuperDoc.Sms.Services;

/// <summary>
/// Point-in-time view of the WWAN modem's SMS state, for diagnostics and the UI status bar.
/// </summary>
public sealed record SmsDeviceSnapshot
{
    public bool IsAvailable { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public string AccountPhoneNumber { get; init; } = string.Empty;
    public string SmscAddress { get; init; } = string.Empty;
    public string DeviceStatus { get; init; } = "Unavailable";
    public string CellularClass { get; init; } = "Unknown";

    /// <summary>
    /// Whether inbound SMS is being delivered to this app. False still leaves sending working.
    /// </summary>
    public bool CanReceive { get; init; }

    /// <summary>The filter action the receive registration succeeded with, e.g. "Peek".</summary>
    public string ReceiveMode { get; init; } = string.Empty;

    public string Diagnostic { get; init; } = string.Empty;
}

/// <summary>
/// Sends and receives SMS through the built-in WWAN modem using <see cref="SmsDevice2"/>.
/// </summary>
/// <remarks>
/// Voice calling is intentionally absent. The Fibocom L850-GL in this machine reports
/// "Voice class: No voice" over MBIM, so no cellular call API can be satisfied by this
/// hardware; <c>PhoneLine.FromIdAsync</c> fails with 0x8007139F. See README.
/// </remarks>
public sealed class SmsManager : IDisposable
{
    /// <summary>Stable id so repeated runs replace rather than stack up registrations.</summary>
    private const string ReceiveRegistrationId = "MessageT480s.Sms.Receive";

    private readonly SmsRepository _repo;
    private readonly SmsQueueProcessor _processor;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly ILogger<SmsManager>? _logger;

    private SmsDevice2? _device;
    private SmsMessageRegistration? _registration;
    private string _initDiagnostic = "Not initialized yet.";
    private bool _canReceive;
    private string _receiveMode = string.Empty;
    private bool _disposed;

    /// <summary>Often enough that a dropped receive path is noticed in under a minute.</summary>
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(30);

    private readonly Timer _healthTimer;

    /// <summary>Raised after an inbound message has been persisted, so the UI can refresh.</summary>
    public event EventHandler<SmsMessage>? MessageReceived;

    /// <summary>
    /// Raised when the receive path is re-established after having been lost, so the UI can say
    /// so instead of leaving the user believing nothing happened.
    /// </summary>
    public event EventHandler? ReceiveStateChanged;

    public SmsManager(
        SmsRepository repo,
        ILogger<SmsManager>? logger = null,
        ILogger<SmsQueueProcessor>? queueLogger = null)
    {
        _repo = repo;
        _logger = logger;
        _processor = new SmsQueueProcessor(_repo, SendMessageInternalAsync, queueLogger);
        _ = EnsureInitializedAsync();

        _healthTimer = new Timer(OnHealthCheck, null, HealthCheckInterval, HealthCheckInterval);
    }

    /// <summary>Queues a message; the background processor performs the actual send with retries.</summary>
    public long SendSms(string to, string body)
    {
        if (string.IsNullOrWhiteSpace(to))
        {
            throw new ArgumentException("Recipient number cannot be empty.", nameof(to));
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new ArgumentException("Message body cannot be empty.", nameof(body));
        }

        return _processor.Enqueue(new SmsMessage
        {
            To = NormalizeRecipient(to),
            Body = body
        });
    }

    /// <summary>
    /// Strips the punctuation people paste along with a number and rejects anything that is not
    /// dialable. Catching this here costs one immediate error instead of five modem round trips
    /// and a permanently Failed row.
    /// </summary>
    public static string NormalizeRecipient(string to)
    {
        var trimmed = to.Trim();
        var hasPlus = trimmed.StartsWith('+');

        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        if (digits.Length is < 3 or > 20)
        {
            throw new ArgumentException(
                $"'{to}' is not a valid phone number. Use digits, optionally with a leading '+' country code.",
                nameof(to));
        }

        // Anything that is not a digit, separator, or the leading '+' means a typo, not formatting.
        if (trimmed.Skip(hasPlus ? 1 : 0).Any(c => !char.IsDigit(c) && !" -.()".Contains(c)))
        {
            throw new ArgumentException(
                $"'{to}' contains characters that are not valid in a phone number.",
                nameof(to));
        }

        return hasPlus ? "+" + digits : digits;
    }

    public IReadOnlyList<SmsMessage> GetRecentMessages(int max = 100) => _repo.GetRecent(max);

    public IReadOnlyList<SmsMessage> GetMessagesPage(SmsMailboxFilter filter, int page, int pageSize, string? phoneQuery = null)
        => _repo.GetMessagesPage(filter, page, pageSize, phoneQuery);

    public int CountMessages(SmsMailboxFilter filter, string? phoneQuery = null)
        => _repo.CountMessages(filter, phoneQuery);

    public bool RetryFailedMessage(long id) => _repo.RetryFailed(id);

    /// <summary>Threads, newest activity first, with the address book already applied.</summary>
    public IReadOnlyList<Conversation> GetConversations()
    {
        var contacts = _repo.GetContacts().ToDictionary(c => c.PhoneKey);
        var conversations = _repo.GetConversations();

        foreach (var conversation in conversations)
        {
            if (contacts.TryGetValue(conversation.PeerKey, out var contact))
            {
                conversation.Contact = contact;
            }
        }

        return conversations;
    }

    public IReadOnlyList<SmsMessage> GetConversationMessages(string peerKey)
        => _repo.GetConversationMessages(peerKey);

    public int MarkConversationRead(string peerKey) => _repo.MarkConversationRead(peerKey);

    public int DeleteMessages(IEnumerable<long> ids) => _repo.DeleteMessages(ids);

    public int DeleteConversations(IEnumerable<string> peerKeys) => _repo.DeleteConversations(peerKeys);

    public IReadOnlyList<Contact> GetContacts() => _repo.GetContacts();

    public long SaveContact(Contact contact) => _repo.SaveContact(contact);

    public bool DeleteContact(long id) => _repo.DeleteContact(id);

    /// <summary>UI preferences, kept next to the messages so they survive a reinstall.</summary>
    public string GetSetting(string key, string fallback = "") => _repo.GetSetting(key, fallback);

    public void SetSetting(string key, string value) => _repo.SetSetting(key, value);

    /// <summary>Reads live modem state. Safe to call before initialization has finished.</summary>
    public async Task<SmsDeviceSnapshot> GetDeviceSnapshotAsync()
    {
        await EnsureInitializedAsync();

        var device = _device;
        if (device is null)
        {
            return new SmsDeviceSnapshot { Diagnostic = _initDiagnostic };
        }

        try
        {
            return new SmsDeviceSnapshot
            {
                IsAvailable = true,
                DeviceId = device.DeviceId,
                AccountPhoneNumber = device.AccountPhoneNumber ?? string.Empty,
                SmscAddress = device.SmscAddress ?? string.Empty,
                DeviceStatus = device.DeviceStatus.ToString(),
                CellularClass = device.CellularClass.ToString(),
                CanReceive = _canReceive,
                ReceiveMode = _receiveMode,
                Diagnostic = _initDiagnostic
            };
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to read modem snapshot.");
            return new SmsDeviceSnapshot { Diagnostic = $"Modem query failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Takes the country for national-format numbers from the SIM, which knows better than the
    /// Windows region guessed at startup.
    /// </summary>
    /// <remarks>
    /// The service centre address is the reliable signal: it is always stored in international
    /// form and every SIM has one, whereas <c>AccountPhoneNumber</c> is blank on many carriers.
    /// An explicit user choice still wins, so someone with a foreign SIM can pin their country.
    /// </remarks>
    private void AdoptCountryCodeFromSim()
    {
        if (_device is null)
        {
            return;
        }

        if (CallingCodes.IsKnownCode(_repo.GetSetting(SmsRepository.CountryCodeSetting)))
        {
            return;
        }

        try
        {
            var fromSmsc = CallingCodes.FromInternationalNumber(_device.SmscAddress);
            var code = fromSmsc.Length > 0
                ? fromSmsc
                : CallingCodes.FromInternationalNumber(_device.AccountPhoneNumber);

            if (code.Length > 0)
            {
                _repo.ApplyCountryCode(code, "SIM");
            }
        }
        catch (Exception ex)
        {
            // A country code is a convenience, never a reason to fail modem initialisation.
            _logger?.LogWarning(ex, "Could not derive the country code from the SIM.");
        }
    }

    /// <summary>
    /// Estimates how the carrier will bill a message: segment count depends on whether the
    /// body fits GSM-7 (160 chars/segment) or needs UCS-2 (70 chars/segment).
    /// </summary>
    public int EstimateSegmentCount(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return 0;
        }

        var device = _device;
        if (device is not null)
        {
            try
            {
                var probe = new SmsTextMessage2
                {
                    To = device.AccountPhoneNumber ?? "+10000000000",
                    Body = body,
                    Encoding = ChooseEncoding(body)
                };
                return (int)device.CalculateLength(probe).SegmentCount;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "CalculateLength unavailable; falling back to local estimate.");
            }
        }

        var perSegment = NeedsUnicode(body) ? 70 : 160;
        return (body.Length + perSegment - 1) / perSegment;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_device is not null)
        {
            return;
        }

        if (DemoMode.IsEnabled)
        {
            // Opening the modem here would take the receive registration away from the copy the
            // user is actually running.
            _initDiagnostic = "Demo mode: the modem is not opened and nothing can be sent.";
            return;
        }

        await _initGate.WaitAsync();
        try
        {
            if (_device is not null)
            {
                return;
            }

            _device = SmsDevice2.GetDefault();
            if (_device is null)
            {
                _initDiagnostic = "No WWAN SMS device found. Check that the SIM is inserted and mobile broadband is enabled.";
                _logger?.LogWarning("{Diagnostic}", _initDiagnostic);
                return;
            }

            _initDiagnostic = $"Modem ready. Status={_device.DeviceStatus}, number={_device.AccountPhoneNumber}, SMSC={_device.SmscAddress}.";
            _logger?.LogInformation("{Diagnostic}", _initDiagnostic);

            AdoptCountryCodeFromSim();

            // Re-acquiring the modem after a reset must not register a second time - the receive
            // registration outlives the device handle.
            if (_registration is null)
            {
                TryRegisterForIncomingMessages();
            }
        }
        catch (Exception ex)
        {
            _initDiagnostic = $"SMS device initialization failed: {ex.Message}";
            _logger?.LogError(ex, "Failed to initialize SMS device.");
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>
    /// Filter actions to attempt, in order of preference.
    /// </summary>
    /// <remarks>
    /// <see cref="SmsFilterActionType.AcceptImmediately"/> is deliberately absent. It means
    /// "consume this message exclusively, ahead of every other app", which Windows reserves for
    /// the system's default messaging handler; any other caller gets 0xD0000022
    /// (STATUS_ACCESS_DENIED) regardless of capabilities or MSIX packaging.
    /// <see cref="SmsFilterActionType.Peek"/> is preferred because it is non-destructive: this
    /// app sees the message and the built-in messaging app still receives its own copy.
    /// </remarks>
    private static readonly SmsFilterActionType[] ReceiveActionPreference =
    [
        SmsFilterActionType.Peek,
        SmsFilterActionType.Accept
    ];

    /// <summary>
    /// Subscribes to inbound text messages, trying the permitted filter actions in turn.
    /// A total failure is not fatal: sending remains available.
    /// </summary>
    private void TryRegisterForIncomingMessages()
    {
        ReleaseStaleRegistration();

        var failures = new List<string>();

        foreach (var action in ReceiveActionPreference)
        {
            try
            {
                var rules = new SmsFilterRules(action);
                rules.Rules.Add(new SmsFilterRule(SmsMessageType.Text));

                _registration = SmsMessageRegistration.Register(ReceiveRegistrationId, rules);
                _registration.MessageReceived += OnMessageReceived;
                _canReceive = true;
                _receiveMode = action.ToString();
                _initDiagnostic += $" Receiving incoming SMS ({action} mode).";
                _logger?.LogInformation(
                    "Registered for incoming SMS (id={Id}, action={Action}).", _registration.Id, action);
                return;
            }
            catch (Exception ex)
            {
                failures.Add($"{action}=0x{ex.HResult:X8}");
                _logger?.LogDebug(ex, "SMS registration rejected for action {Action}.", action);
            }
        }

        _canReceive = false;
        _initDiagnostic +=
            $" Incoming SMS is unavailable (tried {string.Join(", ", failures)}). Sending still works.";
        _logger?.LogWarning(
            "Could not register for incoming SMS. Attempts: {Failures}. Sending is unaffected.",
            string.Join(", ", failures));
    }

    /// <summary>
    /// Drops a registration this app left behind on a previous run.
    /// </summary>
    /// <remarks>
    /// The registration survives the process that made it. A clean shutdown unregisters it in
    /// <see cref="Dispose"/>, but a crash, a Task Manager kill or a power cut does not - and the
    /// orphan then makes every later run fail with 0xD0000022 (STATUS_ACCESS_DENIED), leaving
    /// the app permanently unable to receive until Windows is restarted. Clearing it here makes
    /// the app recover by itself.
    ///
    /// This only works for an orphan left by the same package identity. One created under a
    /// different identity (an unpackaged build, or a package whose Publisher has since changed)
    /// cannot be released from here and needs a reboot.
    /// </remarks>
    private void ReleaseStaleRegistration()
    {
        try
        {
            foreach (var registration in SmsMessageRegistration.AllRegistrations)
            {
                if (!string.Equals(registration.Id, ReceiveRegistrationId, StringComparison.Ordinal))
                {
                    continue;
                }

                registration.Unregister();
                _logger?.LogWarning(
                    "Released a stale SMS registration (id={Id}) left by a previous run.", registration.Id);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not inspect existing SMS registrations (0x{HResult:X8}).", ex.HResult);
        }
    }

    private void OnMessageReceived(SmsMessageRegistration sender, SmsMessageReceivedTriggerDetails args)
    {
        try
        {
            var text = args.TextMessage;
            if (text is null || string.IsNullOrWhiteSpace(text.Body))
            {
                return;
            }

            var stored = new SmsMessage
            {
                From = text.From ?? string.Empty,
                To = text.To ?? _device?.AccountPhoneNumber ?? string.Empty,
                Body = text.Body,
                CreatedAt = text.Timestamp == default ? DateTimeOffset.UtcNow : text.Timestamp,
                SentAt = null,
                Status = SmsStatus.Received,
                RetryCount = 0,
                ErrorMessage = string.Empty
            };

            stored.Id = _repo.Insert(stored);
            _logger?.LogInformation("Incoming SMS #{Id} saved from={From}", stored.Id, stored.From);
            MessageReceived?.Invoke(this, stored);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to persist incoming SMS.");
        }
    }

    private async Task<SmsSendResult> SendMessageInternalAsync(SmsMessage msg)
    {
        try
        {
            await EnsureInitializedAsync();

            var device = _device;
            if (device is null)
            {
                return SmsSendResult.NotReady(_initDiagnostic);
            }

            if (device.DeviceStatus != SmsDeviceStatus.Ready)
            {
                return SmsSendResult.NotReady($"Modem is not ready to send (status={device.DeviceStatus}).");
            }

            var outgoing = new SmsTextMessage2
            {
                To = msg.To,
                Body = msg.Body,
                Encoding = ChooseEncoding(msg.Body)
            };

            var result = await device.SendMessageAndGetResultAsync(outgoing);
            if (result.IsSuccessful)
            {
                _logger?.LogInformation(
                    "Modem send success for SMS #{Id} to={To} ({Encoding}).",
                    msg.Id, msg.To, outgoing.Encoding);
                return SmsSendResult.Sent();
            }

            var error =
                $"Send rejected: modem={result.ModemErrorCode}, network={result.NetworkCauseCode}, " +
                $"transport={result.TransportFailureCause}, transient={result.IsErrorTransient}";
            _logger?.LogWarning(
                "SMS #{Id} rejected by modem (transient={Transient}). {Error}",
                msg.Id, result.IsErrorTransient, error);

            // The modem already told us whether this is worth trying again; honouring that
            // avoids spending the whole retry budget on a message the network will never accept.
            return result.IsErrorTransient
                ? SmsSendResult.Transient(error)
                : SmsSendResult.Permanent(error);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Modem send failed for SMS #{Id}", msg.Id);

            // The cached SmsDevice2 can outlive the hardware it refers to - a driver reset or an
            // airplane-mode toggle leaves it valid-looking but dead, and every later send throws.
            // Dropping it makes the next attempt re-acquire the modem instead of failing forever.
            ResetDevice();
            return SmsSendResult.Transient($"{ex.Message} (0x{ex.HResult:X8})");
        }
    }

    /// <summary>
    /// Periodically confirms the receive path is still alive and rebuilds it when it is not.
    /// </summary>
    /// <remarks>
    /// A registration does not raise anything when it dies. A driver reset, an airplane-mode
    /// toggle or the modem dropping off the bus leaves the app running and able to send, while
    /// silently receiving nothing - which looks identical to "nobody has texted me". Polling is
    /// the only way to notice: there is no event for it.
    /// </remarks>
    private async void OnHealthCheck(object? state)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var wasReceiving = _canReceive;

            // Touching the device is what surfaces a dead handle: the property throws rather
            // than returning a "gone" status.
            try
            {
                _ = _device?.DeviceStatus;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Modem handle no longer responds; re-acquiring.");
                ResetDevice();
            }

            if (_device is null)
            {
                await EnsureInitializedAsync();
            }

            if (!IsRegistrationAlive())
            {
                _logger?.LogWarning("Incoming SMS registration is gone; re-registering.");

                _registration = null;
                _canReceive = false;
                TryRegisterForIncomingMessages();

                if (_canReceive)
                {
                    _logger?.LogInformation("Incoming SMS registration restored.");
                }
            }

            if (wasReceiving != _canReceive)
            {
                ReceiveStateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Receive health check failed.");
        }
    }

    /// <summary>True when our registration is still present in the system's list.</summary>
    private bool IsRegistrationAlive()
    {
        if (_registration is null)
        {
            return false;
        }

        try
        {
            foreach (var registration in SmsMessageRegistration.AllRegistrations)
            {
                if (string.Equals(registration.Id, ReceiveRegistrationId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            // AllRegistrations is denied in some contexts; assume alive rather than churning
            // the registration on every tick.
            _logger?.LogDebug(ex, "Could not enumerate SMS registrations during health check.");
            return true;
        }
    }

    /// <summary>Forces the next send to re-acquire the modem, keeping any receive registration.</summary>
    private void ResetDevice()
    {
        if (_device is null)
        {
            return;
        }

        _device = null;
        _initDiagnostic = "Modem handle was reset after a failure; it will be re-acquired.";
        _logger?.LogWarning("Dropped the cached modem handle so it can be re-acquired.");
    }

    /// <summary>
    /// Picks the SMS alphabet. Vietnamese and other non-ASCII text needs UCS-2; forcing it
    /// explicitly avoids relying on driver-specific interpretation of <see cref="SmsEncoding.Optimal"/>.
    /// </summary>
    private static SmsEncoding ChooseEncoding(string body)
        => NeedsUnicode(body) ? SmsEncoding.Unicode : SmsEncoding.Optimal;

    private static bool NeedsUnicode(string body)
    {
        foreach (var ch in body)
        {
            if (ch > 127)
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _healthTimer.Dispose();
        _processor.Dispose();

        try
        {
            if (_registration is not null)
            {
                _registration.MessageReceived -= OnMessageReceived;
                _registration.Unregister();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Ignoring failure while unregistering SMS receiver at shutdown.");
        }

        _initGate.Dispose();
    }
}
