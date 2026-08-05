using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MyApp.Models;

namespace MyApp.Storage;

public sealed class SmsRepository : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _sync = new();
    private readonly ILogger<SmsRepository>? _logger;

    private const string SelectColumns =
        "Id, ToNumber, FromNumber, Body, CreatedAt, SentAt, Status, RetryCount, ErrorMessage, NextAttemptAt, PeerKey";

    public SmsRepository(string? dbPath = null, ILogger<SmsRepository>? logger = null)
    {
        _logger = logger;
        var effectivePath = dbPath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "smsmanager.db");

        Directory.CreateDirectory(Path.GetDirectoryName(effectivePath)!);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = effectivePath }.ToString());
        _conn.Open();
        ConfigureConnection();
        EnsureTables();
        _logger?.LogInformation("SmsRepository initialized. Database: {DbPath}", effectivePath);
    }

    /// <summary>
    /// WAL lets the console harness and the WinUI app hold the same database open at once
    /// instead of one of them failing with "database is locked"; the busy timeout absorbs the
    /// brief overlaps between the UI's polling reads and the queue worker's writes.
    /// </summary>
    private void ConfigureConnection()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
    }

    private void EnsureTables()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS SmsMessages (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ToNumber TEXT NOT NULL DEFAULT '',
                FromNumber TEXT NOT NULL DEFAULT '',
                Body TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL,
                SentAt TEXT,
                Status INTEGER NOT NULL,
                RetryCount INTEGER NOT NULL DEFAULT 0,
                ErrorMessage TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS IX_SmsMessages_Status_CreatedAt
            ON SmsMessages(Status, CreatedAt);
            """;
        cmd.ExecuteNonQuery();

        // Databases created before retry scheduling existed lack this column; add it in place
        // so an existing message history survives the upgrade.
        if (!ColumnExists("NextAttemptAt"))
        {
            using var alter = _conn.CreateCommand();
            alter.CommandText = "ALTER TABLE SmsMessages ADD COLUMN NextAttemptAt TEXT;";
            alter.ExecuteNonQuery();
            _logger?.LogInformation("Migrated database: added NextAttemptAt column.");
        }

        if (!ColumnExists("PeerKey"))
        {
            using var alter = _conn.CreateCommand();
            alter.CommandText = "ALTER TABLE SmsMessages ADD COLUMN PeerKey TEXT NOT NULL DEFAULT '';";
            alter.ExecuteNonQuery();
            _logger?.LogInformation("Migrated database: added PeerKey column.");
        }

        using var index = _conn.CreateCommand();
        index.CommandText = """
            CREATE INDEX IF NOT EXISTS IX_SmsMessages_PeerKey_CreatedAt
            ON SmsMessages(PeerKey, CreatedAt);

            CREATE TABLE IF NOT EXISTS Contacts (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                FamilyName TEXT NOT NULL DEFAULT '',
                GivenName TEXT NOT NULL DEFAULT '',
                Note TEXT NOT NULL DEFAULT '',
                PhoneNumber TEXT NOT NULL DEFAULT '',
                PhoneKey TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS UX_Contacts_PhoneKey ON Contacts(PhoneKey);

            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """;
        index.ExecuteNonQuery();

        // Contacts gained a form of address and a postal address after the table shipped.
        foreach (var column in new[] { "Title", "Address" })
        {
            if (ContactColumnExists(column))
            {
                continue;
            }

            using var alter = _conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE Contacts ADD COLUMN {column} TEXT NOT NULL DEFAULT '';";
            alter.ExecuteNonQuery();
            _logger?.LogInformation("Migrated database: added Contacts.{Column} column.", column);
        }

        NormalizeTimestampsToUtc();
        BackfillPeerKeys();
    }

    /// <summary>
    /// Rewrites timestamps written before storage was normalised to UTC.
    /// </summary>
    /// <remarks>
    /// Rows already stored with a non-UTC offset would keep sorting by their literal text, so
    /// existing conversations would stay interleaved wrongly even after the write path is fixed.
    /// </remarks>
    private void NormalizeTimestampsToUtc()
    {
        List<(long Id, string? Created, string? Sent, string? Next)> rows = [];

        using (var read = _conn.CreateCommand())
        {
            // Anything not already ending in +00:00 is a candidate; NULL and '' are left alone.
            read.CommandText = """
                SELECT Id, CreatedAt, SentAt, NextAttemptAt
                FROM SmsMessages
                WHERE (CreatedAt IS NOT NULL AND CreatedAt <> '' AND CreatedAt NOT LIKE '%+00:00')
                   OR (SentAt IS NOT NULL AND SentAt <> '' AND SentAt NOT LIKE '%+00:00')
                   OR (NextAttemptAt IS NOT NULL AND NextAttemptAt <> '' AND NextAttemptAt NOT LIKE '%+00:00');
                """;
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        using var transaction = _conn.BeginTransaction();
        using (var update = _conn.CreateCommand())
        {
            update.CommandText = """
                UPDATE SmsMessages
                SET CreatedAt = $created, SentAt = $sent, NextAttemptAt = $next
                WHERE Id = $id;
                """;
            var createdParam = update.Parameters.Add("$created", SqliteType.Text);
            var sentParam = update.Parameters.Add("$sent", SqliteType.Text);
            var nextParam = update.Parameters.Add("$next", SqliteType.Text);
            var idParam = update.Parameters.Add("$id", SqliteType.Integer);

            foreach (var (id, created, sent, next) in rows)
            {
                createdParam.Value = ToUtcText(created) ?? DateTimeOffset.UtcNow.ToString("O");
                sentParam.Value = (object?)ToUtcText(sent) ?? DBNull.Value;
                nextParam.Value = (object?)ToUtcText(next) ?? DBNull.Value;
                idParam.Value = id;
                update.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        _logger?.LogInformation("Normalised timestamps to UTC for {Count} message(s).", rows.Count);
    }

    private static string? ToUtcText(string? raw)
        => string.IsNullOrWhiteSpace(raw) || !DateTimeOffset.TryParse(raw, out var parsed)
            ? null
            : parsed.ToUniversalTime().ToString("O");

    /// <summary>
    /// Fills <c>PeerKey</c> for rows written before the column existed. Without this the whole
    /// existing history would group under one empty key and every old message would be missing
    /// from its conversation.
    /// </summary>
    private void BackfillPeerKeys()
    {
        List<(long Id, string From, string To)> rows = [];

        using (var read = _conn.CreateCommand())
        {
            read.CommandText = "SELECT Id, FromNumber, ToNumber FROM SmsMessages WHERE PeerKey = '';";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        using var transaction = _conn.BeginTransaction();
        using (var update = _conn.CreateCommand())
        {
            update.CommandText = "UPDATE SmsMessages SET PeerKey = $key WHERE Id = $id;";
            var keyParam = update.Parameters.Add("$key", SqliteType.Text);
            var idParam = update.Parameters.Add("$id", SqliteType.Integer);

            foreach (var (id, from, to) in rows)
            {
                // Same rule the model uses: an inbound row is identified by a non-empty sender.
                keyParam.Value = PhoneNumber.ToKey(string.IsNullOrEmpty(from) ? to : from);
                idParam.Value = id;
                update.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        _logger?.LogInformation("Backfilled PeerKey for {Count} existing message(s).", rows.Count);
    }

    private bool ColumnExists(string columnName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM pragma_table_info('SmsMessages') WHERE name = $name;";
        cmd.Parameters.AddWithValue("$name", columnName);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) > 0;
    }

    private bool ContactColumnExists(string columnName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM pragma_table_info('Contacts') WHERE name = $name;";
        cmd.Parameters.AddWithValue("$name", columnName);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0) > 0;
    }

    // ----- Settings ---------------------------------------------------------------------

    /// <summary>Reads a stored UI preference, or <paramref name="fallback"/> when unset.</summary>
    public string GetSetting(string key, string fallback = "")
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Settings WHERE Key = $key;";
            cmd.Parameters.AddWithValue("$key", key);
            return cmd.ExecuteScalar() as string ?? fallback;
        }
    }

    public void SetSetting(string key, string value)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Settings (Key, Value) VALUES ($key, $value)
                ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Returns messages left mid-flight to the Pending queue. A message marked <c>Sending</c>
    /// when the process died would otherwise be stranded forever: the queue only picks up
    /// <c>Pending</c> rows, and the manual retry button only accepts <c>Failed</c> ones.
    /// </summary>
    public int RequeueInterruptedSends()
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE SmsMessages
                SET Status = $pending, NextAttemptAt = NULL
                WHERE Status = $sending;
                """;
            cmd.Parameters.AddWithValue("$pending", (int)SmsStatus.Pending);
            cmd.Parameters.AddWithValue("$sending", (int)SmsStatus.Sending);
            var affected = cmd.ExecuteNonQuery();
            if (affected > 0)
            {
                _logger?.LogWarning(
                    "Requeued {Count} message(s) interrupted mid-send by a previous shutdown.", affected);
            }

            return affected;
        }
    }

    public long Insert(SmsMessage msg)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO SmsMessages
                    (ToNumber, FromNumber, Body, CreatedAt, SentAt, Status, RetryCount, ErrorMessage, NextAttemptAt, PeerKey)
                VALUES ($to, $from, $body, $created, $sent, $status, $retry, $err, $next, $peer);
                SELECT last_insert_rowid();
                """;
            BindParameters(cmd, msg);
            var id = (long)(cmd.ExecuteScalar() ?? 0L);
            _logger?.LogDebug("Inserted SMS #{Id} status={Status} to={To}", id, msg.Status, msg.To);
            return id;
        }
    }

    public void Update(SmsMessage msg)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE SmsMessages SET
                    ToNumber=$to,
                    FromNumber=$from,
                    Body=$body,
                    CreatedAt=$created,
                    SentAt=$sent,
                    Status=$status,
                    RetryCount=$retry,
                    ErrorMessage=$err,
                    NextAttemptAt=$next,
                    PeerKey=$peer
                WHERE Id=$id;
                """;
            BindParameters(cmd, msg);
            cmd.Parameters.AddWithValue("$id", msg.Id);
            cmd.ExecuteNonQuery();
            _logger?.LogDebug(
                "Updated SMS #{Id} status={Status} retry={RetryCount}", msg.Id, msg.Status, msg.RetryCount);
        }
    }

    /// <summary>
    /// Pending messages whose backoff window has elapsed. Rows waiting on a retry delay stay
    /// out of the result, so one failing message no longer blocks the rest of the queue.
    /// </summary>
    public IReadOnlyList<SmsMessage> GetPending(int max = 50)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT {SelectColumns}
                FROM SmsMessages
                WHERE Status = $status
                  AND (NextAttemptAt IS NULL OR NextAttemptAt = '' OR NextAttemptAt <= $now)
                ORDER BY CreatedAt
                LIMIT $max;
                """;
            cmd.Parameters.AddWithValue("$status", (int)SmsStatus.Pending);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$max", max);

            return ReadAll(cmd);
        }
    }

    public SmsMessage? GetById(long id)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {SelectColumns} FROM SmsMessages WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }
    }

    public IReadOnlyList<SmsMessage> GetRecent(int max = 100)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT {SelectColumns}
                FROM SmsMessages
                ORDER BY CreatedAt DESC
                LIMIT $max;
                """;
            cmd.Parameters.AddWithValue("$max", max);
            return ReadAll(cmd);
        }
    }

    public int CountMessages(SmsMailboxFilter mailboxFilter, string? phoneQuery = null)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            var where = BuildWhere(mailboxFilter, phoneQuery, cmd);
            cmd.CommandText = $"SELECT COUNT(1) FROM SmsMessages {where};";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }

    public IReadOnlyList<SmsMessage> GetMessagesPage(
        SmsMailboxFilter mailboxFilter,
        int page,
        int pageSize,
        string? phoneQuery = null)
    {
        if (page < 1)
        {
            page = 1;
        }

        if (pageSize <= 0)
        {
            pageSize = 50;
        }

        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            var where = BuildWhere(mailboxFilter, phoneQuery, cmd);
            cmd.CommandText = $"""
                SELECT {SelectColumns}
                FROM SmsMessages
                {where}
                ORDER BY CreatedAt DESC
                LIMIT $limit OFFSET $offset;
                """;
            cmd.Parameters.AddWithValue("$limit", pageSize);
            cmd.Parameters.AddWithValue("$offset", (page - 1) * pageSize);

            return ReadAll(cmd);
        }
    }

    /// <summary>Moves a failed message back into the queue for an immediate attempt.</summary>
    public bool RetryFailed(long id)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE SmsMessages
                SET Status = $pending, RetryCount = 0, ErrorMessage = $empty, NextAttemptAt = NULL
                WHERE Id = $id AND Status = $failed;
                """;
            cmd.Parameters.AddWithValue("$pending", (int)SmsStatus.Pending);
            cmd.Parameters.AddWithValue("$failed", (int)SmsStatus.Failed);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$empty", string.Empty);

            if (cmd.ExecuteNonQuery() > 0)
            {
                _logger?.LogInformation("Manual retry requested for SMS #{Id}", id);
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// One row per peer, newest activity first. Inbound and outbound rows collapse into the same
    /// thread because they share <c>PeerKey</c>.
    /// </summary>
    public IReadOnlyList<Conversation> GetConversations()
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();

            // The correlated subquery picks the display address from the newest row, so a thread
            // shows the most recent spelling of the number rather than the oldest.
            cmd.CommandText = """
                SELECT
                    m.PeerKey,
                    (SELECT CASE WHEN x.FromNumber <> '' THEN x.FromNumber ELSE x.ToNumber END
                     FROM SmsMessages x
                     WHERE x.PeerKey = m.PeerKey
                     ORDER BY x.CreatedAt DESC, x.Id DESC LIMIT 1) AS PeerAddress,
                    (SELECT x.Body FROM SmsMessages x
                     WHERE x.PeerKey = m.PeerKey
                     ORDER BY x.CreatedAt DESC, x.Id DESC LIMIT 1) AS LastBody,
                    (SELECT x.Status FROM SmsMessages x
                     WHERE x.PeerKey = m.PeerKey
                     ORDER BY x.CreatedAt DESC, x.Id DESC LIMIT 1) AS LastStatus,
                    (SELECT CASE WHEN x.FromNumber <> '' THEN 1 ELSE 0 END FROM SmsMessages x
                     WHERE x.PeerKey = m.PeerKey
                     ORDER BY x.CreatedAt DESC, x.Id DESC LIMIT 1) AS LastIncoming,
                    MAX(m.CreatedAt) AS LastAt,
                    COUNT(1) AS Total,
                    SUM(CASE WHEN m.Status = $failed THEN 1 ELSE 0 END) AS Failed
                FROM SmsMessages m
                WHERE m.PeerKey <> ''
                GROUP BY m.PeerKey
                ORDER BY LastAt DESC;
                """;
            cmd.Parameters.AddWithValue("$failed", (int)SmsStatus.Failed);

            using var reader = cmd.ExecuteReader();
            var items = new List<Conversation>();
            while (reader.Read())
            {
                var address = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                items.Add(new Conversation
                {
                    PeerKey = reader.GetString(0),
                    PeerDisplay = PhoneNumber.ToDisplay(address),
                    LastMessagePreview = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    LastMessageStatus = (SmsStatus)reader.GetInt32(3),
                    LastMessageIsIncoming = reader.GetInt32(4) == 1,
                    LastMessageAt = DateTimeOffset.TryParse(reader.GetString(5), out var at) ? at : DateTimeOffset.MinValue,
                    MessageCount = reader.GetInt32(6),
                    FailedCount = reader.IsDBNull(7) ? 0 : reader.GetInt32(7)
                });
            }

            return items;
        }
    }

    /// <summary>Every message in one thread, oldest first so it reads like a conversation.</summary>
    public IReadOnlyList<SmsMessage> GetConversationMessages(string peerKey, int max = 500)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT {SelectColumns}
                FROM SmsMessages
                WHERE PeerKey = $peer
                ORDER BY CreatedAt, Id
                LIMIT $max;
                """;
            cmd.Parameters.AddWithValue("$peer", peerKey);
            cmd.Parameters.AddWithValue("$max", max);
            return ReadAll(cmd);
        }
    }

    /// <summary>
    /// Permanently removes the given messages. Returns how many rows actually went.
    /// </summary>
    /// <remarks>
    /// A row that the queue is mid-send on can be deleted here; the worker's later
    /// <see cref="Update"/> simply matches no rows. The message may still reach the network -
    /// deleting a record does not recall a transmission - but nothing is left inconsistent.
    /// </remarks>
    public int DeleteMessages(IEnumerable<long> ids)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0)
        {
            return 0;
        }

        lock (_sync)
        {
            using var transaction = _conn.BeginTransaction();
            var deleted = 0;

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM SmsMessages WHERE Id = $id;";
                var idParam = cmd.Parameters.Add("$id", SqliteType.Integer);

                foreach (var id in list)
                {
                    idParam.Value = id;
                    deleted += cmd.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            _logger?.LogInformation("Deleted {Count} message(s).", deleted);
            return deleted;
        }
    }

    /// <summary>Removes whole threads: every message exchanged with each of these peers.</summary>
    public int DeleteConversations(IEnumerable<string> peerKeys)
    {
        var list = peerKeys.Where(k => !string.IsNullOrEmpty(k)).Distinct().ToList();
        if (list.Count == 0)
        {
            return 0;
        }

        lock (_sync)
        {
            using var transaction = _conn.BeginTransaction();
            var deleted = 0;

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM SmsMessages WHERE PeerKey = $key;";
                var keyParam = cmd.Parameters.Add("$key", SqliteType.Text);

                foreach (var key in list)
                {
                    keyParam.Value = key;
                    deleted += cmd.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            _logger?.LogInformation(
                "Deleted {Threads} conversation(s), {Messages} message(s).", list.Count, deleted);
            return deleted;
        }
    }

    // ----- Contacts ---------------------------------------------------------------------

    public IReadOnlyList<Contact> GetContacts()
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT Id, FamilyName, GivenName, Note, PhoneNumber, CreatedAt, Title, Address
                FROM Contacts
                ORDER BY FamilyName, GivenName, PhoneNumber;
                """;

            using var reader = cmd.ExecuteReader();
            var items = new List<Contact>();
            while (reader.Read())
            {
                items.Add(new Contact
                {
                    Id = reader.GetInt64(0),
                    FamilyName = reader.GetString(1),
                    GivenName = reader.GetString(2),
                    Note = reader.GetString(3),
                    PhoneNumber = reader.GetString(4),
                    CreatedAt = DateTimeOffset.TryParse(reader.GetString(5), out var at) ? at : DateTimeOffset.UtcNow,
                    Title = reader.GetString(6),
                    Address = reader.GetString(7)
                });
            }

            return items;
        }
    }

    /// <summary>
    /// Inserts or updates by <see cref="Contact.Id"/>. The phone key is unique, so saving a
    /// second contact on a number that already exists is rejected rather than silently creating
    /// a duplicate that could never be matched deterministically.
    /// </summary>
    public long SaveContact(Contact contact)
    {
        var key = contact.PhoneKey;
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("A contact needs a phone number.", nameof(contact));
        }

        lock (_sync)
        {
            using var clash = _conn.CreateCommand();
            clash.CommandText = "SELECT Id FROM Contacts WHERE PhoneKey = $key AND Id <> $id;";
            clash.Parameters.AddWithValue("$key", key);
            clash.Parameters.AddWithValue("$id", contact.Id);
            if (clash.ExecuteScalar() is not null)
            {
                throw new InvalidOperationException(
                    $"Another contact already uses the number {contact.DisplayPhone}.");
            }

            using var cmd = _conn.CreateCommand();
            if (contact.Id == 0)
            {
                cmd.CommandText = """
                    INSERT INTO Contacts
                        (Title, FamilyName, GivenName, Address, Note, PhoneNumber, PhoneKey, CreatedAt)
                    VALUES ($title, $family, $given, $address, $note, $phone, $key, $created);
                    SELECT last_insert_rowid();
                    """;
            }
            else
            {
                cmd.CommandText = """
                    UPDATE Contacts SET
                        Title=$title, FamilyName=$family, GivenName=$given,
                        Address=$address, Note=$note, PhoneNumber=$phone, PhoneKey=$key
                    WHERE Id=$id;
                    SELECT $id;
                    """;
                cmd.Parameters.AddWithValue("$id", contact.Id);
            }

            cmd.Parameters.AddWithValue("$title", contact.Title?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("$address", contact.Address?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("$family", contact.FamilyName?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("$given", contact.GivenName?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("$note", contact.Note?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("$phone", contact.PhoneNumber?.Trim() ?? string.Empty);
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue(
                "$created",
                Utc(contact.CreatedAt == default ? DateTimeOffset.UtcNow : contact.CreatedAt));

            var id = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            _logger?.LogInformation("Saved contact #{Id} for {Phone}", id, contact.DisplayPhone);
            return id;
        }
    }

    public bool DeleteContact(long id)
    {
        lock (_sync)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM Contacts WHERE Id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    private static string BuildWhere(SmsMailboxFilter mailboxFilter, string? phoneQuery, SqliteCommand cmd)
    {
        var parts = new List<string>();

        switch (mailboxFilter)
        {
            case SmsMailboxFilter.Inbox:
                parts.Add("FromNumber <> ''");
                break;
            case SmsMailboxFilter.Outbox:
                parts.Add("FromNumber = ''");
                break;
            case SmsMailboxFilter.Failed:
                parts.Add("Status = $failedStatus");
                cmd.Parameters.AddWithValue("$failedStatus", (int)SmsStatus.Failed);
                break;
        }

        if (!string.IsNullOrWhiteSpace(phoneQuery))
        {
            parts.Add("(ToNumber LIKE $phone OR FromNumber LIKE $phone)");
            cmd.Parameters.AddWithValue("$phone", $"%{phoneQuery.Trim()}%");
        }

        return parts.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", parts)}";
    }

    private static List<SmsMessage> ReadAll(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var items = new List<SmsMessage>();
        while (reader.Read())
        {
            items.Add(Read(reader));
        }

        return items;
    }

    private static SmsMessage Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        To = reader.GetString(1),
        From = reader.GetString(2),
        Body = reader.GetString(3),
        CreatedAt = ParseTimestamp(reader, 4) ?? DateTimeOffset.UtcNow,
        SentAt = ParseTimestamp(reader, 5),
        Status = (SmsStatus)reader.GetInt32(6),
        RetryCount = reader.GetInt32(7),
        ErrorMessage = reader.GetString(8),
        NextAttemptAt = ParseTimestamp(reader, 9),
        PeerKey = reader.IsDBNull(10) ? string.Empty : reader.GetString(10)
    };

    /// <summary>
    /// Formats a timestamp for storage, always in UTC.
    /// </summary>
    /// <remarks>
    /// Every ordering in this schema is a text comparison on these strings, and text comparison
    /// ignores the offset. Outgoing rows are timestamped with <c>DateTimeOffset.UtcNow</c>
    /// (+00:00) while inbound rows carry the modem's local timestamp (+07:00 here), so mixing
    /// the two made a conversation interleave wrongly: an outgoing message at 11:16 local stored
    /// as "04:16+00:00" sorted before an incoming one at 09:18 stored as "09:18+07:00".
    /// Normalising to UTC on write makes lexical order and chronological order the same thing.
    /// </remarks>
    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private static string? Utc(DateTimeOffset? value) => value is null ? null : Utc(value.Value);

    /// <summary>
    /// Older rows stored "no timestamp" as an empty string rather than NULL, so both have to be
    /// tolerated; an unparseable value is treated as absent instead of failing the whole query.
    /// </summary>
    private static DateTimeOffset? ParseTimestamp(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var text = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
    }

    private static void BindParameters(SqliteCommand cmd, SmsMessage msg)
    {
        cmd.Parameters.AddWithValue("$to", msg.To ?? string.Empty);
        cmd.Parameters.AddWithValue("$from", msg.From ?? string.Empty);
        cmd.Parameters.AddWithValue("$body", msg.Body ?? string.Empty);
        cmd.Parameters.AddWithValue("$created", Utc(msg.CreatedAt));
        cmd.Parameters.AddWithValue("$sent", (object?)Utc(msg.SentAt) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", (int)msg.Status);
        cmd.Parameters.AddWithValue("$retry", msg.RetryCount);
        cmd.Parameters.AddWithValue("$err", msg.ErrorMessage ?? string.Empty);
        cmd.Parameters.AddWithValue("$next", (object?)Utc(msg.NextAttemptAt) ?? DBNull.Value);

        // Derived rather than trusted from the caller, so the grouping key can never drift out
        // of step with the addresses actually stored on the row.
        cmd.Parameters.AddWithValue("$peer", PhoneNumber.ToKey(msg.PeerAddress));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _conn.Dispose();
        }
    }
}
