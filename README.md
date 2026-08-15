# SMSer

Group texts, easier.

Paste a roster — however garbled it came off a phone — and get back a link and a QR code
that open a group message with everyone already in the To: field. Every list is saved
under a short URL you can come back to, edit and regenerate.

Rewritten from the original Next.js app as a .NET 10 / ASP.NET Core solution, on the same
shape as TemprBac: Aspire app host, Razor Pages, Azure Storage, `ServiceDefaults` for
telemetry, health checks and security headers.

## Layout

| Project | What it is |
| --- | --- |
| `src/Smser.AppHost` | .NET Aspire app host. Starts Azurite and the web app together for local dev. |
| `src/Smser.Web` | The site. Razor Pages, QR generation, rate limiting. |
| `src/Smser.Library` | Parser, `sms:` link builder, short ids, Table Storage access. No ASP.NET dependency. |
| `src/Smser.ServiceDefaults` | OpenTelemetry, health checks, `/alive`, `/version`, response security headers. |
| `src/Smser.Tests` | MSTest, on Microsoft.Testing.Platform. Mostly the parser. |
| `samples/` | Sample rosters you can paste into the running app. Each is checked against an expected result on every build. |

---

## Getting up and running

### 1. Install the prerequisites

| | Why | Check it worked |
| --- | --- | --- |
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | Everything. | `dotnet --version` → `10.0.x` |
| [Docker Desktop](https://www.docker.com/products/docker-desktop/) | The app host runs Azurite (the Azure Storage emulator) in a container. | `docker version` |

Docker is only needed for the app-host path. See [running without Docker](#running-without-docker)
if you would rather not install it.

```powershell
dotnet --version
docker version --format '{{.Server.Version}}'
```

### 2. Clone and restore

```powershell
git clone https://github.com/nhudacin/smser.git
cd smser
dotnet restore src/Smser.slnx
```

### 3. Build

```powershell
dotnet build src/Smser.slnx
```

### 4. Run the tests

```powershell
dotnet test --solution src/Smser.slnx
```

`--solution` is required — `dotnet test` on this repo goes through Microsoft.Testing.Platform
(set in `global.json`), which wants the flag rather than a bare path. To run just one
project, point at the project instead:

```powershell
dotnet test src/Smser.Tests/Smser.Tests.csproj
```

### 5. Run the app

Make sure Docker Desktop is actually running, then:

```powershell
dotnet run --project src/Smser.AppHost
```

Two things come up:

| | URL |
| --- | --- |
| The site | <http://localhost:5200> |
| Aspire dashboard (logs, traces, resource health) | <http://localhost:15200> |

The dashboard prints a login URL with a token on it in the console — follow that link
rather than the bare address, or you will be asked for the token.

`Ctrl-C` in the console stops the web app and the Azurite container together.

### 6. Try it

1. Open <http://localhost:5200> and click **Start a new list**.
2. Give it a name.
3. Open any file in [`samples/`](samples/) and paste it into **Roster import** —
   `samples/messy-mixed.txt` is the interesting one:

   ```text
   Carpool list, pasted together from three different places.

   Alex 219-555-0113 / Sam 312.555.0147
   home 415 555 0199, cell 213-555-0188
   Chris O 310-555-0166 x204
   Pat Doe 650-555-0143 -- do not text before 9am
   Duplicate on purpose: (219) 555-0113
   Two smashed together: 21055501333125550152
   Not numbers: order 4500123789, zip 46360, 16 seats
   ```

4. Press **Import**. It should find eight numbers — several per line, the extension
   dropped, the duplicate collapsed, the run-together pair split, and the order number
   and zip ignored.
5. Press **Generate**. You land on `/new/{id}` with the QR code and the mobile link, and
   that URL is now a permanent link to the list.

[`samples/README.md`](samples/README.md) describes the rest. They cover phone
"copy as text" dumps, forwarded email threads, OCR runs with the separators lost,
non-NANP numbers, things that only look like numbers, and one list long enough to exceed
what a QR code can hold. Each is paired with an `.expected` file and checked on every
build, so they cannot drift away from what the parser does.

### Running without Docker

The web project runs on its own against a locally installed
[Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite):

```powershell
npm install -g azurite
azurite            # leave this running in its own terminal
```

then, in a second terminal:

```powershell
dotnet run --project src/Smser.Web
```

The site comes up on <http://localhost:5200> as before, with no Aspire dashboard.
`appsettings.Development.json` points the storage client at `UseDevelopmentStorage=true`
for this path; under the app host that value is overridden from the environment, so both
ways work without editing anything.

### Where the data goes

Rosters live in an Azure Table called `rosters`. Under the app host, Azurite keeps that
table in a named Docker volume (`smser-azurite`), so saved links survive a restart. To
start from an empty table:

```powershell
docker volume rm smser-azurite
```

### Troubleshooting

| Symptom | Cause |
| --- | --- |
| `docker: error during connect` on startup | Docker Desktop is not running. |
| Site loads, saving a roster errors | Azurite is still starting. The app host waits for it, so this mostly happens on the standalone path — check the `azurite` terminal. |
| `dotnet test` says to use `--solution` | You passed the `.slnx` positionally. See step 4. |
| Port 5200 or 15200 already in use | Change `applicationUrl` in the relevant `Properties/launchSettings.json`. |
| Copy-link button does nothing | `navigator.clipboard` needs a secure context. It is hidden on plain-http origins that are not localhost — e.g. hitting your dev machine from a phone by LAN IP. |

---

## How the parsing works

`PhoneNumberParser` is the app, and it does two passes over the pasted text:

1. **Separated numbers** — `(219) 555-0113`, `219.555.0113`, `+1 219 555 0113`,
   `12195550113`. A regex with digit-boundary guards on both ends, so a match is always a
   whole run of digits rather than a window into a longer one.
2. **Run-together digits** — `21955501133125550147`, which is what OCR produces when it
   loses the separators between two contact rows. Pass 1 deliberately cannot match these,
   so pass 2 takes them — but only splits a run if it divides into valid numbers
   *exactly*, with nothing left over.

Numbers are checked against real NANP rules (area code and exchange both start 2–9,
neither is an N11 service code), normalised to `1` + ten digits, deduplicated, and
returned in the order they appeared.

The all-or-nothing rule in pass 2 is the one worth knowing about. The obvious alternative
— walk left to right, skip a digit when nothing fits — turns `00721955501133125550147`
into a structurally valid number that could belong to anyone: real, textable, and
indistinguishable downstream from a number the user meant. Dropping the run instead
leaves a visible gap the user can fix.

Scope is NANP only. A parser loose enough to accept arbitrary international formats
accepts most of the surrounding junk too.

**Every phone number in this repo — tests, docs, placeholders — is in `555-0100`–`555-0199`,
the block NANPA reserves for fictional use.** Please keep it that way; this is a public
repo for an app whose whole subject is other people's phone numbers.

## Notes

- **Ids are lowercase.** Routing runs with `LowercaseUrls`, which lowercases generated
  paths *including route parameter values*, so `ShortId` uses a single-case alphabet.
  Eight characters of 36 is 41.4 bits — the same keyspace the original got from seven
  mixed-case ones.
- **A roster is only as private as its link.** There is no sign-in. That is what the
  id length and the rate limiter are sized against.
- **QR codes are generated per request**, not stored. The original kept a base64 PNG in
  storage next to the numbers, which made every saved list expensive to hold and
  impossible to fix once a rendering bug shipped.
- **Lists over ~240 numbers have no QR code.** That is the capacity of a version-40
  symbol at error correction L. The page shows the link and says so.
- **No inline styles or scripts.** The CSP in `ServiceDefaults` carries no
  `'unsafe-inline'`, so everything lives in `wwwroot/css/site.css` and
  `wwwroot/js/site.js`. Import and Generate are real form posts and work without
  JavaScript; the only script on the page is the copy-link button.

## Deploying to Azure

Not wired up yet. The pieces that are in place for it: `/alive` for the health probe,
`/version` (the commit SHA, from `dotnet publish -p:SourceRevisionId=<sha>`) so a deploy
smoke test can tell the new build from the one it replaces, HSTS and forwarded-headers
configuration for running behind a reverse proxy, and an app host whose storage resource
targets a real account when published rather than the emulator.
