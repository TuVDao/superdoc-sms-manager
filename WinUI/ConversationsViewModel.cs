using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using SuperDoc.Sms.Models;
using SuperDoc.Sms.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace SuperDoc.Sms.WinUI;

/// <summary>
/// The message side of the app: a list of threads on the left, the selected thread on the right.
/// </summary>
public sealed class ConversationsViewModel : INotifyPropertyChanged
{
    private readonly SmsManager _smsManager;
    private readonly ILogger _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly Strings _loc;

    private Conversation? _selectedConversation;
    private string _newRecipient = string.Empty;
    private string _messageBody = string.Empty;
    private string _searchText = string.Empty;
    private string _statusText = string.Empty;
    private bool _isComposingNew;
    private int _refreshInFlight;

    public ConversationsViewModel(SmsManager smsManager, ILogger logger, DispatcherQueue dispatcher, Strings loc)
    {
        _smsManager = smsManager;
        _logger = logger;
        _dispatcher = dispatcher;
        _loc = loc;

        SendCommand = new AsyncCommand(SendAsync, CanSend);
        NewConversationCommand = new DelegateCommand(StartNewConversation);
        CancelNewConversationCommand = new DelegateCommand(CancelNewConversation);
        RetryCommand = new AsyncCommand<long>(RetryAsync);
        DeleteSelectedMessagesCommand = new AsyncCommand(DeleteSelectedMessagesAsync, () => SelectedMessageCount > 0);
        DeleteSelectedConversationsCommand =
            new AsyncCommand(DeleteSelectedConversationsAsync, () => SelectedConversationCount > 0);
    }

    /// <summary>
    /// Asks the user to confirm a destructive action. Set by the window, which owns the dialog.
    /// Deletion is permanent - there is no undo and no archive - so it must never be silent.
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the thread gains messages, so the view can scroll to the bottom.</summary>
    public event Action? ThreadChanged;

    public ObservableCollection<Conversation> Conversations { get; } = new();

    public ObservableCollection<SmsMessage> ThreadMessages { get; } = new();

    public ICommand SendCommand { get; }

    public DelegateCommand NewConversationCommand { get; }

    public DelegateCommand CancelNewConversationCommand { get; }

    public ICommand RetryCommand { get; }

    public AsyncCommand DeleteSelectedMessagesCommand { get; }

    public AsyncCommand DeleteSelectedConversationsCommand { get; }

    /// <summary>Ids of the messages ticked in the open thread.</summary>
    private readonly List<long> _selectedMessageIds = [];

    /// <summary>Peer keys of the threads ticked in the list.</summary>
    private readonly List<string> _selectedConversationKeys = [];

    public int SelectedMessageCount => _selectedMessageIds.Count;

    public int SelectedConversationCount => _selectedConversationKeys.Count;

    public bool HasSelectedMessages => SelectedMessageCount > 0;

    public bool HasSelectedConversations => SelectedConversationCount > 0;

    public string DeleteMessagesLabel => _loc.DeleteNMessages(SelectedMessageCount);

    public string DeleteConversationsLabel => _loc.DeleteNConversations(SelectedConversationCount);

    /// <summary>
    /// Re-reads the strings this view model composes itself. Bindings pick up language changes
    /// on their own, but text already built into a property has to be recomputed.
    /// </summary>
    public void RefreshLocalisedText()
    {
        OnPropertyChanged(nameof(ThreadTitle));
        OnPropertyChanged(nameof(ThreadSubtitle));
        OnPropertyChanged(nameof(DeleteMessagesLabel));
        OnPropertyChanged(nameof(DeleteConversationsLabel));
        OnPropertyChanged(nameof(ComposerCounter));

        // The per-message status text comes from a value converter, which the binding engine has
        // no reason to re-run when only the language changed. Rebuilding the thread is the
        // reliable way to repaint it; losing scroll position on a deliberate language switch is
        // a fair trade.
        ThreadMessages.Clear();
        LoadThread();
    }

    /// <summary>Called by the view when the thread's selection changes.</summary>
    public void SetSelectedMessages(IEnumerable<SmsMessage> messages)
    {
        _selectedMessageIds.Clear();
        _selectedMessageIds.AddRange(messages.Select(m => m.Id));

        OnPropertyChanged(nameof(SelectedMessageCount));
        OnPropertyChanged(nameof(HasSelectedMessages));
        OnPropertyChanged(nameof(DeleteMessagesLabel));
        DeleteSelectedMessagesCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Called by the view when the thread list's selection changes.</summary>
    public void SetSelectedConversations(IEnumerable<Conversation> conversations)
    {
        _selectedConversationKeys.Clear();
        _selectedConversationKeys.AddRange(conversations.Select(c => c.PeerKey));

        OnPropertyChanged(nameof(SelectedConversationCount));
        OnPropertyChanged(nameof(HasSelectedConversations));
        OnPropertyChanged(nameof(DeleteConversationsLabel));
        DeleteSelectedConversationsCommand.RaiseCanExecuteChanged();
    }

    private async Task DeleteSelectedMessagesAsync()
    {
        var ids = _selectedMessageIds.ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var confirmed = ConfirmAsync is null || await ConfirmAsync(
            _loc.DeleteMessagesTitle,
            _loc.ConfirmDeleteMessages(ids.Count));

        if (!confirmed)
        {
            return;
        }

        try
        {
            var deleted = await Task.Run(() => _smsManager.DeleteMessages(ids));
            SetSelectedMessages([]);
            StatusText = _loc.DeletedMessages(deleted);

            // The thread may now be empty, which removes it from the list entirely.
            var key = SelectedConversation?.PeerKey;
            await RefreshAsync();

            if (key is not null && Conversations.All(c => c.PeerKey != key))
            {
                SelectedConversation = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete messages.");
            StatusText = _loc.DeleteFailed(ex.Message);
        }
    }

    private async Task DeleteSelectedConversationsAsync()
    {
        var keys = _selectedConversationKeys.ToList();
        if (keys.Count == 0)
        {
            return;
        }

        var names = Conversations
            .Where(c => keys.Contains(c.PeerKey))
            .Select(c => c.Title)
            .ToList();

        var preview = names.Count <= 3
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(3)) + " " + _loc.AndNMore(names.Count - 3);

        var confirmed = ConfirmAsync is null || await ConfirmAsync(
            _loc.DeleteConversationsTitle,
            _loc.ConfirmDeleteConversations(preview));

        if (!confirmed)
        {
            return;
        }

        try
        {
            var deleted = await Task.Run(() => _smsManager.DeleteConversations(keys));
            SetSelectedConversations([]);

            if (SelectedConversation is not null && keys.Contains(SelectedConversation.PeerKey))
            {
                SelectedConversation = null;
            }

            StatusText = _loc.DeletedConversations(keys.Count, deleted);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete conversations.");
            StatusText = _loc.DeleteFailed(ex.Message);
        }
    }

    public Conversation? SelectedConversation
    {
        get => _selectedConversation;
        set
        {
            if (SetField(ref _selectedConversation, value))
            {
                if (value is not null)
                {
                    _isComposingNew = false;
                    OnPropertyChanged(nameof(IsComposingNew));
                }

                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(ThreadTitle));
                OnPropertyChanged(nameof(ThreadSubtitle));
                OnPropertyChanged(nameof(CanEditRecipient));
                LoadThread();
                RaiseSendCanExecute();
            }
        }
    }

    public bool HasSelection => SelectedConversation is not null || IsComposingNew;

    /// <summary>True while the user is starting a thread with a number that has none yet.</summary>
    public bool IsComposingNew
    {
        get => _isComposingNew;
        private set
        {
            if (SetField(ref _isComposingNew, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(CanEditRecipient));
                OnPropertyChanged(nameof(ThreadTitle));
                OnPropertyChanged(nameof(ThreadSubtitle));
            }
        }
    }

    public bool CanEditRecipient => IsComposingNew;

    public string ThreadTitle => IsComposingNew
        ? _loc.NewMessageTitle
        : SelectedConversation?.Title ?? _loc.SelectConversation;

    public string ThreadSubtitle => IsComposingNew
        ? _loc.EnterRecipient
        : SelectedConversation?.Subtitle ?? string.Empty;

    /// <summary>Only used when starting a new thread; existing threads take the peer's number.</summary>
    public string NewRecipient
    {
        get => _newRecipient;
        set
        {
            if (SetField(ref _newRecipient, value))
            {
                RaiseSendCanExecute();
            }
        }
    }

    public string MessageBody
    {
        get => _messageBody;
        set
        {
            if (SetField(ref _messageBody, value))
            {
                RaiseSendCanExecute();
                OnPropertyChanged(nameof(ComposerCounter));
                OnPropertyChanged(nameof(ShowUnicodeWarning));
            }
        }
    }

    /// <summary>
    /// Characters, segments and encoding for what is currently typed.
    /// </summary>
    /// <remarks>
    /// Shown because the arithmetic is unintuitive and costs money: a single emoji forces UCS-2,
    /// which cuts capacity from 160 characters to 70, and each segment is billed separately.
    /// </remarks>
    public string ComposerCounter
    {
        get
        {
            var info = SmsSegments.Measure(MessageBody);
            return _loc.ComposerCounter(info.Characters, info.Segments, info.RequiresUnicode);
        }
    }

    /// <summary>True once the text has forced Unicode and the message is no longer trivial.</summary>
    public bool ShowUnicodeWarning
    {
        get
        {
            var info = SmsSegments.Measure(MessageBody);
            return info.RequiresUnicode && info.Characters > 0;
        }
    }

    /// <summary>Inserts an emoji, replacing whatever the caret has selected.</summary>
    public void InsertAtCaret(string text, int selectionStart, int selectionLength)
    {
        var body = MessageBody ?? string.Empty;
        var start = Math.Clamp(selectionStart, 0, body.Length);
        var length = Math.Clamp(selectionLength, 0, body.Length - start);

        MessageBody = body.Remove(start, length).Insert(start, text);
    }

    /// <summary>Filters the thread list by contact name or number.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public void StartNewConversation()
    {
        SelectedConversation = null;
        NewRecipient = string.Empty;
        MessageBody = string.Empty;
        IsComposingNew = true;
        ThreadMessages.Clear();
    }

    private void CancelNewConversation()
    {
        IsComposingNew = false;
        NewRecipient = string.Empty;
    }

    /// <summary>
    /// Reloads threads and the open thread. Database work happens off the UI thread; the results
    /// are merged so the list does not flicker or lose the user's place on the 3-second poll.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            var query = SearchText;
            var selectedKey = SelectedConversation?.PeerKey;

            var (conversations, thread) = await Task.Run(() =>
            {
                var all = _smsManager.GetConversations();

                if (!string.IsNullOrWhiteSpace(query))
                {
                    var needle = query.Trim();
                    var needleKey = PhoneNumber.ToKey(needle);
                    all = all.Where(c =>
                            c.Title.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                            c.PeerDisplay.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                            (needleKey.Length > 0 && c.PeerKey.Contains(needleKey, StringComparison.Ordinal)))
                        .ToList();
                }

                var messages = selectedKey is null
                    ? (IReadOnlyList<SmsMessage>)Array.Empty<SmsMessage>()
                    : _smsManager.GetConversationMessages(selectedKey);

                return (all, messages);
            });

            RunOnUi(() =>
            {
                MergeConversations(conversations);
                if (selectedKey is not null)
                {
                    MergeThread(thread);
                }
                else if (DemoMode.IsEnabled && Conversations.Count > 0)
                {
                    // An empty reading pane is the correct first impression for a real user and
                    // the wrong one for a documentation screenshot, which exists to show a thread.
                    SelectedConversation = Conversations[0];

                    // Opening a thread also ticks it in the list, which raises the bulk-delete
                    // bar. Queued rather than called directly, so it runs after the list's own
                    // selection event has been handled.
                    RunOnUi(() => SetSelectedConversations([]));
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh conversations.");
            RunOnUi(() => StatusText = _loc.LoadListFailed(ex.Message));
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    private void LoadThread()
    {
        var key = SelectedConversation?.PeerKey;
        if (key is null)
        {
            ThreadMessages.Clear();
            return;
        }

        try
        {
            MergeThread(_smsManager.GetConversationMessages(key));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load thread {Key}.", key);
        }
    }

    /// <summary>Updates rows in place so the ListView keeps its scroll position and selection.</summary>
    private void MergeConversations(IReadOnlyList<Conversation> incoming)
    {
        var existing = Conversations.ToDictionary(c => c.PeerKey);

        for (var i = 0; i < incoming.Count; i++)
        {
            var next = incoming[i];
            if (existing.TryGetValue(next.PeerKey, out var current))
            {
                current.CopyStateFrom(next);
                current.Contact = next.Contact;

                var index = Conversations.IndexOf(current);
                if (index != i)
                {
                    Conversations.Move(index, i);
                }
            }
            else
            {
                Conversations.Insert(i, next);
            }
        }

        while (Conversations.Count > incoming.Count)
        {
            Conversations.RemoveAt(Conversations.Count - 1);
        }
    }

    private void MergeThread(IReadOnlyList<SmsMessage> incoming)
    {
        var grew = incoming.Count > ThreadMessages.Count;
        var existing = ThreadMessages.ToDictionary(m => m.Id);

        for (var i = 0; i < incoming.Count; i++)
        {
            var next = incoming[i];
            if (existing.TryGetValue(next.Id, out var current))
            {
                current.CopyStateFrom(next);

                var index = ThreadMessages.IndexOf(current);
                if (index != i)
                {
                    ThreadMessages.Move(index, i);
                }
            }
            else
            {
                ThreadMessages.Insert(i, next);
            }
        }

        while (ThreadMessages.Count > incoming.Count)
        {
            ThreadMessages.RemoveAt(ThreadMessages.Count - 1);
        }

        if (grew)
        {
            ThreadChanged?.Invoke();
        }
    }

    private bool CanSend()
    {
        if (string.IsNullOrWhiteSpace(MessageBody))
        {
            return false;
        }

        return IsComposingNew
            ? !string.IsNullOrWhiteSpace(NewRecipient)
            : SelectedConversation is not null;
    }

    private async Task SendAsync()
    {
        var recipient = IsComposingNew ? NewRecipient : SelectedConversation?.PeerDisplay;
        var body = MessageBody;

        if (string.IsNullOrWhiteSpace(recipient))
        {
            StatusText = _loc.NoRecipient;
            return;
        }

        try
        {
            var (id, segments) = await Task.Run(() =>
                (_smsManager.SendSms(recipient, body), _smsManager.EstimateSegmentCount(body)));

            StatusText = _loc.Queued(id, recipient, segments);
            MessageBody = string.Empty;

            // A brand-new thread only exists after the first message is stored, so select it
            // once the refresh has brought it in.
            var newKey = PhoneNumber.ToKey(recipient);
            IsComposingNew = false;
            await RefreshAsync();
            SelectedConversation ??= Conversations.FirstOrDefault(c => c.PeerKey == newKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue message.");
            StatusText = _loc.SendFailed(ex.Message);
        }
    }

    public async Task RetryAsync(long id)
    {
        try
        {
            var ok = await Task.Run(() => _smsManager.RetryFailedMessage(id));
            StatusText = ok ? _loc.RetryQueued(id) : _loc.RetryNotFailed(id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retry message #{Id}.", id);
            StatusText = _loc.RetryFailed(ex.Message);
        }

        await RefreshAsync();
    }

    private void RaiseSendCanExecute()
    {
        if (SendCommand is AsyncCommand cmd)
        {
            cmd.RaiseCanExecuteChanged();
        }
    }

    private void RunOnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        _dispatcher.TryEnqueue(() => action());
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
