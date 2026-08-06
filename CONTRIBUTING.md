# Contributing

## The most useful thing you can send

**A compatibility report.** This app is developed against a single Fibocom L850-GL in a ThinkPad
T480s on one Vietnamese carrier. Whether it works on a Dell Latitude with an L860-GL on a German
network is genuinely unknown, and only you can find out.

```bat
scripts\check-compatibility.cmd
```

The checker is read-only — it never sends a message and never registers a filter that would
consume one — and it masks phone numbers and device identifiers. Open a
[compatibility report](../../issues/new?template=compatibility-report.yml) with its output.
Reports that it *did not* work are as valuable as reports that it did.

## Translations

The interface ships in 19 languages, and **only English and Vietnamese have been checked by a
speaker**. Every other pack is machine-assisted and marked `"reviewed": false`.

A language is one file — `WinUI/Strings/<code>.json` — read at runtime, so fixing or adding one
requires no code change and no C# at all. To add a language, copy `en.json`, translate the values
and fill in `_meta`: `code`, `name` written in the language's own script, and `rtl` if it is
written right to left.

The tests will tell you if a pack is missing a key, has an empty value, or uses `{0}`
placeholders that do not match the English original.

## Before opening a pull request

```bat
scripts\run-tests.cmd
```

No modem, SIM or administrator rights are needed. If you change how phone numbers are normalised,
add cases to `Tests/PhoneNumberTests.cs` — a normalisation change silently re-files people's
conversation history, and the tests are what keeps that honest.

To work on the interface without a WWAN module:

```bat
set SUPERDOC_SMS_DEMO=1
```

The app then uses a separate demo database of invented contacts and never opens the modem, so it
cannot interfere with a copy that is doing real work.

## Privacy in issues and pull requests

Logs, screenshots and database files contain **real phone numbers and message text** — yours and
other people's. Read anything through before you attach it, and redact what should not be public.
Screenshots for the documentation should be generated with `scripts\capture-screenshots.ps1`,
which uses invented contacts and numbers from the range reserved for fiction.

The subtler risk is source code. A phone number makes a natural-looking test case, and a real one
found its way into a test here exactly that way. To guard against it:

```bat
scripts\install-hooks.cmd
```

That installs a pre-push hook running `scripts\check-no-secrets.ps1`, which reads patterns from a
`.secret-patterns` file in the repository root — one identifier per line. **That file is
git-ignored and must stay that way**; committing the list would publish precisely what it exists
to keep out. A useful list is your own number, the numbers of anyone you have messaged, and the
SIM's IMEI, IMSI, ICCID and service centre address.

Test data should come from a range that can never be allocated: `+44 7700 900000–900999` (Ofcom,
UK) or `555-0100–555-0199` (NANP).

## Style

Match the surrounding code. Comments here explain *why* something is the way it is —
particularly where the reason is a Windows behaviour that cost days to discover — rather than
restating what the line does.

## Licence

Contributions are accepted under the [MIT licence](LICENSE).
