# Sample rosters

Test data for the importer. Each `.txt` is a realistic paste — open one, copy it into the
**Roster import** box on `/new`, and press Import. Each has a `.expected` file beside it
listing the normalised numbers the parser should find, in order; an empty `.expected`
means the file should yield nothing at all.

`Smser.Tests/SampleRosterTests.cs` runs every pair on each build, so these cannot drift
away from what the parser actually does. **Adding a case is two files and no code.**

| File | What it covers |
| --- | --- |
| `clean-roster.txt` | The easy case — one well-formed number per line. |
| `same-number-many-formats.txt` | One number written fifteen ways, including unmatched parentheses. All fifteen collapse to a single entry. |
| `phone-contacts-copy-as-text.txt` | A phone's contact list where labels and numbers land on separate lines. Two numbers for the same person are both kept. |
| `roster-with-jersey-numbers.txt` | Numbers surrounded by other numbers — jersey numbers, dates, field and gate numbers, game counts. |
| `group-email-thread.txt` | A forwarded email with headers, a signature, prose, and a number mentioned parenthetically as dead. |
| `messy-mixed.txt` | Several numbers per line, an extension, a deliberate duplicate, a run-together pair, and non-numbers alongside. |
| `ocr-run-together.txt` | Digit runs with the separators lost. Includes two that are deliberately **not** split — see below. |
| `international-and-invalid.txt` | Non-NANP numbers and structurally impossible ones. Yields nothing. |
| `not-phone-numbers.txt` | Order numbers, tracking numbers, invoices, addresses, zips, serials, IPs, a unix timestamp. Yields nothing. |
| `too-big-for-a-qr-code.txt` | 250 numbers, past the ~240 a QR symbol holds. The page should save fine, show the link, and say the list is too long for a code. |

## Two cases worth understanding

**`ocr-run-together.txt` drops two of its five runs on purpose.**
`00721955501133125550147` has a row number glued to the front and does not divide into
whole numbers; `219555011312` is twelve digits, which is neither one number nor two. The
parser only splits a run when it tiles *exactly*. Guessing instead would emit a
structurally valid number that could belong to anyone — saved, and then texted.

**`not-phone-numbers.txt` and `international-and-invalid.txt` must stay empty.**
These are the regression tests for false positives, which are the expensive failure here.
A missed number is visible in the box and easy to fix; an invented one is not.

## Rule for new samples

Every number must be in **`555-0100`–`555-0199`**, the block NANPA reserves for fictional
use. Any area code is fine. `SampleRosterTests` enforces this — a sample that yields a
number outside that range fails the build. This is a public repo for an app about other
people's phone numbers; please keep it that way.
