using Microsoft.Extensions.Logging;
using MyApp.Models;
using MyApp.Storage;

namespace MyApp.Services;

public sealed class SmsQueueProcessor : IDisposable
{
    private readonly SmsRepository _repo;
    private readonly Func<SmsMessage, Task<SmsSendResult>> _sendFunc;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _workerTask;
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _notReadyDelay;
    private readonly TimeSpan _maxNotReadyWait;
    private readonly ILogger<SmsQueueProcessor>? _logger;

    public SmsQueueProcessor(
        SmsRepository repo,
        Func<SmsMessage, Task<SmsSendResult>> sendFunc,
        ILogger<SmsQueueProcessor>? logger = null,
        int maxRetries = 5,
        TimeSpan? baseDelay = null,
        TimeSpan? notReadyDelay = null,
        TimeSpan? maxNotReadyWait = null)
    {
        _repo = repo;
        _sendFunc = sendFunc;
        _logger = logger;
        _maxRetries = maxRetries;
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(5);

        // Re-check the radio often while it is coming up, but do not let a message sit in the
        // queue forever if the modem never attaches.
        _notReadyDelay = notReadyDelay ?? TimeSpan.FromSeconds(15);
        _maxNotReadyWait = maxNotReadyWait ?? TimeSpan.FromMinutes(30);

        // A previous run may have been killed between "mark Sending" and the modem result.
        // Those rows are invisible to both the queue and the retry button until requeued.
        _repo.RequeueInterruptedSends();

        _workerTask = Task.Run(ProcessLoopAsync);
    }

    public long Enqueue(SmsMessage msg)
    {
        msg.CreatedAt = DateTimeOffset.UtcNow;
        msg.Status = SmsStatus.Pending;
        msg.RetryCount = 0;
        msg.ErrorMessage = string.Empty;
        msg.NextAttemptAt = null;
        msg.Id = _repo.Insert(msg);
        _logger?.LogInformation("Queued SMS #{Id} to={To}", msg.Id, msg.To);
        return msg.Id;
    }

    private async Task ProcessLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var pending = _repo.GetPending(20);
                if (pending.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), _cts.Token);
                    continue;
                }

                foreach (var msg in pending)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    await ProcessMessageAsync(msg);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Queue loop hit an unexpected error. Retrying.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task ProcessMessageAsync(SmsMessage msg)
    {
        try
        {
            msg.Status = SmsStatus.Sending;
            _repo.Update(msg);

            var result = await _sendFunc(msg);
            switch (result.Outcome)
            {
                case SmsSendOutcome.Sent:
                    msg.Status = SmsStatus.Sent;
                    msg.SentAt = DateTimeOffset.UtcNow;
                    msg.ErrorMessage = string.Empty;
                    msg.NextAttemptAt = null;
                    _repo.Update(msg);
                    _logger?.LogInformation("SMS #{Id} sent successfully.", msg.Id);
                    return;

                case SmsSendOutcome.NotReady:
                    HandleNotReady(msg, result.Error ?? "Modem not ready");
                    return;

                case SmsSendOutcome.PermanentFailure:
                    msg.RetryCount++;
                    msg.Status = SmsStatus.Failed;
                    msg.ErrorMessage = result.Error ?? "Rejected";
                    msg.NextAttemptAt = null;
                    _repo.Update(msg);
                    _logger?.LogError(
                        "SMS #{Id} rejected permanently; not retrying. Error={Error}", msg.Id, msg.ErrorMessage);
                    return;

                default:
                    HandleFailure(msg, result.Error ?? "Unknown send failure");
                    return;
            }
        }
        catch (Exception ex)
        {
            HandleFailure(msg, ex.Message);
        }
    }

    /// <summary>
    /// Puts the message back without spending a retry, because nothing was actually attempted.
    /// A cold-booted modem needs tens of seconds to attach to the network, and the app now
    /// starts with Windows, so this is the normal state of the first message after sign-in.
    /// </summary>
    private void HandleNotReady(SmsMessage msg, string reason)
    {
        var waitingFor = DateTimeOffset.UtcNow - msg.CreatedAt;
        if (waitingFor > _maxNotReadyWait)
        {
            msg.Status = SmsStatus.Failed;
            msg.ErrorMessage = $"Modem never became ready within {_maxNotReadyWait.TotalMinutes:0} minutes. {reason}";
            msg.NextAttemptAt = null;
            _repo.Update(msg);
            _logger?.LogError("SMS #{Id} gave up waiting for the modem. {Reason}", msg.Id, reason);
            return;
        }

        msg.Status = SmsStatus.Pending;
        msg.ErrorMessage = reason;
        msg.NextAttemptAt = DateTimeOffset.UtcNow.Add(_notReadyDelay);
        _repo.Update(msg);
        _logger?.LogInformation(
            "SMS #{Id} is waiting for the modem ({Elapsed:0}s so far, retry budget untouched). {Reason}",
            msg.Id, waitingFor.TotalSeconds, reason);
    }

    /// <summary>
    /// Records the failure and schedules the next attempt by writing <see cref="SmsMessage.NextAttemptAt"/>.
    /// The delay is stored rather than awaited so a single failing message no longer holds up
    /// every other queued message, and so the backoff survives a restart.
    /// </summary>
    private void HandleFailure(SmsMessage msg, string error)
    {
        msg.RetryCount++;
        msg.ErrorMessage = error;

        if (msg.RetryCount > _maxRetries)
        {
            msg.Status = SmsStatus.Failed;
            msg.NextAttemptAt = null;
            _repo.Update(msg);
            _logger?.LogError(
                "SMS #{Id} failed permanently after {Retries} retries. Error={Error}",
                msg.Id, msg.RetryCount, error);
            return;
        }

        var delay = TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * Math.Pow(2, msg.RetryCount - 1));
        msg.Status = SmsStatus.Pending;
        msg.NextAttemptAt = DateTimeOffset.UtcNow.Add(delay);
        _repo.Update(msg);

        _logger?.LogWarning(
            "SMS #{Id} send failed. Retry={Retry} scheduled in {DelayMs}ms. Error={Error}",
            msg.Id, msg.RetryCount, delay.TotalMilliseconds, error);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _workerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // ignore shutdown race
        }

        _cts.Dispose();
    }
}
