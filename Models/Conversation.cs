using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SuperDoc.Sms.Models;

/// <summary>
/// One thread: every message exchanged with a single peer, inbound and outbound together.
/// </summary>
public sealed class Conversation : INotifyPropertyChanged
{
    private string _lastMessagePreview = string.Empty;
    private DateTimeOffset _lastMessageAt;
    private SmsStatus _lastMessageStatus;
    private bool _lastMessageIsIncoming;
    private int _messageCount;
    private int _failedCount;
    private int _unreadCount;
    private Contact? _contact;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Normalised peer address; the identity of the thread.</summary>
    public required string PeerKey { get; init; }

    /// <summary>The peer as it should be shown when no contact matches.</summary>
    public required string PeerDisplay { get; init; }

    /// <summary>The matching address book entry, when there is one.</summary>
    public Contact? Contact
    {
        get => _contact;
        set
        {
            if (SetField(ref _contact, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(HasContact));
            }
        }
    }

    /// <summary>Contact name when known, otherwise the phone number itself.</summary>
    public string Title => Contact?.DisplayName ?? PeerDisplay;

    /// <summary>The number, shown as a second line only when a name is taking the first.</summary>
    public string Subtitle => Contact is null ? string.Empty : PeerDisplay;

    public bool HasContact => Contact is not null;

    public string LastMessagePreview
    {
        get => _lastMessagePreview;
        set => SetField(ref _lastMessagePreview, value);
    }

    public DateTimeOffset LastMessageAt
    {
        get => _lastMessageAt;
        set
        {
            if (SetField(ref _lastMessageAt, value))
            {
                OnPropertyChanged(nameof(LastMessageAtDisplay));
            }
        }
    }

    public SmsStatus LastMessageStatus
    {
        get => _lastMessageStatus;
        set => SetField(ref _lastMessageStatus, value);
    }

    public bool LastMessageIsIncoming
    {
        get => _lastMessageIsIncoming;
        set => SetField(ref _lastMessageIsIncoming, value);
    }

    public int MessageCount
    {
        get => _messageCount;
        set => SetField(ref _messageCount, value);
    }

    /// <summary>Drives the "something went wrong in here" marker on the thread list.</summary>
    public int FailedCount
    {
        get => _failedCount;
        set
        {
            if (SetField(ref _failedCount, value))
            {
                OnPropertyChanged(nameof(HasFailed));
            }
        }
    }

    public bool HasFailed => FailedCount > 0;

    /// <summary>Inbound messages in this thread the user has not seen yet.</summary>
    public int UnreadCount
    {
        get => _unreadCount;
        set
        {
            if (SetField(ref _unreadCount, value))
            {
                OnPropertyChanged(nameof(HasUnread));
            }
        }
    }

    /// <summary>Drives the bold weight in the thread list.</summary>
    public bool HasUnread => UnreadCount > 0;

    /// <summary>Today shows a clock, this year a date, older years include the year.</summary>
    public string LastMessageAtDisplay
    {
        get
        {
            var local = LastMessageAt.ToLocalTime();
            var now = DateTimeOffset.Now;

            if (local.Date == now.Date)
            {
                return local.ToString("HH:mm");
            }

            return local.Year == now.Year
                ? local.ToString("dd/MM")
                : local.ToString("dd/MM/yyyy");
        }
    }

    public void CopyStateFrom(Conversation other)
    {
        LastMessagePreview = other.LastMessagePreview;
        LastMessageAt = other.LastMessageAt;
        LastMessageStatus = other.LastMessageStatus;
        LastMessageIsIncoming = other.LastMessageIsIncoming;
        MessageCount = other.MessageCount;
        FailedCount = other.FailedCount;
        UnreadCount = other.UnreadCount;
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
