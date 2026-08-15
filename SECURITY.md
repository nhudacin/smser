# Security Policy

## Reporting a vulnerability

**Please do not open a public issue.**

Use GitHub's private reporting:
[**Report a vulnerability**](https://github.com/nhudacin/smser/security/advisories/new).

That opens a private advisory only the maintainer can see. Include what you found, how to
reproduce it, and what an attacker gets out of it. You should get a first response within
a week; this is a side project, not a staffed product, so please size your expectations to
that.

## Supported versions

`main` only. There are no released versions and no backports.

## What this app is exposed to

Worth stating plainly, because it shapes what counts as a vulnerability here.

- **There is no sign-in and no user accounts.** A saved roster is protected by the
  unguessability of its URL and nothing else. Ids are 8 characters of a 36-character
  alphabet (~41.4 bits, 2.8e12 ids) and the endpoint is rate limited per caller.
- **Rosters contain phone numbers.** That is the data worth protecting. Anything that
  lets one visitor read another's roster is the highest-severity class of bug here —
  enumeration of ids, a cache that leaks across requests, an id that turns out to be
  predictable.
- **Anonymous writes.** Saving a roster is unauthenticated and writes to storage. It is
  rate limited, and roster size is capped, but there is no captcha.
- **Photo import runs entirely in the browser.** The Tesseract WebAssembly engine is
  served from this origin and no photo is ever uploaded. Anything that causes an image
  to leave the device is in scope and serious.
- **No third-party scripts, fonts, analytics, or CDNs.** Everything is served from the
  app's own origin under a Content-Security-Policy with no `'unsafe-inline'`.

## In scope

- Reading, listing, or enumerating rosters you did not create
- Id generation that is predictable or lower-entropy than claimed
- XSS, CSRF, header injection, or anything that defeats the CSP
- Anything that causes a photo, or text read from one, to be transmitted off the device
- Parser behaviour that produces a phone number **not present in the input** — a wrong
  number gets texted to a stranger, which is the worst outcome this app has
- Denial of service that a single caller can cause within the rate limits

## Out of scope

- Missing rate limits or headers on the local development configuration
- Findings that require an already-compromised machine or browser
- Reports from automated scanners with no demonstrated impact
- The absence of authentication — that is a design decision, documented above
- Anything about `smser.vercel.app`, which is a stale deployment of the old Next.js app
  and is not built from this code

## Credentials

There are none in this repository, and there never should be. `.gitignore` covers `.env`,
`appsettings.local.json`, and `appsettings.*.local.json`. Local development uses the Azure
Storage emulator via `UseDevelopmentStorage=true`, which carries no secret.

If you ever find a credential committed here, report it privately as above and treat it as
live regardless of how it looks.
