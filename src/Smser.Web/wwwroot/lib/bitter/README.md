# Vendored display face

[Bitter](https://fonts.google.com/specimen/Bitter) — the slab serif used for headlines,
the wordmark, the step numerals and the italic asides. Checked in rather than linked from
Google Fonts.

| File | What it is | Size |
|---|---|---|
| `bitter-latin.woff2` | upright, Latin | 34 KB |
| `bitter-latin-ext.woff2` | upright, Latin Extended | 32 KB |
| `bitter-italic-latin.woff2` | italic, Latin | 19 KB |
| `bitter-italic-latin-ext.woff2` | italic, Latin Extended | 18 KB |

Bitter v42, © the Bitter Project Authors, under the
[SIL Open Font License 1.1](https://openfontlicense.org/) — which permits redistribution
like this as long as the licence travels with it.

## Why these are committed

The Content-Security-Policy names no third-party origins. A Google Fonts `<link>` is an
external stylesheet that loads external font files, so it would need
`https://fonts.googleapis.com` in `style-src` and `https://fonts.gstatic.com` in
`font-src`. Serving the face from this origin means the policy needs no change at all, and
no visitor's browser tells a third party which page they are on — the same reasoning that
put the OCR engine in `../tesseract`.

## Why four files and not five

Bitter is a **variable** font. The 400, 600, 700 and 800 weights the design uses all come
out of one file per style, which is why the `@font-face` blocks in `site.css` declare
`font-weight: 400 800` rather than one block per weight. Asking Google Fonts for four
weights returns the same URL four times.

The two Latin subsets are both kept: roster names are user data, and a group called
"Kraków" should not fall back to Georgia mid-word.

## Updating

Ask the CSS API for the range the design uses, with a browser user agent so it answers
with woff2 rather than a legacy format:

```bash
curl -A "Mozilla/5.0 ... Chrome/120.0 Safari/537.36" \
  "https://fonts.googleapis.com/css2?family=Bitter:ital,wght@0,400;0,600;0,700;0,800;1,400&display=swap"
```

Take the `latin` and `latin-ext` `src` URLs for each style, download them here, and copy
the matching `unicode-range` values into `site.css` — they must stay in step with the
files or the browser will download a subset and then find the glyph missing.
