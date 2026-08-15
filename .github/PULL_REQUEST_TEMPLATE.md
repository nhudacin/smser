<!--
Thanks for the PR. Keep this short — a few honest sentences beat a filled-in form.
Delete any section that does not apply.
-->

## What this changes

<!-- And why. The diff already says what; the why is the part that gets lost. -->

## How you know it works

<!-- Tests you added, what you ran, what you clicked. If a bug slipped past the existing
     tests, say why they missed it — that is usually the more useful half. -->

## Checklist

- [ ] `dotnet test --solution src/Smser.slnx` passes
- [ ] Any phone number added is in `555-0100`–`555-0199` ([why](../CONTRIBUTING.md#ground-rules))
- [ ] Parser changes come with the input that motivated them, as a test or a `samples/` pair
- [ ] No inline styles or scripts (the CSP has no `'unsafe-inline'`)
- [ ] Still works with JavaScript disabled
