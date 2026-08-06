using Microsoft.Extensions.Logging;
using SuperDoc.Sms.Logging;
using SuperDoc.Sms.Models;
using SuperDoc.Sms.Services;
using SuperDoc.Sms.Storage;

// Console harness for the SMS module. Sending works from this unpackaged build;
// receiving requires the packaged WinUI app (see README).
// Both directions must be UTF-8. Without InputEncoding, piped Vietnamese text is decoded with
// the console's OEM codepage and the diacritics are destroyed before they ever reach the modem.
try
{
    Console.OutputEncoding = System.Text.Encoding.UTF8;
    Console.InputEncoding = System.Text.Encoding.UTF8;
}
catch (IOException)
{
    // No real console attached (fully redirected); the defaults are fine in that case.
}

using var loggerFactory = LoggingSetup.CreateLoggerFactory();
using var repo = new SmsRepository(logger: loggerFactory.CreateLogger<SmsRepository>());

// Seeding runs against the repository alone: it must not open the modem, because the copy the
// user relies on is holding the receive registration.
if (args.Length > 0 && args[0].Equals("seed-demo", StringComparison.OrdinalIgnoreCase))
{
    SuperDoc.Sms.Cli.DemoSeeder.Seed(repo);
    Console.WriteLine("Demo data written. Set SUPERDOC_SMS_DEMO=1 to run the app against it.");
    return;
}

using var sms = new SmsManager(
    repo,
    loggerFactory.CreateLogger<SmsManager>(),
    loggerFactory.CreateLogger<SmsQueueProcessor>());

sms.MessageReceived += (_, m) => Console.WriteLine($"\n[INCOMING] from={m.From}: {m.Body}\n> ");

var snapshot = await sms.GetDeviceSnapshotAsync();
PrintSnapshot(snapshot);

Console.WriteLine();
Console.WriteLine("Commands:");
Console.WriteLine("  send <phone> <message>   queue an SMS (e.g. send +84912345678 Xin chao)");
Console.WriteLine("  list [n]                 show the most recent messages (default 20)");
Console.WriteLine("  status                   re-read modem state");
Console.WriteLine("  quit");

while (true)
{
    Console.Write("> ");
    var line = Console.ReadLine();
    if (line is null)
    {
        break;
    }

    // Redirected stdin can carry a BOM on the first line; strip it so commands still match.
    line = line.Trim().TrimStart('﻿').Trim();

    if (line.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    if (line.Length == 0)
    {
        continue;
    }

    if (line.StartsWith("send ", StringComparison.OrdinalIgnoreCase))
    {
        // Split into exactly 3 so the body keeps its own spaces.
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            Console.WriteLine("Invalid format. Use: send <phone> <message>");
            continue;
        }

        try
        {
            var id = sms.SendSms(parts[1], parts[2]);
            Console.WriteLine($"Queued SMS #{id} ({sms.EstimateSegmentCount(parts[2])} segment(s)).");
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine("Rejected: " + ex.Message);
        }

        continue;
    }

    if (line.StartsWith("list", StringComparison.OrdinalIgnoreCase))
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var max = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 20;
        foreach (var m in sms.GetRecentMessages(max))
        {
            var peer = m.Status == SmsStatus.Received ? $"from={m.From}" : $"to={m.To}";
            var err = string.IsNullOrEmpty(m.ErrorMessage) ? string.Empty : $" err={m.ErrorMessage}";
            Console.WriteLine($"#{m.Id} [{m.Status}] {peer} {m.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} {m.Body}{err}");
        }

        continue;
    }

    if (line.Equals("status", StringComparison.OrdinalIgnoreCase))
    {
        PrintSnapshot(await sms.GetDeviceSnapshotAsync());
        continue;
    }

    Console.WriteLine("Unknown command.");
}

static void PrintSnapshot(SmsDeviceSnapshot s)
{
    Console.WriteLine("=== Modem ===");
    if (!s.IsAvailable)
    {
        Console.WriteLine("  UNAVAILABLE: " + s.Diagnostic);
        return;
    }

    Console.WriteLine($"  Status        : {s.DeviceStatus}");
    Console.WriteLine($"  Number        : {s.AccountPhoneNumber}");
    Console.WriteLine($"  SMSC          : {s.SmscAddress}");
    Console.WriteLine($"  Cellular class: {s.CellularClass}");
    Console.WriteLine($"  Can receive   : {s.CanReceive}");
    Console.WriteLine($"  Note          : {s.Diagnostic}");
}
