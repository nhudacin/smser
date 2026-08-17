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
    var pasteFallback = root.querySelector('[data-photo-paste-fallback]');
    var pasteTarget = root.querySelector('[data-photo-paste-target]');
    var rawText = document.getElementById('Input_RawText');
    var form = document.querySelector('form[method="post"]');

    var tabs = document.querySelector('[data-import-tabs]');
    var pastePanel = document.getElementById('panel-paste');

    // The tabs are the only way into the photo panel, so without them there is no way in.
    // Bailing leaves the field as its no-JS self — label, textarea, hint — which is the
    // right fallback rather than a photo panel that nothing can open.
    if (!tabs || !pastePanel) return;

    // Longest edge the image is read at. A modern phone photo is 3000-4000px, which costs
    // several seconds of OCR for detail tesseract cannot use, so big photos come down to
    // this.
    //
    // Small ones now go *up* to it, which is the half that reads wrong at first glance.
    // Tesseract wants text around 30px tall and struggles badly under about 10px, and an
    // image pasted on iOS arrives around 800px on its longest edge — perfectly legible to
    // the eye, but with roster text only a few pixels high. Enlarging it reads
    // dramatically better even though interpolation adds no information whatsoever; the
    // engine simply needs the strokes spread over enough pixels to resolve.
    //
    // Measured on the sideways 600×800 roster that sent us looking: the correct
    // orientation scored 26 at native size and 52 enlarged. That gap is the whole feature.
    var MAX_EDGE = 2000;

    // How far a small image may be enlarged. Upscaling buys real accuracy, but only up to
    // a point — blowing a thumbnail up to MAX_EDGE just buys OCR time on a page that was
    // never going to read.
    var MAX_UPSCALE = 4;

    // How sure tesseract has to be before the page is taken to be the right way up. A good
    // read of a printed roster lands in the seventies or above; a sideways one comes back
    // in the thirties, so there is a lot of room between them and this sits in the middle
    // of it. Costing an occasional needless orientation search is the cheaper mistake:
    // that is a few seconds, where the other way round is a screen of gibberish.
    var UPRIGHT_ENOUGH = 60;

    // Longest edge for the orientation probes.
    //
    // This was 500, on the theory that which way up a page is is a far coarser question
    // than what it says, and so is settled well below the size needed to read it. That
    // theory is wrong, and measurably so. On the sideways 600×800 roster, confidence by
    // orientation came out:
    //
    //     edge    0°   90°  180°  270° (the correct one)
    //      500    22    29    41    34   → picked 180°
    //      800    37    21    29    26   → picked 0°
    //     1600    39    30    37    44   → picked 270°
    //     2400    34    29    40    52   → picked 270°
    //
    // Below about 1600 the ranking is noise and lands on the wrong answer. Confidence
    // only starts tracking orientation once the text is big enough for the engine to
    // resolve at all — the same threshold MAX_EDGE is about, for the same reason.
    //
    // So the probes are no cheaper than a real read. That is the price of them being
    // right, and it is only paid when the first read came back poor.
    var PROBE_EDGE = 1600;

    var LIB = '/lib/tesseract/';
    var busyNow = false;

    // What the progress bar is currently narrating. A read can take several passes, and a
    // bar that says "Reading the roster" four times over reads as a hang.
    var phase = 'Reading the roster';

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

    // Shown only after the button has failed. Focus goes with it: on iOS the Paste callout
    // only appears on an element that already has the caret, so revealing this without
    // focusing it would be an instruction nobody could follow.
    function offerPasteTarget() {
        if (!pasteFallback) return;

        pasteFallback.hidden = false;
        if (pasteTarget) pasteTarget.focus();

        note('paste fallback', 'offered');
    }

    if (pasteTarget) {
        pasteTarget.addEventListener('paste', function (e) {
            var data = e.clipboardData;
            if (!data) return;

            // Nothing is ever allowed to land in the box — it is a target, not a field —
            // and the document listener must not get a second go at the same event.
            e.preventDefault();
            e.stopPropagation();

            var file = imageFrom(data);
            note('paste target', file ? 'image, ' + file.type + ', ' + file.size + ' bytes'
                : 'no image on the event');

            if (file) {
                start(file);
                return;
            }

            // Text pasted here belongs in the box below that exists for it. Refusing it on
            // a technicality would be worse than putting it where it was going anyway.
            var text = data.getData ? data.getData('text') : '';

            if (text && text.trim()) {
                rawText.value = rawText.value.trim()
                    ? rawText.value.replace(/\s+$/, '') + '\n' + text
                    : text;
                selectTab('paste');
                return;
            }

            showBusy('');
            fail('That paste had no image in it. Choose a photo instead.');
        });

        // Whatever gets past the handler above leaves nothing behind. A stray character
        // sitting in something that looks like a drop zone reads as a bug.
        pasteTarget.addEventListener('input', function () { pasteTarget.textContent = ''; });
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
                fail('Your browser would not give up the image on the clipboard.');
                offerPasteTarget();
                return;
            }

            start(file);
        }).catch(function (error) {
            note('clipboard error', (error && error.name ? error.name : 'unknown') +
                ' — ' + (error && error.message ? error.message : 'no message'));

            showBusy('');

            // Every failure here is the same failure from where the person is standing:
            // the browser would not hand the clipboard over. Whether that was a denied
            // permission, a dismissed prompt or an item describing itself as empty, naming
            // it helps nobody and NotAllowedError helps least of all. What helps is the
            // other way in, which is what the fallback below is.
            fail('Could not read the clipboard.');
            offerPasteTarget();
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

        // Nothing advertised. Safari on iOS hands back a ClipboardItem with an empty types
        // list for a photo copied out of Photos — one item, describing nothing — so taking
        // the list at its word means never finding an image that is demonstrably there.
        //
        // So ask anyway. getType rejects for a type the item does not hold, which makes
        // this a slow no-op on a clipboard that really has no image and the whole feature
        // on a clipboard that does.
        for (i = 0; i < items.length; i++) {
            if ((items[i].types || []).length === 0 && items[i].getType) {
                return probeForImage(items[i], 0);
            }
        }

        return null;
    }

    // The types worth guessing at, commonest first. HEIC is on the list because it is what
    // an iPhone camera writes, even though nothing here could decode one — better to find
    // it and say so than to report an empty clipboard.
    var PROBE_TYPES = ['image/png', 'image/jpeg', 'image/heic', 'image/heif', 'image/tiff', 'image/gif', 'image/webp'];

    function probeForImage(item, index) {
        if (index >= PROBE_TYPES.length) {
            note('clipboard probe', 'no image under any known type');
            return null;
        }

        return item.getType(PROBE_TYPES[index]).then(function (blob) {
            note('clipboard probe', PROBE_TYPES[index] + ' answered with ' + blob.size + ' bytes');
            return blob;
        }, function () {
            return probeForImage(item, index + 1);
        });
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

        // Reset, so a second photo does not inherit the last one's narration.
        phase = 'Reading the roster';

        prepare(file).then(function (dataUrl) {
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

    // Draws the photo into a canvas at a sane size and hands back a data: URL. Named for
    // what it does rather than which direction it does it in: it was `downscale` until it
    // learned to enlarge a small photo, which is now the commoner case of the two.
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
    function prepare(file) {
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

        // 6 and 8 are the quarter turns clockwise and anticlockwise. Everything downstream
        // works in degrees, because the orientation search has no EXIF to talk about.
        var canvas = turned(source, turn === 6 ? 90 : turn === 8 ? 270 : 0, MAX_EDGE);

        if (source.close) source.close();

        note('read at', canvas.width + '×' + canvas.height);

        return canvas.toDataURL('image/png');
    }

    // Draws a source onto a canvas turned by a quarter of a circle at a time, scaled so its
    // longest edge lands on maxEdge. Shared by the EXIF correction above and the
    // orientation search below, which want exactly the same thing for different reasons.
    function turned(source, degrees, maxEdge) {
        var width = source.width || source.naturalWidth;
        var height = source.height || source.naturalHeight;

        // Scale is measured against the image the right way up, so a page that is about to
        // be stood upright is not sized as if it were still on its side.
        var swap = degrees === 90 || degrees === 270;
        var uprightWidth = swap ? height : width;
        var uprightHeight = swap ? width : height;

        // Deliberately allowed to exceed 1. This used to be min(1, …), which meant the
        // function could only ever shrink — and that one clamp was enough to break the
        // whole orientation search twice over. It held the probes at whatever a small
        // photo already was, well under the size where confidence means anything; and it
        // then held the winning re-read down there too, so the correct rotation came back
        // scoring *lower* than the sideways first read and was thrown away by the guard
        // in findUpright that exists to keep the search safe.
        var scale = Math.min(MAX_UPSCALE, maxEdge / Math.max(uprightWidth, uprightHeight));

        var canvas = document.createElement('canvas');
        canvas.width = Math.round(uprightWidth * scale);
        canvas.height = Math.round(uprightHeight * scale);

        var context = canvas.getContext('2d');

        // Enlarging is now the common case for a pasted photo, and the default smoothing
        // is tuned for speed rather than for the interpolation quality that decides
        // whether the strokes survive. Not every engine honours it; where it is missing
        // the assignment is simply ignored.
        context.imageSmoothingEnabled = true;
        context.imageSmoothingQuality = 'high';

        if (degrees === 90) {
            context.translate(canvas.width, 0);
            context.rotate(Math.PI / 2);
        } else if (degrees === 180) {
            context.translate(canvas.width, canvas.height);
            context.rotate(Math.PI);
        } else if (degrees === 270) {
            context.translate(0, canvas.height);
            context.rotate(-Math.PI / 2);
        }

        context.drawImage(source, 0, 0, Math.round(width * scale), Math.round(height * scale));

        return canvas;
    }

    // Reopens a data: URL as an image, so an already-processed photo can be turned again
    // without going back to the file and redoing the decode.
    function loadImage(dataUrl) {
        return new Promise(function (resolve, reject) {
            var img = new Image();

            img.onload = function () { resolve(img); };
            img.onerror = function () { reject(new Error('The image could not be reopened.')); };
            img.src = dataUrl;
        });
    }

    function turnedDataUrl(dataUrl, degrees, maxEdge) {
        return loadImage(dataUrl).then(function (img) {
            return turned(img, degrees, maxEdge).toDataURL('image/png');
        });
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

    // One worker for however many passes the read turns out to need. createWorker
    // re-initialises the WebAssembly core every time it is called, and the orientation
    // search below can want five reads — paying that five times would cost more than the
    // reads themselves.
    function withWorker(job) {
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
            return job(worker).then(function (value) {
                return worker.terminate().then(function () { return value; });
            }).catch(function (error) {
                return worker.terminate().then(function () { throw error; });
            });
        });
    }

    function read(worker, dataUrl) {
        return worker.recognize(dataUrl).then(function (result) {
            return { text: result.data.text || '', confidence: result.data.confidence };
        });
    }

    function recognise(dataUrl) {
        return withWorker(function (worker) {
            return read(worker, dataUrl).then(function (first) {
                // Confidence is the number that separates "the page was sideways" from
                // "the page was blurred". Tesseract reports it per read; below about 60 on
                // a printed sheet means it was not reading printed text at all.
                note('confidence', Math.round(first.confidence));
                note('characters read', first.text.length);

                if (first.confidence >= UPRIGHT_ENOUGH) return first.text;

                return findUpright(worker, dataUrl, first);
            });
        });
    }

    // Works out which way up the page is, by reading it every way up and believing the
    // one tesseract was most sure of.
    //
    // EXIF is the cheap answer and it is tried first, back in toDataUrl — but it only
    // exists when the camera wrote a tag, and a roster photographed sideways on a table
    // has nothing to write. The pixels are then the only evidence there is.
    //
    // Tesseract can be asked directly: osd.traineddata detects page orientation without
    // reading a word. It is 4.3 MB gzipped, which is over twice the entire English
    // language data, and it would still only be a hint — a wrong one would have to be
    // caught by re-reading anyway, so it removes no work, it only reorders it. This app
    // ships one core variant instead of eight and the fast language data instead of the
    // accurate one, both to save less than that. So the probes do the job instead.
    //
    // They are cheap because they are small. Orientation is a much coarser question than
    // transcription — upright text scores far above sideways text long before either is
    // legible — so the probes run at PROBE_EDGE, where a pass costs a fraction of a real
    // read. Only the winner is then read properly.
    function findUpright(worker, dataUrl, first) {
        var best = { degrees: 0, confidence: -1 };

        phase = 'Working out which way up the page is';

        return [0, 90, 180, 270].reduce(function (chain, degrees) {
            return chain.then(function () {
                return turnedDataUrl(dataUrl, degrees, PROBE_EDGE).then(function (probe) {
                    return read(worker, probe).then(function (result) {
                        note('probe at ' + degrees + '°', 'confidence ' + Math.round(result.confidence));

                        if (result.confidence > best.confidence) {
                            best = { degrees: degrees, confidence: result.confidence };
                        }
                    });
                });
            });
        }, Promise.resolve()).then(function () {
            note('upright at', best.degrees + '°');

            // Already the right way up, so the first read is the best there is and the
            // page was simply hard to read. Saying so beats turning it for no reason.
            if (best.degrees === 0) return first.text;

            phase = 'Reading it again the right way up';

            return turnedDataUrl(dataUrl, best.degrees, MAX_EDGE).then(function (turned) {
                return read(worker, turned).then(function (second) {
                    note('confidence after turning', Math.round(second.confidence));

                    // The probes are small and can be wrong. This cannot make the result
                    // worse than not having tried: the full read has to actually beat the
                    // one it is replacing.
                    return second.confidence > first.confidence ? second.text : first.text;
                });
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
            say(phase + '… ' + Math.round(message.progress * 100) + '%');
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
