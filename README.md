# SUPERDOC SMS Manager

Send **and receive** SMS from the SIM in your Windows laptop.

Many business laptops — ThinkPad, Latitude, EliteBook, Surface and others — ship with a WWAN
module and take a SIM. Windows can *receive* text messages on those machines: they show up in
the built-in **Operator messages** app. But that app's compose box is permanently disabled
("Read-only message"), so there is no supported way to *send* one.

This app closes that gap. It talks to the modem through `Windows.Devices.Sms` and gives you a
normal messaging window: conversation threads, an address book, delivery status, retries and
desktop notifications — using the laptop's own number, not a paired phone.

> If you only want to text from your usual mobile number, **Phone Link** already does that and
> is built into Windows. This app is for the case where the laptop has its own SIM and you want
> to use *that* number.

---

## Does my laptop work?

Check before building anything. The tool is read-only — it never sends a message:

```bat
scripts\check-compatibility.cmd
```

```
Modem            : OK
  Status         : Ready
  Own number     : +84*******23
  Cellular class : Gsm

Sending          : SUPPORTED
Receiving        : SUPPORTED
  Peek               : OK
  Accept             : OK
  AcceptImmediately  : denied (0xD0000022)

RESULT: this machine can SEND and RECEIVE. The app should work fully.
```

Identifiers are masked, so the output is safe to paste into a bug report.

**Requirements**

| | |
|---|---|
| OS | Windows 10 2004 (19041) or later, x64 |
| Hardware | A WWAN/cellular module with an active SIM |
| Build | .NET 8 SDK |

Developed and tested against a **Fibocom L850-GL** in a ThinkPad T480s on a Vietnamese carrier.
Other MBIM modems should work — the app uses only standard Windows APIs — but that is untested.
Reports from other hardware are very welcome.

---

## Install

```bat
scripts\build.cmd
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Desktop
```

`build.cmd` publishes a self-contained unpackaged build into `app\`. `install.ps1` adds Start
menu and desktop shortcuts and launches it. Nothing needs administrator rights.

The app registers itself under `HKCU\…\Run` so it starts with Windows, comes up hidden in the
tray, and keeps running when you close the window — which it must, because inbound SMS only
reaches a *running* process.

To remove it: delete the shortcuts, delete the `SUPERDOC SMS Manager` value under that Run key,
and delete `app\`.

---

## What it does

- **Conversation threads.** Everything exchanged with one peer in one place, both directions,
  in true chronological order, with per-message time and delivery state.
- **Contacts.** Title, family name, given name, phone, address, note. Threads show the contact
  name when there is one and the number when there is not.
- **Reliable sending.** A persisted queue with exponential backoff. A modem that is not ready
  yet does not consume the retry budget; a permanently rejected message fails immediately
  instead of retrying five times.
- **Unicode.** Vietnamese and other non-ASCII text is sent as UCS-2 explicitly rather than
  relying on the driver's interpretation of `Optimal`.
- **Notifications** for incoming messages while the window is hidden.
- **Delete** single messages, several at once, or whole threads — behind a confirmation dialog.
- **Interface in English and Vietnamese**, following the machine's display language, switchable
  live from the header.
- **Adjustable text size** and drag-resizable panels, remembered between runs.

Everything is stored in a local SQLite database at `%LOCALAPPDATA%\smsmanager.db`. Nothing is
sent anywhere except through your modem, to your carrier.

---

## Things that cost days to work out

If you are building something similar against `Windows.Devices.Sms`, these are the walls. Each
one presents as either silence or a bare error code.

### Receiving needs `Peek`, not `AcceptImmediately`

`SmsFilterActionType.AcceptImmediately` means *consume this message exclusively, ahead of every
other app*. Windows reserves it for the system's default messaging handler and denies everyone
else with `0xD0000022` (`STATUS_ACCESS_DENIED`) — regardless of capabilities or packaging.

`Peek` is permitted and non-destructive: this app sees each message and the built-in app still
gets its own copy. `Accept` also works. The compatibility checker prints all three.

### A sideloaded unsigned MSIX cannot receive SMS at all

Every registration returns `0xD0000022` and `SmsMessageRegistration.AllRegistrations` returns
`0x80070005`, even though the installed manifest carries `cellularMessaging`, `mobileBroadband`,
`internetClient` and `runFullTrust`, and Microsoft's own capability check passes. A brand-new,
never-used registration id is denied too, so it is not a collision.

The **unpackaged build registers successfully every time on the same machine, at the same
moment**. That is why this app ships unpackaged. Whether a properly signed Store build with an
approved `cellularMessaging` capability behaves differently is untested.

### Unpackaged apps need the runtime's DDLM package, not just the framework

A machine can have `Microsoft.WindowsAppRuntime.1.6` installed and still fail with `0x80670016`
— no window, no crash dialog, nothing in any log. The missing piece is the **DDLM** package,
which ships separately:

```powershell
$dir = "<nuget>\microsoft.windowsappsdk\<version>\tools\MSIX\win10-x64"
Add-AppxPackage -Path "$dir\Microsoft.WindowsAppRuntime.Singleton.1.6.msix"
Add-AppxPackage -Path "$dir\Microsoft.WindowsAppRuntime.DDLM.1.6.msix"
```

This build sets `WindowsAppSDKSelfContained`, so it carries the SDK and sidesteps the problem.

### Notifications are dropped silently without a display name

`AppNotificationManager.Default.Register()` — the parameterless overload — registers the AUMID
with only a `NotificationGUID`. Windows then accepts `Show()` **without raising anything** and
discards the banner, because it has no name to attribute the notification to. The app never
appears under Settings → Notifications either. The log said *notification shown* and the screen
stayed empty.

```csharp
AppNotificationManager.Default.Register("Your App Name", iconUri);   // this is the fix
```

### Timestamps must be stored in UTC

Outgoing rows were stamped with `UtcNow` (+00:00) while inbound rows carried the modem's local
timestamp (+07:00). Every ordering here is a text comparison on ISO strings, and text comparison
ignores the offset — so a message sent at 11:16 local, stored as `04:16+00:00`, sorted *before*
one received at 09:18 stored as `09:18+07:00`. Threads looked correct in each direction and
wrong when interleaved.

### The taskbar icon needs `altform-unplated` assets

Without them Windows draws the logo on a plate filled with the manifest's `BackgroundColor`. A
blue logo on the default accent colour is invisible; setting the colour to black gives a black
square on the taskbar. The fix is `Square44x44Logo.targetsize-N_altform-unplated.png`.

### WinUI 3 windows do not inherit the executable's icon

`<ApplicationIcon>` covers Explorer and the taskbar. The window's own title bar shows a generic
placeholder until you call `AppWindow.SetIcon()`.

### XamlCompiler can fail with no diagnostic at all

A bad construct produces a bare `MSB3073 … XamlCompiler.exe … exited with code 1` with no
message anywhere — `output.json` contains only timing markers. The target line number tells you
which pass died: `…interop.targets(590,…)` is pass 1, `(760,…)` is pass 2 (XBF generation).
Then bisect. Two confirmed causes here: `FontSize` set on a `Grid` (WinUI has no inherited
`TextElement.FontSize` the way WPF does), and the Community Toolkit sizer controls, which
compile in pass 1 and kill pass 2.

---

## Limitations

- **Voice calling is out of scope.** The L850-GL reports "no voice" over MBIM, exposes no audio
  interface and no AT serial port, and Windows desktop has no supported API for a third-party
  app to place cellular calls. It would need AT commands plus a modem-provided USB audio
  interface — a different architecture.
- **The app must be running to receive.** There is no background activation for SMS in this app
  model; `SmsMessageReceivedTrigger` cannot be registered without a manifest-declared background
  task entry point, which a .NET WinUI 3 desktop app does not produce. Starting at sign-in and
  living in the tray is the supported shape.
- **A dropped receive registration is polled for, not signalled.** Nothing is raised when a
  registration dies, so a 30-second health check re-registers when it disappears. The failure it
  guards against — a modem reset killing the registration in place — has not been reproduced on
  demand.
- **Deleting a message deletes the record, not a transmission** already handed to the network.
- The build is unsigned, so SmartScreen will warn on first run.

---

## Building

```bat
dotnet build Message_T480s.sln -c Debug -p:Platform=x64
```

| Project | What it is |
|---|---|
| `Message_T480s.csproj` | Core library: modem access, send queue, SQLite storage |
| `WinUI/` | The desktop app |
| `Cli/` | A console harness for the same core, useful for debugging the modem |
| `Tools/SmsCompatibilityCheck/` | The read-only hardware check |

A Windows TFM (`net8.0-windows10.0.19041.0`) is required: `Windows.Devices.Sms` is only
projected into managed code when the target framework carries a Windows platform version.

Note that the app locks its own executable while running; `build.cmd` stops it first, otherwise
the build fails with MSB3027.

The MSIX project still builds (`scripts\build-winui-msix.cmd`) but, per the finding above, the
resulting package cannot receive.

---

## Contributing

Reports from other modems and laptops are the most useful contribution — paste the output of
`scripts\check-compatibility.cmd` (identifiers are masked) along with the model.

## Licence

MIT — see [LICENSE](LICENSE).
