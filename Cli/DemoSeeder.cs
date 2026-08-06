using SuperDoc.Sms.Models;
using SuperDoc.Sms.Storage;

namespace SuperDoc.Sms.Cli;

/// <summary>
/// Fills a throwaway database with invented contacts and conversations, so the interface can be
/// photographed for the documentation without a real person's number appearing in a public image.
/// </summary>
/// <remarks>
/// Numbers come from Ofcom's <c>07700 900xxx</c> range, which is reserved for fiction and can
/// never be allocated to a subscriber. The country is pinned to the UK so the screenshots do not
/// depend on the region of whichever machine produced them.
/// </remarks>
internal static class DemoSeeder
{
    private const string CountryCode = "44";

    public static void Seed(SmsRepository repo)
    {
        // Written as the user-level setting, not just applied in this process: the app is a
        // separate process and would otherwise fall back to the region of whichever machine is
        // taking the screenshots.
        repo.SetSetting(SmsRepository.CountryCodeSetting, CountryCode);
        repo.ApplyCountryCode(CountryCode, "demo data");

        // English, so the published screenshots are readable by the widest audience.
        repo.SetSetting("ui.language", "en");

        SeedContacts(repo);
        SeedConversations(repo);
    }

    private static void SeedContacts(SmsRepository repo)
    {
        Contact[] contacts =
        [
            // Names are written family-name-first, which is the order the contact list renders
            // (see Contact.DisplayName), and they exercise the non-ASCII path at the same time.
            new()
            {
                Title = "Ms.",
                FamilyName = "Nguyễn",
                GivenName = "Minh Anh",
                PhoneNumber = "07700 900123",
                Address = "41 Bridge Street, Cambridge",
                Note = "Project lead"
            },
            new()
            {
                Title = "Dr.",
                FamilyName = "Tanaka",
                GivenName = "Yuki",
                PhoneNumber = "+44 7700 900456",
                Address = "Unit 8, Riverside Park, Leeds",
                Note = string.Empty
            },
            new()
            {
                Title = string.Empty,
                FamilyName = "Kim",
                GivenName = "Ji-woo",
                PhoneNumber = "07700 900781",
                Address = string.Empty,
                Note = "On site until Friday"
            },
        ];

        foreach (var contact in contacts)
        {
            repo.SaveContact(contact);
        }
    }

    private static void SeedConversations(SmsRepository repo)
    {
        // Relative to now, so the thread always reads as recent whenever it is regenerated.
        var start = DateTimeOffset.UtcNow.AddHours(-26);

        Add(repo, "07700 900123", inbound: true, start, "Are we still on for the 10am walkthrough?");
        Add(repo, "07700 900123", inbound: false, start.AddMinutes(4), "Yes — I'll bring the printed layouts.");
        Add(repo, "07700 900123", inbound: true, start.AddMinutes(9), "Perfect. Room 2 is booked until noon.");
        // The most recent exchange in the whole database, so this is the thread the list opens on.
        Add(repo, "07700 900123", inbound: false, start.AddHours(24), "Just landed. Heading over now 👍");
        Add(repo, "07700 900123", inbound: true, start.AddHours(24).AddMinutes(6), "No rush, kettle's on.");

        Add(repo, "+44 7700 900456", inbound: false, start.AddHours(3), "Sent the revised figures to your inbox.");
        Add(repo, "+44 7700 900456", inbound: true, start.AddHours(4), "Got them, thanks. One question on table 4.");

        Add(repo, "07700 900781", inbound: true, start.AddHours(22), "Site access sorted for tomorrow.");

        // An alphanumeric sender, which is how carriers and public services actually appear.
        Add(repo, "VODAFONE", inbound: true, start.AddHours(12), "You have used 80% of your data allowance.");

        // A failure, so the retry affordance is visible in the screenshot.
        var failed = new SmsMessage
        {
            To = "07700 900781",
            From = string.Empty,
            Body = "Can you confirm the delivery window?",
            // Deliberately not the newest message: the list is ordered by recency, and the
            // screenshot should open on the fullest thread rather than on a failure.
            CreatedAt = start.AddHours(14),
            Status = SmsStatus.Failed,
            RetryCount = 3,
            ErrorMessage = "Modem reported: network temporarily unavailable"
        };
        repo.Insert(failed);
    }

    private static void Add(
        SmsRepository repo,
        string peer,
        bool inbound,
        DateTimeOffset at,
        string body)
    {
        var message = new SmsMessage
        {
            To = inbound ? string.Empty : peer,
            From = inbound ? peer : string.Empty,
            Body = body,
            CreatedAt = at,
            SentAt = at,
            Status = inbound ? SmsStatus.Received : SmsStatus.Sent
        };

        repo.Insert(message);
    }
}
