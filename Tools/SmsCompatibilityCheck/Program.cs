using Windows.Devices.Sms;

// Reports whether this machine's WWAN modem can send and receive SMS, before anyone spends
// time building and installing the app. Read-only: it registers and immediately unregisters,
// and never transmits anything.
//
// SmsFilterActionType.Drop is deliberately never attempted. A Drop rule active even for an
// instant would discard a real incoming message.

Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine("SUPERDOC SMS Manager - compatibility check");
Console.WriteLine(new string('=', 60));
Console.WriteLine();

var packaged = false;
try
{
    var package = Windows.ApplicationModel.Package.Current;
    packaged = true;
    Console.WriteLine($"Running as       : packaged ({package.Id.FamilyName})");
}
catch (Exception)
{
    Console.WriteLine("Running as       : unpackaged  (this is the supported shape)");
}

// ---- 1. Is there a modem, and does it have a SIM that has registered? --------------------
SmsDevice2? device = null;
try
{
    device = SmsDevice2.GetDefault();
}
catch (Exception ex)
{
    Console.WriteLine($"Modem            : ERROR 0x{ex.HResult:X8} - {ex.Message}");
}

if (device is null)
{
    Console.WriteLine("Modem            : NOT FOUND");
    Console.WriteLine();
    Console.WriteLine("No WWAN SMS device is available. Check that:");
    Console.WriteLine("  - the laptop actually has a cellular/WWAN module (not just Wi-Fi)");
    Console.WriteLine("  - a SIM is inserted and mobile broadband is switched on");
    Console.WriteLine("  - Windows shows the modem under Settings > Network > Cellular");
    return 2;
}

Console.WriteLine($"Modem            : OK");
Console.WriteLine($"  Status         : {device.DeviceStatus}");
Console.WriteLine($"  Own number     : {Mask(device.AccountPhoneNumber)}");
Console.WriteLine($"  SMSC           : {Mask(device.SmscAddress)}");
Console.WriteLine($"  Cellular class : {device.CellularClass}");
Console.WriteLine();

if (device.DeviceStatus != SmsDeviceStatus.Ready)
{
    Console.WriteLine($"NOTE: the modem is '{device.DeviceStatus}', not 'Ready'.");
    Console.WriteLine("      Sending will not work until it reaches Ready - it can take up to a");
    Console.WriteLine("      minute after boot, and stays locked if the SIM has a PIN.");
    Console.WriteLine();
}

// ---- 2. Sending -------------------------------------------------------------------------
// Length calculation exercises the send path without transmitting.
var canSend = false;
try
{
    var probe = new SmsTextMessage2
    {
        To = "+10000000000",
        Body = "compatibility check",
        Encoding = SmsEncoding.Optimal
    };

    _ = device.CalculateLength(probe);
    canSend = true;
    Console.WriteLine("Sending          : SUPPORTED");
}
catch (Exception ex)
{
    Console.WriteLine($"Sending          : NOT AVAILABLE (0x{ex.HResult:X8})");
}

// ---- 3. Receiving -----------------------------------------------------------------------
// AcceptImmediately means "consume this message exclusively, ahead of every other app".
// Windows reserves that for the system's default messaging handler and denies everyone else
// with 0xD0000022, so the app uses Peek. Both are reported here to make the difference clear.
string? receiveMode = null;
var results = new List<string>();

foreach (var action in new[]
         {
             SmsFilterActionType.Peek,
             SmsFilterActionType.Accept,
             SmsFilterActionType.AcceptImmediately
         })
{
    var id = $"SmsCompatibilityCheck.{action}.{Guid.NewGuid():N}";
    try
    {
        var rules = new SmsFilterRules(action);
        rules.Rules.Add(new SmsFilterRule(SmsMessageType.Text));

        var registration = SmsMessageRegistration.Register(id, rules);
        results.Add($"  {action,-18} : OK");
        receiveMode ??= action.ToString();

        try { registration.Unregister(); } catch { /* transient at teardown */ }
    }
    catch (Exception ex)
    {
        results.Add($"  {action,-18} : denied (0x{ex.HResult:X8})");
    }
}

Console.WriteLine(receiveMode is null ? "Receiving        : NOT AVAILABLE" : "Receiving        : SUPPORTED");
foreach (var line in results)
{
    Console.WriteLine(line);
}

Console.WriteLine();
Console.WriteLine(new string('=', 60));

// ---- Verdict ----------------------------------------------------------------------------
if (canSend && receiveMode is not null)
{
    Console.WriteLine("RESULT: this machine can SEND and RECEIVE. The app should work fully.");
    return 0;
}

if (canSend)
{
    Console.WriteLine("RESULT: this machine can SEND but not receive.");
    if (packaged)
    {
        Console.WriteLine("        You ran the packaged build. A sideloaded unsigned MSIX is denied");
        Console.WriteLine("        SMS registration - try the unpackaged build before concluding");
        Console.WriteLine("        the hardware is at fault.");
    }

    return 1;
}

Console.WriteLine("RESULT: this machine cannot send. The modem is present but the SMS API is");
Console.WriteLine("        unavailable - check the SIM, the carrier profile and the modem status.");
return 2;

// Keeps the SIM's identifiers out of pasted output; enough remains to confirm it is populated.
static string Mask(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return "(empty)";
    }

    return value.Length <= 4 ? value : value[..3] + new string('*', value.Length - 5) + value[^2..];
}
