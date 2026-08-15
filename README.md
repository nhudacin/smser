<div align="center">

# 📱 SMSer

### Group texts, easier.

Paste a roster — however garbled it came off your phone — and get back a link and a
QR code that open a group message with everyone already in it.

[![CI](https://github.com/nhudacin/smser/actions/workflows/ci.yml/badge.svg)](https://github.com/nhudacin/smser/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Aspire](https://img.shields.io/badge/.NET%20Aspire-13.4-8A2BE2?logo=dotnet&logoColor=white)](https://learn.microsoft.com/dotnet/aspire/)
[![Azure](https://img.shields.io/badge/Azure-Table%20Storage-0078D4?logo=microsoftazure&logoColor=white)](https://learn.microsoft.com/azure/storage/tables/)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

[Quick start](#-quick-start) · [How it works](#-how-the-parser-works) · [Architecture](#%EF%B8%8F-architecture) · [Samples](samples/) · [Testing](#-testing) · [Contributing](CONTRIBUTING.md)

<img src="docs/images/result.png" alt="A saved SMS group in SMSer, showing the parsed phone numbers on the left and a QR code with a mobile link on the right" width="860">

</div>

---

## 🤔 Why

You coach a team, run a carpool, or organise a group of parents. You have everyone's
number — on a printed sheet, in a screenshot, in a forwarded email, or scattered through
your phone's contacts. You want one group text.

Getting there means typing thirty numbers into the To: field without a typo. Your phone's
"copy as text" gives you something like this:

```text
#7    Sam Chen         312-555-0147
#12   Alex Rivera      219-555-0113
Practice Tue/Thu 6:00pm, Field 3, Gate 12
```

SMSer takes that, finds the phone numbers in it, ignores the jersey numbers and the gate
number and the time, and hands back a QR code. Scan it, and the messaging app opens with
the whole roster already filled in.

## ✨ Features

| | |
|---|---|
| **📷 Photo import** | Take a picture of the roster, or drop one in. The text is read **on your device** — the photo is never uploaded. |
| **Paste anything** | Names, jersey numbers, dates, zips, e-mail addresses, OCR noise. The importer finds the phone numbers and ignores the rest. |
| **Every format** | `(219) 555-0113`, `219.555.0113`, `+1 219 555 0113`, `12195550113`, and a dozen more all collapse to one entry. |
| **Refuses to guess** | Numbers are checked against real NANP rules. A run of digits is only split when it divides into whole numbers *exactly* — a wrong number here gets texted to a stranger. |
| **QR code or link** | Scan the code, or tap the mobile link. Both open a group message with everyone in the To: field. |
| **Shareable, editable** | Every list gets its own short URL. Come back next season, fix a number, regenerate — the link stays the same. |
| **Works without JavaScript** | Import and Generate are real form posts. The only script on the page is the copy-link button. |
| **Light and dark** | Theme-aware, and sized for the phone you are actually holding at the game. |
| **Locked down** | Strict CSP with no `unsafe-inline`, rate limiting, security headers, and no sign-in to leak. |

## 📸 Screenshots

<table>
<tr>
<td width="50%" valign="top">

**The front page**

<img src="docs/images/home.png" alt="The SMSer home page, with the headline 'Group texts, easier', a 'Start a new list' button, and three feature cards">

</td>
<td width="50%" valign="top">

**Paste the mess, press Import**

<img src="docs/images/import.png" alt="The new-group form with garbled roster text pasted in and a notice reading 'Found 8 numbers'">

Eight numbers found across six lines — two on one line, a duplicate collapsed, an
extension dropped, an order number and a zip ignored.

</td>
</tr>
<tr>
<td valign="top">

**Photograph the roster**

<img src="docs/images/photo-mobile.png" alt="The photo import control on a phone, offering 'Take a photo' and 'Choose a photo' buttons" width="330">

On a phone the camera button opens the rear camera directly. OCR runs on the device.

</td>
<td valign="top">

**Reading it**

<img src="docs/images/photo-reading.png" alt="The photo import control showing a thumbnail of the roster and a progress bar reading 'Loading the reader'">

</td>
</tr>
<tr>
<td valign="top">

**Dark mode, same page**

<img src="docs/images/result-dark.png" alt="A saved group rendered in dark mode, with the QR code still on a white plate">

The QR keeps its white plate in dark mode — inverting it would make it unreadable to
every camera.

</td>
<td valign="top">

**On the phone it is used from**

<img src="docs/images/mobile.png" alt="A saved group on a narrow mobile viewport, with the QR code and buttons stacked" width="300">

</td>
</tr>
</table>

## 📷 Reading a photo

Take a picture of the roster — or drop one in on a desktop — and the text goes straight
into the import box, which then runs the same parser everything else does.

**The OCR runs in your browser**, on a WebAssembly build of Tesseract served from this
app's own origin. That is a deliberate choice, not a convenience one. A photo of a roster
is a photo of thirty people's phone numbers; the usual approach — POST it to a cloud
vision API — hands exactly that to a third party for every list anyone ever makes. Keeping
it on the device also means the feature works offline, costs nothing to run, and needs no
API key to try locally.

The trade is size: the engine is about 5 MB. It is fetched **on first use**, not on page
load, so anyone who never touches the photo control never downloads it, and the browser
caches it afterwards.

| | |
|---|---|
| **On a phone** | The camera button opens the rear camera directly (`capture="environment"`). |
| **On a desktop** | Drag a photo onto the drop zone, or pick one. The camera button is hidden, because `capture` does nothing there. |
| **Before recognition** | The image is scaled to 2000px on its long edge and EXIF rotation is applied, so a portrait phone photo does not reach the OCR sideways. |
| **After** | The text is *appended* to the import box and Import runs automatically — photographing page two does not wipe page one. |
| **Without JavaScript** | The whole control stays hidden. Pasting still works. |

OCR output is messy by nature, which suits this app: the parser was already built to find
numbers in garbled text and ignore the rest. What comes out of a photo is exactly the kind
of input [`samples/`](samples/) is full of.

## 🧠 How the parser works

`PhoneNumberParser` is the app. It runs two passes over the pasted text, because the input
splits into two very different shapes.

```mermaid
flowchart LR
    paste["<b>Pasted text</b><br/><i>names · dates · zips<br/>e-mails · jersey numbers<br/>phone numbers</i>"]

    paste --> p1["<b>Pass 1</b><br/>separated numbers<br/><i>(219) 555-0113<br/>+1 219 555 0113</i>"]
    paste --> p2["<b>Pass 2</b><br/>runs of 12+ digits<br/><i>21955501133125550147</i>"]

    p2 --> tile{"Divides into<br/>whole numbers<br/><b>exactly</b>?"}
    tile -->|yes| split["Split"]
    tile -->|no| drop["<b>Drop the run</b><br/><i>a visible gap beats a<br/>plausible wrong number</i>"]

    p1 --> nanp["<b>NANP rules</b><br/><i>area code and exchange<br/>start 2-9, neither is N11</i>"]
    split --> nanp

    nanp --> norm["<b>Normalise · dedupe</b><br/><i>1 + ten digits<br/>first appearance wins</i>"]
    norm --> out["<b>sms://open?addresses=…</b><br/><i>then a QR code</i>"]

    style drop fill:#fee2e2,stroke:#ef4444,color:#7f1d1d
    style out fill:#dcfce7,stroke:#22c55e,color:#14532d
```

**The all-or-nothing rule in pass 2 is the part worth understanding.** The obvious
implementation walks left to right and skips a digit when nothing fits. Given
`00721955501133125550147` — a roster line whose row number got glued to the front — that
version skips the two zeros, finds a structurally valid ten-digit window, and emits it. The
result is a real number that could belong to anyone, saved and then texted, and nothing
downstream can tell it apart from a number you meant. Requiring an exact tiling removes
that entire class of answer.

Scope is NANP only (US, Canada, Caribbean). A parser loose enough for arbitrary
international formats accepts most of the surrounding junk too.

## 🏗️ Architecture

```mermaid
flowchart TB
    user(["📱 Phone or browser"])
    user --> rate

    subgraph web["<b>Smser.Web</b> · ASP.NET Core Razor Pages"]
        direction TB
        rate["<b>Rate limiter</b><br/><i>+ security headers</i>"]
        pages["<b>Pages</b><br/><i>/ · /new · /new/:id</i>"]
        qr["<b>QrCodeGenerator</b><br/><i>encodes the sms: link as a<br/>PNG, inlined as a data: URI</i>"]
        rate --> pages
        pages --> qr
    end

    subgraph lib["<b>Smser.Library</b> · no ASP.NET dependency"]
        direction LR
        parser["<b>PhoneNumberParser</b><br/><i>two passes, NANP rules</i>"]
        link["<b>SmsLink</b>"]
        store["<b>SmsGroupStore</b>"]
        ids["<b>ShortId</b><br/><i>8 chars · 41.4 bits</i>"]
        parser --> link
        store --> ids
    end

    pages --> parser
    pages --> store
    store --> table[("<b>Azure Table Storage</b> · rosters<br/><i>Azurite in a container when local</i>")]

    style web fill:#eef2ff,stroke:#818cf8
    style lib fill:#f0fdf4,stroke:#4ade80
```

The parser, the link builder, the ids and the storage contract all live in
`Smser.Library`, which takes no ASP.NET dependency — so the roster round-trip is testable
without a host, and a future worker or CLI can reference the same code.

Two projects sit alongside and are left off the diagram because they wire things up rather
than serve requests: **`Smser.AppHost`** is the .NET Aspire host that starts Azurite and
the web app together for local development, and **`Smser.ServiceDefaults`** carries the
OpenTelemetry setup, the health checks behind `/alive` and `/version`, and the
Content-Security-Policy.

### What a paste actually does

```mermaid
sequenceDiagram
    autonumber
    actor U as You
    participant W as Smser.Web
    participant P as PhoneNumberParser
    participant T as Azure Table

    U->>W: paste roster, press Import
    W->>P: Parse(pasted text)
    P-->>W: 8 normalised numbers
    W-->>U: numbers in the box — nothing saved yet
    Note over U,W: edit freely — hand edits are re-checked
    U->>W: press Generate
    W->>P: Parse(the numbers box)
    W->>T: insert under a new short id
    W-->>U: redirect to /new/{id}
    U->>W: GET /new/{id}
    W-->>U: QR code + sms: link + share link
```

## 🚀 Quick start

### Prerequisites

| | Why | Check |
|---|---|---|
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | Everything. | `dotnet --version` → `10.0.x` |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | The app host runs Azurite, the Azure Storage emulator, in a container. | `docker version` |

Docker is only needed for the app-host path — see [without Docker](#without-docker).

### Run it

```bash
git clone https://github.com/nhudacin/smser.git
cd smser

dotnet restore src/Smser.slnx
dotnet build   src/Smser.slnx
dotnet run --project src/Smser.AppHost
```

| | URL |
|---|---|
| 🌐 The site | <http://localhost:5200> |
| 📊 Aspire dashboard (logs, traces, health) | <http://localhost:15200> |

> The dashboard prints a login URL with a token on it in the console — follow that link
> rather than the bare address, or it will ask you for the token.

`Ctrl-C` stops the web app and the Azurite container together.

### Try it

1. Open <http://localhost:5200> and click **Start a new list**.
2. Give it a name.
3. Open [`samples/messy-mixed.txt`](samples/messy-mixed.txt) and paste it into **Roster import**.
4. Press **Import** — it should find eight numbers.
5. Press **Generate**. You land on `/new/{id}` with the QR code, and that URL is now a
   permanent link to the list.

[`samples/`](samples/) has ten more, covering phone contact dumps, forwarded email
threads, OCR runs with the separators lost, non-NANP numbers, things that only look like
numbers, and one list long enough to exceed what a QR code can hold.

### Without Docker

Run the web project on its own against a locally installed
[Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite):

```bash
npm install -g azurite
azurite                             # leave running in its own terminal
dotnet run --project src/Smser.Web  # in a second terminal
```

`appsettings.Development.json` points the storage client at `UseDevelopmentStorage=true`
for this path. Under the app host that value is overridden from the environment, so both
ways work without editing anything.

### Where the data goes

Rosters live in an Azure Table called `rosters`. Under the app host, Azurite keeps it in a
named Docker volume so saved links survive a restart. To start from an empty table:

```bash
docker volume rm smser-azurite
```

### Troubleshooting

| Symptom | Cause |
|---|---|
| `docker: error during connect` on startup | Docker Desktop is not running. |
| Site loads, saving a roster errors | Azurite is still starting. The app host waits for it, so this is mostly the standalone path — check the `azurite` terminal. |
| `dotnet test` says to use `--solution` | You passed the `.slnx` positionally. See [Testing](#-testing). |
| Port 5200 or 15200 already in use | Change `applicationUrl` in the relevant `Properties/launchSettings.json`. |
| Copy-link button does nothing | `navigator.clipboard` needs a secure context. It is hidden on plain-http origins that are not localhost — e.g. reaching your dev machine from a phone by LAN IP. |

## 🧪 Testing

```bash
dotnet test --solution src/Smser.slnx
```

`--solution` is required — tests run through Microsoft.Testing.Platform (opted in via
`global.json`), which wants the flag rather than a bare path.

| Suite | What it covers |
|---|---|
| `PhoneNumberParserTests` | Every format, numbers buried in prose, run-together digits, and a long list of things that must **not** parse. |
| `SampleRosterTests` | Every file in [`samples/`](samples/) against its `.expected` result, plus a guard that no sample yields a number outside the reserved range. |
| `NewPageFormWiringTests` | Boots the real app and asserts on rendered HTML — the form's `action` and the buttons' `formaction`. |
| `PhotoImportWiringTests` | That the photo control ships hidden, asks for the rear camera, every vendored asset is served, and the CSP still permits the engine to compile. |
| `ShortIdTests` · `SmsLinkTests` · `QrCodeGeneratorTests` | Id keyspace and validation, link format, QR rendering and its capacity ceiling. |

The whole suite is self-contained: nothing reaches storage, and the page-wiring tests boot
the web app in-process. **No Docker, no Azurite.** CI runs exactly this on every pull
request, in Release, with `-warnaserror`.

> **Why a test renders HTML.** Generate once silently re-ran the Import handler, because a
> bare `<form method="post">` posts to the current document URL — which after Import was
> `/new?handler=Import`. Nothing was saved and no QR appeared, and every unit test passed
> straight through it. `NewPageFormWiringTests` exists so that cannot happen twice.

## 📁 Project layout

| Path | Role |
|---|---|
| `src/Smser.AppHost` | .NET Aspire host. Starts Azurite and the web app together for local dev. |
| `src/Smser.Web` | The site. Razor Pages, QR generation, rate limiting. |
| `src/Smser.Library` | Parser, `sms:` link builder, short ids, Table Storage. No ASP.NET dependency. |
| `src/Smser.ServiceDefaults` | OpenTelemetry, health checks, `/alive`, `/version`, response security headers. |
| `src/Smser.Tests` | MSTest on Microsoft.Testing.Platform. |
| `src/Smser.Web/wwwroot/lib/tesseract` | Vendored OCR engine. See [its README](src/Smser.Web/wwwroot/lib/tesseract/README.md) for versions and why it is committed. |
| `samples/` | Sample rosters, each checked against an expected result on every build. |

**Stack:** .NET 10 · ASP.NET Core Razor Pages · .NET Aspire · Azure Table Storage ·
QRCoder · Tesseract (WebAssembly, in-browser) · MSTest · GitHub Actions.

## 🔒 Privacy and security

This is an app about other people's phone numbers, so a few things are deliberate:

- **A roster is only as private as its link.** There is no sign-in. Ids are 8 characters
  of a 36-character alphabet — 41.4 bits — and the endpoint is rate limited. That is what
  makes scanning for other people's lists impractical.
- **Every phone number in this repo is fictional.** All of them are in `555-0100`–`555-0199`,
  the block [NANPA reserves](https://nationalnanpa.com/) for fictional use, and
  `SampleRosterTests` fails the build if a sample ever yields one outside it.
- **Photos never leave the device.** The OCR engine is WebAssembly served from this
  origin and runs in the browser. No photo is uploaded, stored, or sent to a vision API.
- **Nothing is stored that does not need to be.** The QR code is regenerated per request
  rather than kept in storage. There are no analytics and no third-party scripts.
- **Strict CSP with no `unsafe-inline`.** All styling is in `site.css` and all behaviour in
  `site.js`, so the policy is one the markup actually honours.
- **CI audits dependencies** for known advisories, transitive ones included, on every PR.

Found something? See [SECURITY.md](SECURITY.md).

## 🗺️ Roadmap

- [ ] **Deploy to Azure.** The hooks are in place — `/alive` for the health probe,
      `/version` (the commit SHA, from `dotnet publish -p:SourceRevisionId=<sha>`) so a
      smoke test can tell the new build from the one it replaces, HSTS and
      forwarded-headers config for running behind a reverse proxy, and an app host whose
      storage resource targets a real account when published. There is no infra or
      pipeline yet.
- [ ] **Retention.** Rosters are kept forever today. `CreatedTs` exists so a sweep has
      something to sort on.
- [ ] **Non-NANP numbers**, if anyone actually wants them.

## 🤝 Contributing

Issues and pull requests are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md). The short
version: `dotnet test --solution src/Smser.slnx` should pass, and any phone number you add
belongs in `555-0100`–`555-0199`.

## 📄 License

[MIT](LICENSE) © Nick Hudacin
