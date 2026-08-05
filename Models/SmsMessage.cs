using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MyApp.Models;

public enum SmsStatus
{
    Pending,
    Sending,
    Sent,
    Failed,

    /// <summary>
    /// An inbound message from the modem. Appended last so existing rows keep their stored values.
    /// </summary>
    Received
}

/// <summary>
/// A single SMS row. Raises <see cref="PropertyChanged"/> so the UI can refresh a message in
/// place as it moves Pending -> Sending -> Sent, instead of the list being rebuilt underneath
/// the user (which resets scroll position and selection).
/// </summary>
public class SmsMessage : INotifyPropertyChanged
{
    private string _to = string.Empty;
    private string _from = string.Empty;
    private string _body = string.Empty;
    private DateTimeOffset _createdAt;
    private DateTimeOffset? _sentAt;
    private SmsStatus _status;
    private int _retryCount;
    private string _errorMessage = string.Empty;
    private DateTimeOffset? _nextAttemptAt;

    public event PropertyChangedEventHandler? PropertyChanged;

    public long Id { get; set; }

    public string To
    {
        get => _to;
        set => SetField(ref _to, value);
    }

    public string From
    {
        get => _from;
        set => SetField(ref _from, value);
    }

    public string Body
    {
        get => _body;
        set => SetField(ref _body, value);
    }

    public DateTimeOffset CreatedAt
    {
        get => _createdAt;
        set => SetField(ref _createdAt, value);
    }

    public DateTimeOffset? SentAt
    {
        get => _sentAt;
        set
        {
            if (SetField(ref _sentAt, value))
            {
                OnPropertyChanged(nameof(TimestampDisplay));
            }
        }
    }

    public SmsStatus Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public int RetryCount
    {
        get => _retryCount;
        set => SetField(ref _retryCount, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetField(ref _errorMessage, value);
    }

    /// <summary>
    /// Earliest time the queue may try this message again. Persisting the backoff means a
    /// failing message does not restart its retry schedule every time the app restarts, and
    /// does not have to be held in memory to be delayed.
    /// </summary>
    public DateTimeOffset? NextAttemptAt
    {
        get => _nextAttemptAt;
        set => SetField(ref _nextAttemptAt, value);
    }

    /// <summary>True for a message that arrived from the network rather than one we sent.</summary>
    public bool IsIncoming => !string.IsNullOrEmpty(From);

    /// <summary>The other party, whichever direction the message went.</summary>
    public string PeerAddress => IsIncoming ? From : To;

    /// <summary>
    /// Normalised <see cref="PeerAddress"/>, persisted so conversations can be grouped by a
    /// single indexed column instead of normalising every row on every query.
    /// </summary>
    public string PeerKey { get; set; } = string.Empty;

    /// <summary>Timestamp shown inside the conversation bubble.</summary>
    public string TimestampDisplay
    {
        get
        {
            var local = (SentAt ?? CreatedAt).ToLocalTime();
            return local.Date == DateTimeOffset.Now.Date
                ? local.ToString("HH:mm")
                : local.ToString("dd/MM/yyyy HH:mm");
        }
    }

    /// <summary>Copies server-side state onto an existing instance the UI is already bound to.</summary>
    public void CopyStateFrom(SmsMessage other)
    {
        To = other.To;
        From = other.From;
        Body = other.Body;
        CreatedAt = other.CreatedAt;
        SentAt = other.SentAt;
        Status = other.Status;
        RetryCount = other.RetryCount;
        ErrorMessage = other.ErrorMessage;
        NextAttemptAt = other.NextAttemptAt;
        PeerKey = other.PeerKey;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
