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

    // ── diagnostics ─────────────────────────────────────────────────────────

    // Off unless asked for by hand: /new?debug=1.
    //
    // The photo path is the one part of this app that cannot be tested from the server or
    // from a unit test — it is a camera, a decoder and a WebAssembly engine, and the three
    // of them disagree by browser. When it goes wrong on somebody's phone the only honest
    // way to find out why has been to guess, and every guess costs a round trip.
    //
    // So this writes down what actually happened and shows it on the page. It is not a
    // console log: the phone where this breaks is the phone with no console attached.
    //
    // Nothing is sent anywhere. The photo never leaves the device — that is the whole
    // reason the OCR runs locally — and neither does this, until the person reads it and
    // decides to copy it.
    var DEBUG = /(?:^|[?&])debug=1(?:&|$)/.test(window.location.search);

    var debugPanel = document.querySelector('[data-photo-debug]');
    var debugLog = document.querySelector('[data-photo-debug-log]');
    var debugCopy = document.querySelector('[data-photo-debug-copy]');
    var notes = [];

    if (DEBUG && debugPanel) debugPanel.hidden = false;

    function note(label, value) {
        if (!DEBUG) return;

        notes.push(label + ': ' + value);
        if (debugLog) debugLog.value = notes.join('\n');
    }

    if (debugCopy && debugLog) {
        debugCopy.addEventListener('click', function () {
            // select() first and unconditionally: on iOS the Clipboard API is the part most
            // likely to be broken here, and if it is, the text is at least already selected
            // for a long-press Copy.
            debugLog.select();

            if (!navigator.clipboard) return;
            navigator.clipboard.writeText(debugLog.value).then(function () {
                debugCopy.textContent = 'Copied';
            }, function () {
                debugCopy.textContent = 'Select all above and copy';
            });
        });
    }

    note('page', window.location.pathname);
    note('user agent', window.navigator.userAgent);
    note('createImageBitmap', typeof createImageBitmap === 'function' ? 'yes' : 'no');
    note('clipboard read', navigator.clipboard && navigator.clipboard.read ? 'yes' : 'no');

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

        reading.then(function (items) {
            note('clipboard items', items.length);

            for (var i = 0; i < items.length; i++) {
                note('clipboard item ' + (i + 1), (items[i].types || []).join(', ') || 'no types');
            }

            return imageFromClipboard(items);
        }).then(function (file) {
            if (!file) {
                showBusy('');
                fail('Nothing on the clipboard looked like an image. Copy a photo first, ' +
                    'or use Take a photo.');
                return;
            }

            start(file);
        }).catch(function (error) {
            note('clipboard error', (error && error.name ? error.name : 'unknown') +
                ' — ' + (error && error.message ? error.message : 'no message'));

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
        note('file', file.type + ', ' + file.size + ' bytes');

        // The bytes are read for their header before the image is decoded, because what
        // the header says is the only way to tell afterwards whether the decoder did what
        // it was asked. A failure here is not fatal — it costs the rotation check, not
        // the import.
        return bytes(file).catch(function () { return null; }).then(function (buffer) {
            var facts = buffer ? readJpegHeader(buffer) : null;

            if (facts) {
                note('exif orientation', facts.orientation);
                note('stored size', facts.width + '×' + facts.height);
            } else {
                note('exif orientation', 'no JPEG header to read');
            }

            return decode(file).then(function (source) { return toDataUrl(source, facts); });
        });
    }

    function decode(file) {
        if (typeof createImageBitmap === 'function') {
            return createImageBitmap(file, { imageOrientation: 'from-image' })
                .then(function (bitmap) {
                    note('decoder', 'createImageBitmap');
                    return bitmap;
                })
                .catch(function () {
                    note('decoder', 'createImageBitmap threw, fell back to FileReader');
                    return viaFileReader(file);
                });
        }

        note('decoder', 'no createImageBitmap, using FileReader');
        return viaFileReader(file);
    }

    function toDataUrl(source, facts) {
        var width = source.width || source.naturalWidth;
        var height = source.height || source.naturalHeight;

        note('decoded size', width + '×' + height);

        // Did the decoder apply the rotation it was asked for?
        //
        // It does not say, and asking is not the same as being obeyed: a decoder is free
        // to accept `imageOrientation: 'from-image'` and ignore it, which hands the OCR a
        // page lying on its side. That fails as gibberish rather than as an error, so
        // nothing upstream notices — and it can only happen with a photo off a phone,
        // because a phone is the only thing that writes the tag.
        //
        // The stored dimensions settle it. Orientations 6 and 8 are the quarter turns, so
        // if the decode came back with width and height still in the order the file stores
        // them, nothing was rotated and this has to do it.
        //
        // Only those two. 2, 3 and 4 are the flips and the half turn, none of which change
        // the dimensions — so there is no way to tell from here whether the decoder already
        // acted, and guessing wrong would turn a correct page upside down. 5 and 7 are the
        // transposes, which no camera emits. They are reported and left alone.
        var quarterTurn = facts && (facts.orientation === 6 || facts.orientation === 8);
        var asStored = facts && facts.width > 0 && width === facts.width && height === facts.height;
        var turn = quarterTurn && asStored ? facts.orientation : 1;

        note('rotation', turn === 1
            ? (facts && facts.orientation > 1 ? 'left to the decoder' : 'none needed')
            : 'turned here, orientation ' + turn);

        // Scale is measured against the image the right way up, so a portrait photo is not
        // sized as if it were landscape.
        var turnedWidth = turn === 1 ? width : height;
        var turnedHeight = turn === 1 ? height : width;
        var scale = Math.min(1, MAX_EDGE / Math.max(turnedWidth, turnedHeight));

        var drawWidth = Math.round(width * scale);
        var drawHeight = Math.round(height * scale);

        var canvas = document.createElement('canvas');
        canvas.width = Math.round(turnedWidth * scale);
        canvas.height = Math.round(turnedHeight * scale);

        var context = canvas.getContext('2d');

        if (turn === 6) {
            context.translate(canvas.width, 0);
            context.rotate(Math.PI / 2);
        } else if (turn === 8) {
            context.translate(0, canvas.height);
            context.rotate(-Math.PI / 2);
        }

        context.drawImage(source, 0, 0, drawWidth, drawHeight);

        if (source.close) source.close();

        note('read at', canvas.width + '×' + canvas.height);

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
                img.onload = function () { resolve(img); };
                img.onerror = function () { reject(new Error('The file could not be opened as an image.')); };
                img.src = reader.result;
            };
            reader.onerror = function () { reject(new Error('The file could not be read.')); };
            reader.readAsDataURL(file);
        });
    }

    // Just the head. EXIF lives in the first APP1 segment and cannot exceed 64 KB, and the
    // frame header follows close behind it, so there is no reason to hold a second copy of
    // a five megapixel photo in memory to read a handful of bytes.
    function bytes(file) {
        var head = file.slice ? file.slice(0, 256 * 1024) : file;

        if (head.arrayBuffer) return head.arrayBuffer();

        return new Promise(function (resolve, reject) {
            var reader = new FileReader();

            reader.onload = function () { resolve(reader.result); };
            reader.onerror = function () { reject(new Error('The file could not be read.')); };
            reader.readAsArrayBuffer(head);
        });
    }

    // The two facts that decide whether a decode came back rotated: the orientation tag,
    // and the size the frame header declares. Small enough to read by hand, and reading it
    // by hand is cheaper than a library for a photo that never leaves this device anyway.
    function readJpegHeader(buffer) {
        var view = new DataView(buffer);

        if (view.byteLength < 4 || view.getUint16(0) !== 0xFFD8) return null;

        var facts = { orientation: 1, width: 0, height: 0 };
        var offset = 2;

        while (offset + 4 <= view.byteLength) {
            if (view.getUint8(offset) !== 0xFF) { offset++; continue; }

            var marker = view.getUint8(offset + 1);

            // Padding and the standalone markers, none of which carry a length.
            if (marker === 0xFF || marker === 0x01 || (marker >= 0xD0 && marker <= 0xD9)) {
                offset += 2;
                continue;
            }

            // Start of scan. Compressed data from here on, and nothing left worth reading.
            if (marker === 0xDA) break;

            var length = view.getUint16(offset + 2);
            if (length < 2) break;

            if (marker === 0xE1) readOrientation(view, offset + 4, facts);

            // A start-of-frame marker carries the stored dimensions. The Huffman and
            // arithmetic-coding tables share the 0xC0-0xCF range and are not frames.
            if (marker >= 0xC0 && marker <= 0xCF &&
                marker !== 0xC4 && marker !== 0xC8 && marker !== 0xCC &&
                offset + 9 <= view.byteLength) {
                facts.height = view.getUint16(offset + 5);
                facts.width = view.getUint16(offset + 7);
            }

            offset += 2 + length;
        }

        return facts;
    }

    // "Exif\0\0", then a TIFF header that declares the byte order everything after it uses
    // — including, on the same phones that write the orientation tag, big-endian.
    function readOrientation(view, start, facts) {
        if (start + 14 > view.byteLength) return;
        if (view.getUint32(start) !== 0x45786966) return;

        var tiff = start + 6;
        var order = view.getUint16(tiff);
        var little = order === 0x4949;

        if (!little && order !== 0x4D4D) return;
        if (view.getUint16(tiff + 2, little) !== 42) return;

        var ifd = tiff + view.getUint32(tiff + 4, little);
        if (ifd + 2 > view.byteLength) return;

        var count = view.getUint16(ifd, little);

        for (var i = 0; i < count; i++) {
            var entry = ifd + 2 + i * 12;
            if (entry + 12 > view.byteLength) return;

            if (view.getUint16(entry, little) === 0x0112) {
                var value = view.getUint16(entry + 8, little);
                if (value >= 1 && value <= 8) facts.orientation = value;
                return;
            }
        }
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
                // Confidence is the number that separates "the page was sideways" from
                // "the page was blurred". Tesseract reports it per read; below about 60
                // on a printed sheet means it was not reading printed text at all.
                note('confidence', Math.round(result.data.confidence));
                note('characters read', (result.data.text || '').length);

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

        note('first line read', cleaned.split('\n')[0]);

        // Diagnostics stop here rather than importing, and that is the point of them.
        // Import is a form post: it navigates, and everything written down above goes with
        // the old document. Whoever asked for ?debug=1 wants to read it.
        if (DEBUG) {
            say('Read the photo. Diagnostics are below — press Import when you have copied them.');
            return;
        }

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
