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
    var pasteHint = root.querySelector('[data-photo-paste-hint]');
    var pasteButton = root.querySelector('[data-photo-paste]');
    var rawText = document.getElementById('Input_RawText');
    var form = document.querySelector('form[method="post"]');

    var tabs = document.querySelector('[data-import-tabs]');
    var pastePanel = document.getElementById('panel-paste');

    // The tabs are the only way into the photo panel, so without them there is no way in.
    // Bailing leaves the field as its no-JS self — label, textarea, hint — which is the
    // right fallback rather than a photo panel that nothing can open.
    if (!tabs || !pastePanel) return;

    // Longest edge the image is scaled to before recognition. A modern phone photo is
    // 3000-4000px, which costs several seconds of OCR for detail that tesseract cannot
    // use. Below about 1200 the digits start to break up, so this leaves headroom.
    var MAX_EDGE = 2000;

    var LIB = '/lib/tesseract/';
    var busyNow = false;

    // ── tabs ────────────────────────────────────────────────────────────────

    var tabButtons = tabs.querySelectorAll('[data-import-tab]');

    // The tablist is hidden in the markup; this is what turns it on. From here the tabs
    // own which panel is showing — including the photo panel's own `hidden`, which is why
    // nothing sets it directly any more.
    tabs.hidden = false;
    selectTab('paste');

    for (var t = 0; t < tabButtons.length; t++) {
        tabButtons[t].addEventListener('click', function () {
            // `this` rather than the event target, because the click may well land on one
            // of the two label spans inside the button. Focus is left where the pointer
            // put it; moving it on a click only fights the mouse.
            selectTab(this.getAttribute('data-import-tab'));
        });
    }

    // Arrows move and select in one go — automatic activation, which costs nothing with
    // two tabs. This is what the roving tabindex in the markup is for: only the selected
    // tab is in the tab order, so Tab leaves the control instead of walking through it.
    tabs.addEventListener('keydown', function (e) {
        var current = tabIndexOf(document.activeElement);
        if (current < 0) return;

        var next;
        if (e.key === 'ArrowLeft') next = current - 1;
        else if (e.key === 'ArrowRight') next = current + 1;
        else if (e.key === 'Home') next = 0;
        else if (e.key === 'End') next = tabButtons.length - 1;
        else return;

        e.preventDefault();

        next = (next + tabButtons.length) % tabButtons.length;
        selectTab(tabButtons[next].getAttribute('data-import-tab'));
        tabButtons[next].focus();
    });

    function selectTab(name) {
        for (var i = 0; i < tabButtons.length; i++) {
            var chosen = tabButtons[i].getAttribute('data-import-tab') === name;

            tabButtons[i].setAttribute('aria-selected', chosen ? 'true' : 'false');
            tabButtons[i].tabIndex = chosen ? 0 : -1;
        }

        pastePanel.hidden = name !== 'paste';
        root.hidden = name !== 'photo';
    }

    function tabIndexOf(element) {
        for (var i = 0; i < tabButtons.length; i++) {
            if (tabButtons[i] === element) return i;
        }
        return -1;
    }

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

    // Paste. On a desktop the roster usually arrives as a screenshot — a snip of a PDF, a
    // crop of a team e-mail, an image copied out of a chat — and none of those are a file
    // on disk. Without this the only way in is "save it somewhere, then go and find it".
    //
    // The listener is on the document, not the zone: the zone is not focusable, so a
    // zone-scoped listener would never fire without a click nobody thinks to make. Paste
    // anywhere on the page and it lands here.
    //
    // It yields to text, which matters because the import textarea sits directly below and
    // exists to be pasted into. Taking the image only when the clipboard has no text to
    // offer leaves an ordinary text paste — much the more common one — entirely alone, and
    // means a clipboard carrying both (copying a region out of a spreadsheet, say) still
    // does the thing the textarea was asked to do.
    document.addEventListener('paste', function (e) {
        if (busyNow) return;

        var data = e.clipboardData;
        if (!data) return;

        var text = data.getData ? data.getData('text') : '';
        if (text && text.trim()) return;

        var file = imageFrom(data);
        if (!file) return;

        // Only once there is definitely an image to read. Everything else — text, an empty
        // clipboard, a file that is not an image — is left to the browser to handle.
        e.preventDefault();

        // Whichever tab was showing, the photo one is where the work is about to appear.
        // Without this the paste starts a read whose progress bar is on a hidden panel.
        selectTab('photo');
        start(file);
    });

    // The hint is revealed the same way the control itself is, and for the same reason:
    // telling someone they can paste is worse than saying nothing if pasting does nothing.
    if (window.ClipboardEvent && pasteHint) pasteHint.hidden = false;

    // The other half of pasting, and the only half a phone has.
    //
    // Everything above is the paste *event*, which is a push: the browser fires it at a
    // focused editable element when a paste gesture lands on one. That is why it works on
    // a laptop and cannot work here on a phone. There is no editable element in this panel
    // to paste into — a <textarea> would refuse an image even if there were one — so on a
    // handset the event simply never arrives, and the hint that advertises it is hidden by
    // `.on-fine` precisely because it would be a lie.
    //
    // Reading the clipboard is the pull direction, and it is the direction a phone offers.
    // Revealed only where the API exists, which is the rule the hint and the whole control
    // already follow: a button that cannot do the thing it names is worse than no button.
    if (pasteButton && navigator.clipboard && navigator.clipboard.read) {
        pasteButton.hidden = false;
        pasteButton.addEventListener('click', readClipboard);
    }

    function readClipboard() {
        if (busyNow) return;

        // Called as the very first thing in the click handler, with nothing awaited in
        // front of it. Reading the clipboard is something a user gesture permits rather
        // than something the page may do, and that permission is already gone by the time
        // an earlier promise resolves — so an await here costs the whole feature.
        var reading;
        try {
            reading = navigator.clipboard.read();
        } catch (error) {
            reading = Promise.reject(error);
        }

        reading.then(imageFromClipboard).then(function (file) {
            if (!file) {
                showBusy('');
                fail('Nothing on the clipboard looked like an image. Copy a photo first, ' +
                    'or use Take a photo.');
                return;
            }

            start(file);
        }).catch(function () {
            showBusy('');

            // Every failure here is the same failure from where the person is standing:
            // the browser would not hand the clipboard over. Whether that was a denied
            // permission, a dismissed prompt or an empty clipboard, naming it helps nobody
            // and NotAllowedError helps least of all.
            fail('Could not read the clipboard. Some browsers ask permission first — ' +
                'otherwise use Take a photo or Choose a photo.');
        });
    }

    // ClipboardItem rather than a file list: each item advertises its types and hands over
    // the blob only when asked. A screenshot usually arrives as image/png and a photo out
    // of the camera roll as image/jpeg, so both paths match on the type rather than
    // assuming one — the same reason imageFrom() above looks instead of taking the first.
    function imageFromClipboard(items) {
        for (var i = 0; i < items.length; i++) {
            var types = items[i].types || [];

            for (var j = 0; j < types.length; j++) {
                if (/^image\//.test(types[j])) return items[i].getType(types[j]);
            }
        }

        return null;
    }

    // Two ways in, because browsers do not agree on which they fill. `files` is the direct
    // one and is what a screenshot shows up as in current browsers; `items` is the older
    // route and still the only populated one in some. A clipboard can also hold several
    // things at once, so both paths look for the image rather than assuming it is first.
    function imageFrom(data) {
        var i;

        if (data.files && data.files.length) {
            for (i = 0; i < data.files.length; i++) {
                if (/^image\//.test(data.files[i].type)) return data.files[i];
            }
        }

        if (data.items) {
            for (i = 0; i < data.items.length; i++) {
                if (data.items[i].kind === 'file' && /^image\//.test(data.items[i].type)) {
                    var file = data.items[i].getAsFile();
                    if (file) return file;
                }
            }
        }

        return null;
    }

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

        // Back to the text that was just read. This is the hand-off the tabs exist to
        // make obvious: the photo tab is a way of filling the paste box, not a second
        // place the roster lives, so it returns you to the text you can now correct.
        selectTab('paste');

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
