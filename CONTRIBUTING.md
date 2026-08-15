# Contributing to SMSer

Thanks for taking a look. This is a small project with a narrow job, so the bar for
"is this in scope" is mostly: does it help someone turn a messy roster into a group text?

## Getting set up

Everything you need is in the [Quick start](README.md#-quick-start). The short version:

```bash
dotnet restore src/Smser.slnx
dotnet build   src/Smser.slnx
dotnet test    --solution src/Smser.slnx
dotnet run --project src/Smser.AppHost
```

The test suite needs neither Docker nor Azurite. Running the app does — or a locally
installed Azurite, see [without Docker](README.md#without-docker).

## Ground rules

**🔢 Every phone number you add must be fictional.** Use `555-0100`–`555-0199`, the only
block [NANPA reserves](https://nationalnanpa.com/) for fictional use. Any area code is
fine: `(219) 555-0113`, `(312) 555-0147`. `SampleRosterTests` fails the build if a sample
yields a number outside that range, and the same expectation applies to tests, comments,
placeholders and docs.

This repo is public and the subject of the app is other people's phone numbers. A
plausible-looking number in a test fixture is a real person's number to whoever owns it.

**✅ Tests pass.** `dotnet test --solution src/Smser.slnx`. CI runs the same thing in
Release with `-warnaserror`, so a new warning fails the build.

**🧪 Parser changes need cases.** If you change `PhoneNumberParser`, add the input that
motivated it. If it is a realistic paste, add it to [`samples/`](samples/) — two files,
no code:

```
samples/your-case.txt        the roster, exactly as someone would paste it
samples/your-case.expected   the normalised numbers it should yield, one per line
```

An empty `.expected` means "this must yield nothing", which is how the false-positive
regressions are written. `SampleRosterTests` picks new pairs up automatically.

**🚫 False positives are worse than misses.** A number the parser fails to find is visible
in the box and easy to fix. A number it invents gets saved and then texted to a stranger,
and nothing downstream can tell it apart from one the user meant. When a change trades one
for the other, it should trade in that direction.

**🎨 No inline styles or scripts.** The Content-Security-Policy carries no
`'unsafe-inline'`. Styling goes in `wwwroot/css/site.css`, behaviour in
`wwwroot/js/site.js`. An inline `style=` or `onclick=` will be dropped by the browser.

**📷 Photos stay on the device.** The OCR engine is vendored under
`src/Smser.Web/wwwroot/lib/tesseract` and runs in the browser. Please do not replace it
with a call to a cloud vision API: a photo of a roster is a photo of thirty people's
phone numbers, and not sending it anywhere is the point. Same reason there are no
third-party origins in the Content-Security-Policy.

`PhotoImportWiringTests` checks the wiring — hidden by default, rear camera requested,
assets served, CSP intact. It cannot check that OCR still *works*, so if you touch the
engine or `photo-ocr.js`, run it against a real photo of a roster by hand.

**♿ It has to work without JavaScript.** Import and Generate are real form posts. Script
is for enhancement only — today that is the copy-link button and nothing else.

## Commit and PR style

Commit messages explain **why**, not what — the diff already says what. Present tense,
first line under ~72 characters.

Pull requests get built and tested automatically. Describe what changed and what you did
to convince yourself it works; if you found a bug the tests missed, say why they missed it.

## Code comments

Comments in this codebase explain reasoning that is not recoverable from the code —
why an approach was rejected, what breaks if a line is removed, which constraint a
constant encodes. Comments that restate the line below them are noise. Follow the
surrounding style.

## Reporting a security issue

Please don't open a public issue. See [SECURITY.md](SECURITY.md).
