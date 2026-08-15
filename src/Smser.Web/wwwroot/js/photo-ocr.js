// Photo import: read a picture of a roster and drop the text into the import box.
//
// The OCR runs entirely in this browser, on WebAssembly served from this origin. That is
// a deliberate choice rather than a convenience one. A photo of a roster is a photo of
// thirty people's phone numbers, and the alternative — POST it to a cloud vision API —
// hands that to a third party for every list anyone ever makes. It also means this works
// offline, costs nothing to run, and needs no API key to try locally.
//
// The engine is ~5 MB, so it is fetched on first use rather than on page load. Someone
// who never touches the photo control never downloads it.
(function () {
    'use strict';

    var root = document.querySelector('[data-photo]');
    if (!root) return;

    // Everything here needs canvas, promises and file reading. Bail before revealing the
    // control if any of it is missing, so the fallback is a page without a camera button
    // rather than a camera button that throws.
    if (!window.Promise || !window.FileReader || !document.createElement('canvas').getContext) return;

    var zone = root.querySelector('[data-photo-zone]');
    var idle = root.querySelector('[data-photo-idle]');
    var busy = root.querySelector('[data-photo-busy]');
    var preview = root.querySelector('[data-photo-preview]');
    var bar = root.querySelector('[data-photo-bar]');
    var status = root.querySelector('[data-photo-status]');
    var reset = root.querySelector('[data-photo-reset]');
    var fileInput = root.querySelector('[data-photo-file]');
    var captureInput = root.querySelector('[data-photo-capture]');
    var rawText = document.getElementById('Input_RawText');
    var form = document.querySelector('form[method="post"]');

    // Longest edge the image is scaled to before recognition. A modern phone photo is
    // 3000-4000px, which costs several seconds of OCR for detail that tesseract cannot
    // use. Below about 1200 the digits start to break up, so this leaves headroom.
    var MAX_EDGE = 2000;

    var LIB = '/lib/tesseract/';
    var busyNow = false;

    // The control is hidden in the markup; this is what turns it on.
    root.hidden = false;

    // ── wiring ──────────────────────────────────────────────────────────────

    root.querySelector('[data-photo-take]').addEventListener('click', function () {
        if (!busyNow) captureInput.click();
    });
    root.querySelector('[data-photo-choose]').addEventListener('click', function () {
        if (!busyNow) fileInput.click();
    });
    [fileInput, captureInput].forEach(function (input) {
        input.addEventListener('change', function () {
            if (input.files && input.files[0]) start(input.files[0]);
            // Cleared so picking the same file twice still fires a change event.
            input.value = '';
        });
    });
    reset.addEventListener('click', function () { showIdle(); });

    ['dragenter', 'dragover'].forEach(function (name) {
        zone.addEventListener(name, function (e) {
            e.preventDefault();
            if (!busyNow) zone.classList.add('is-dragging');
        });
    });
    ['dragleave', 'drop'].forEach(function (name) {
        zone.addEventListener(name, function (e) {
            e.preventDefault();
            zone.classList.remove('is-dragging');
        });
    });
    zone.addEventListener('drop', function (e) {
        if (busyNow) return;
        var file = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
        if (file) start(file);
    });

    // ── view ────────────────────────────────────────────────────────────────

    function showIdle() {
        busyNow = false;
        busy.hidden = true;
        idle.hidden = false;
        zone.classList.remove('is-busy', 'is-error');
        preview.removeAttribute('src');
        setProgress(0);
    }

    function showBusy(dataUrl) {
        busyNow = true;
        idle.hidden = true;
        busy.hidden = false;
        reset.hidden = true;
        zone.classList.add('is-busy');
        zone.classList.remove('is-error');
        preview.src = dataUrl;
    }

    function setProgress(fraction) {
        bar.style.width = Math.max(0, Math.min(1, fraction)) * 100 + '%';
    }

    function say(message) { status.textContent = message; }

    function fail(message) {
        busyNow = false;
        zone.classList.add('is-error');
        zone.classList.remove('is-busy');
        reset.hidden = false;
        say(message);
        setProgress(0);
    }

    // ── the work ────────────────────────────────────────────────────────────

    function start(file) {
        if (!/^image\//.test(file.type)) {
            showBusy('');
            fail('That is not an image. Try a photo, or paste the roster below.');
            return;
        }

        say('Opening the photo…');
        setProgress(0.02);

        downscale(file).then(function (dataUrl) {
            showBusy(dataUrl);
            say('Loading the reader… this part happens once.');
            setProgress(0.06);
            return loadEngine().then(function () { return recognise(dataUrl); });
        }).then(function (text) {
            finish(text);
        }).catch(function (error) {
            fail('Could not read that photo. ' + (error && error.message ? error.message : '') +
                ' You can still paste the roster below.');
        });
    }

    // Draws the photo into a canvas at a sane size and hands back a data: URL.
    //
    // Deliberately no URL.createObjectURL anywhere. The obvious way to get a File onto a
    // canvas is an object URL on an <img>, but that is a blob: URL and the policy says
    // `img-src 'self' data:` — so the browser refuses to load it and the import fails
    // with a CSP violation in the console and nothing else. Widening the policy to allow
    // blob: would work; not needing to is better.
    //
    // createImageBitmap takes the Blob directly, which sidesteps the question, and skips
    // the base64 round-trip a FileReader fallback pays. `imageOrientation: 'from-image'`
    // is not optional: a portrait photo off a phone carries its rotation in EXIF, and
    // ignoring it hands the OCR a sideways page, which reads as no text at all.
    function downscale(file) {
        if (typeof createImageBitmap === 'function') {
            return createImageBitmap(file, { imageOrientation: 'from-image' })
                .then(toDataUrl)
                .catch(function () { return viaFileReader(file); });
        }
        return viaFileReader(file);
    }

    function toDataUrl(source) {
        var width = source.width || source.naturalWidth;
        var height = source.height || source.naturalHeight;
        var scale = Math.min(1, MAX_EDGE / Math.max(width, height));

        var canvas = document.createElement('canvas');
        canvas.width = Math.round(width * scale);
        canvas.height = Math.round(height * scale);
        canvas.getContext('2d').drawImage(source, 0, 0, canvas.width, canvas.height);

        if (source.close) source.close();

        return canvas.toDataURL('image/png');
    }

    // For browsers without createImageBitmap. A data: URL on an <img> is allowed by the
    // policy where a blob: URL is not, which is the whole reason this path reads the file
    // rather than pointing at it.
    function viaFileReader(file) {
        return new Promise(function (resolve, reject) {
            var reader = new FileReader();

            reader.onload = function () {
                var img = new Image();
                img.onload = function () { resolve(toDataUrl(img)); };
                img.onerror = function () { reject(new Error('The file could not be opened as an image.')); };
                img.src = reader.result;
            };
            reader.onerror = function () { reject(new Error('The file could not be read.')); };
            reader.readAsDataURL(file);
        });
    }

    var enginePromise = null;

    function loadEngine() {
        if (enginePromise) return enginePromise;

        enginePromise = new Promise(function (resolve, reject) {
            var script = document.createElement('script');
            script.src = LIB + 'tesseract.min.js';
            script.onload = function () { resolve(); };
            script.onerror = function () { reject(new Error('The reader failed to load.')); };
            document.head.appendChild(script);
        });

        return enginePromise;
    }

    function recognise(dataUrl) {
        // Every path is pinned to this origin. tesseract.js otherwise reaches for a CDN
        // for the core and the language data, which the Content-Security-Policy blocks —
        // correctly, since it would be a third party reading the roster.
        return window.Tesseract.createWorker('eng', 1, {
            workerPath: LIB + 'worker.min.js',
            corePath: LIB + 'tesseract-core-simd-lstm.js',
            langPath: LIB,
            // Load the worker from its own URL rather than wrapping it in a blob, so the
            // policy needs worker-src 'self' and not blob:.
            workerBlobURL: false,
            logger: onProgress
        }).then(function (worker) {
            return worker.recognize(dataUrl).then(function (result) {
                return worker.terminate().then(function () {
                    return result.data.text;
                });
            }).catch(function (error) {
                return worker.terminate().then(function () { throw error; });
            });
        });
    }

    function onProgress(message) {
        if (!message || typeof message.progress !== 'number') return;

        // Loading the engine and the language data occupies the first third of the bar;
        // actual recognition is the rest. Without the split the bar sits at zero through
        // a multi-megabyte download and then races, which reads as a hang.
        if (message.status === 'recognizing text') {
            setProgress(0.35 + message.progress * 0.65);
            say('Reading the roster… ' + Math.round(message.progress * 100) + '%');
        } else {
            setProgress(0.06 + message.progress * 0.29);
            say('Loading the reader… this part happens once.');
        }
    }

    function finish(text) {
        var cleaned = (text || '').replace(/\r\n?/g, '\n').replace(/\n{3,}/g, '\n\n').trim();

        setProgress(1);
        busyNow = false;
        reset.hidden = false;

        if (!cleaned) {
            fail('No text found in that photo. A flatter, closer, better-lit shot usually helps.');
            return;
        }

        // Appended rather than assigned: someone may have pasted part of the roster
        // already, and photographing the second page should not wipe the first.
        rawText.value = rawText.value.trim() ? rawText.value.replace(/\s+$/, '') + '\n' + cleaned : cleaned;

        say('Read the photo. Checking it for numbers…');

        // Straight into the existing Import handler, so the numbers the parser found show
        // up without a second click. This is a real form submit — the same one the Import
        // button does — so the server stays the only place that parses.
        var importButton = form.querySelector('[data-photo-import], button[formaction*="handler=Import"]');
        if (importButton) {
            importButton.click();
        } else {
            say('Read the photo. Press Import to pull the numbers out.');
        }
    }
}());
