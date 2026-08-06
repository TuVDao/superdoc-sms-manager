using SuperDoc.Sms.Models;
using SuperDoc.Sms.Storage;
using Xunit;

namespace SuperDoc.Sms.Tests;

/// <summary>
/// Read state decides what the thread list puts in bold, so the failure is quiet: either a
/// message the user has never seen looks handled, or the whole history shouts for attention.
/// </summary>
public class UnreadTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SmsRepository _repo;

    public UnreadTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"superdoc-unread-{Guid.NewGuid():N}.db");
        _repo = new SmsRepository(_dbPath);
        PhoneNumber.SetDefaultCountryCode("44");
    }

    public void Dispose()
    {
        _repo.Dispose();

        // Disposing the connection returns it to Microsoft.Data.Sqlite's pool rather than closing
        // the handle, so the file stays locked until the pool is emptied. Harmless in the app,
        // which holds one connection for its lifetime, but it blocks cleanup here.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // WAL leaves two sidecars behind; deleting only the database would litter the temp folder.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = _dbPath + suffix;
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    // ----- What counts as unread ----------------------------------------------------------

    [Fact]
    public void AnIncomingMessageStartsUnread()
    {
        Receive("07700 900123", "Are you there?");

        Assert.Equal(1, _repo.CountUnread());
        Assert.Equal(1, Thread("07700 900123").UnreadCount);
    }

    [Fact]
    public void AMessageWeSentIsNeverUnread()
    {
        Send("07700 900123", "On my way");

        // Otherwise every thread would light up the moment the user replied in it.
        Assert.Equal(0, _repo.CountUnread());
        Assert.Equal(0, Thread("07700 900123").UnreadCount);
    }

    [Fact]
    public void UnreadIsCountedPerThread()
    {
        Receive("07700 900123", "one");
        Receive("07700 900123", "two");
        Receive("07700 900456", "three");
        Send("07700 900456", "reply");

        Assert.Equal(2, Thread("07700 900123").UnreadCount);
        Assert.Equal(1, Thread("07700 900456").UnreadCount);
        Assert.Equal(3, _repo.CountUnread());
    }

    // ----- Marking read --------------------------------------------------------------------

    [Fact]
    public void MarkingAThreadReadClearsOnlyThatThread()
    {
        Receive("07700 900123", "one");
        Receive("07700 900456", "two");

        var marked = _repo.MarkConversationRead(PhoneNumber.ToKey("07700 900123"));

        Assert.Equal(1, marked);
        Assert.Equal(0, Thread("07700 900123").UnreadCount);
        Assert.Equal(1, Thread("07700 900456").UnreadCount);
    }

    [Fact]
    public void MarkingReadTwiceMarksNothingTheSecondTime()
    {
        Receive("07700 900123", "one");
        var key = PhoneNumber.ToKey("07700 900123");

        Assert.Equal(1, _repo.MarkConversationRead(key));

        // The view model uses the count to decide whether anything changed; a non-zero result
        // here would repaint the list on every poll for a thread that is already read.
        Assert.Equal(0, _repo.MarkConversationRead(key));
    }

    [Fact]
    public void AMessageArrivingAfterTheThreadWasReadIsUnreadAgain()
    {
        Receive("07700 900123", "one");
        _repo.MarkConversationRead(PhoneNumber.ToKey("07700 900123"));

        Receive("07700 900123", "and another thing");

        Assert.Equal(1, Thread("07700 900123").UnreadCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void MarkingReadWithNoThreadDoesNothing(string? key)
    {
        Receive("07700 900123", "one");

        Assert.Equal(0, _repo.MarkConversationRead(key!));
        Assert.Equal(1, _repo.CountUnread());
    }

    [Fact]
    public void ReadStateSurvivesReloadingTheMessage()
    {
        Receive("07700 900123", "one");
        _repo.MarkConversationRead(PhoneNumber.ToKey("07700 900123"));

        var message = _repo.GetConversationMessages(PhoneNumber.ToKey("07700 900123")).Single();

        Assert.NotNull(message.ReadAt);
        Assert.False(message.IsUnread);
    }

    // ----- Upgrading an existing database ---------------------------------------------------

    [Fact]
    public void ExistingMessagesAreNotMarkedUnreadByTheUpgrade()
    {
        Receive("07700 900123", "from before the feature existed");
        Send("07700 900123", "a reply from back then");
        _repo.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Reproduce the upgrade: drop the column, then let the repository migrate the file again.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = "ALTER TABLE SmsMessages DROP COLUMN ReadAt;";
            drop.ExecuteNonQuery();
        }

        using var upgraded = new SmsRepository(_dbPath);

        // Bolding a history the user has already read would make the feature useless on day one.
        Assert.Equal(0, upgraded.CountUnread());
    }

    // ----- Helpers ---------------------------------------------------------------------------

    private Conversation Thread(string peer)
    {
        var key = PhoneNumber.ToKey(peer);
        return _repo.GetConversations().Single(c => c.PeerKey == key);
    }

    private void Receive(string from, string body) => _repo.Insert(new SmsMessage
    {
        From = from,
        To = string.Empty,
        Body = body,
        CreatedAt = DateTimeOffset.UtcNow,
        Status = SmsStatus.Received
    });

    private void Send(string to, string body) => _repo.Insert(new SmsMessage
    {
        From = string.Empty,
        To = to,
        Body = body,
        CreatedAt = DateTimeOffset.UtcNow,
        SentAt = DateTimeOffset.UtcNow,
        Status = SmsStatus.Sent
    });
}
