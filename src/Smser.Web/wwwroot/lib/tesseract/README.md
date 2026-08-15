# Vendored OCR engine

These files are the Tesseract OCR engine, checked in rather than fetched from a CDN.

| File | What it is | Size |
|---|---|---|
| `tesseract.min.js` | tesseract.js **7.0.0** — the API this app calls | 62 KB |
| `worker.min.js` | tesseract.js worker, loaded from this URL (not a blob) | 109 KB |
| `tesseract-core-simd-lstm.js` | tesseract.js-core **7.0.0** WebAssembly loader | 88 KB |
| `tesseract-core-simd-lstm.wasm` | the engine itself | 2.8 MB |
| `eng.traineddata.gz` | English language data, from [tessdata_fast](https://github.com/tesseract-ocr/tessdata_fast) | 1.9 MB |

All Apache 2.0. tesseract.js and tesseract.js-core are © the tesseract.js authors;
the language data is © Google and the Tesseract OCR contributors.

## Why these are committed

The Content-Security-Policy has no third-party origins in it, and a photo of a roster is
a photo of thirty people's phone numbers — the last thing it should do is travel to
somebody else's CDN. Serving the engine from this origin is what makes both true.

## Why only one core variant

`tesseract.js-core` ships eight builds: SIMD and non-SIMD, LSTM-only and full, each with a
WebAssembly and an asm.js flavour. Shipping all of them is ~30 MB. This is the SIMD LSTM
build, which every browser released since about 2021 can run, and `photo-ocr.js` pins
`corePath` at it so tesseract.js never goes looking for the others.

The asm.js fallbacks are deliberately absent: they exist for browsers with no WebAssembly
at all, which the app already requires.

## Why `tessdata_fast`

`tessdata_best` is roughly twice the size and slower, and its accuracy advantage is on
degraded scans and unusual typefaces. This is reading printed and handwritten-ish rosters
photographed in decent light, where the difference does not show up.

## Updating

```bash
npm i tesseract.js@<version>
cp node_modules/tesseract.js/dist/tesseract.min.js .
cp node_modules/tesseract.js/dist/worker.min.js .
cp node_modules/tesseract.js-core/tesseract-core-simd-lstm.{js,wasm} .
```

Language data comes from the `tessdata_fast` repository and is gzipped here; tesseract.js
decompresses it. Re-gzip with `gzip -9 -c eng.traineddata > eng.traineddata.gz`.

After updating, check the version table above and run the photo import against a real
photo — `PhotoImportWiringTests` proves the files are *served*, not that OCR still works.
