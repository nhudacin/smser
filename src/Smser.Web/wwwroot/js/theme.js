// The light/dark switch.
//
// Two things happen here, and the order matters.
//
// First, the saved choice is applied to <html> before anything paints. That is why this
// file is loaded from <head> and not from the bottom of the page with site.js: a reader
// who has chosen dark would otherwise get a full flash of the light palette on every
// navigation while the stylesheet is already applied and the attribute is not. It has to
// be an external file rather than an inline <script>, because the Content-Security-Policy
// in ServiceDefaults has no 'unsafe-inline' in script-src, and inline handlers are not
// covered by nonces either.
//
// Second, once the DOM exists, the switch itself is revealed and wired. It ships hidden
// for the same reason the photo control does: without JavaScript it could not do
// anything, and a dead control is worse than no control. Anyone in that position gets the
// light palette, which is the app's default anyway.
//
// The OS preference is deliberately not consulted. This app renders light by default for
// everybody, and dark is something you ask for.
(function () {
    'use strict';

    var KEY = 'smser-theme';
    var DARK = 'dark';
    var root = document.documentElement;

    // localStorage throws rather than returning null in a few situations — Safari's
    // private mode historically, and any browser with site data blocked. A reader with
    // storage switched off should still get a working page in the default theme, so every
    // access is guarded and a failure just means the choice is not remembered.
    function saved() {
        try {
            return window.localStorage.getItem(KEY);
        } catch (e) {
            return null;
        }
    }

    function remember(value) {
        try {
            window.localStorage.setItem(KEY, value);
        } catch (e) {
            // Not fatal: the theme still applies for this page view.
        }
    }

    function apply(isDark) {
        if (isDark) {
            root.setAttribute('data-theme', DARK);
        } else {
            root.removeAttribute('data-theme');
        }
    }

    // Before paint.
    apply(saved() === DARK);

    function wire() {
        var toggle = document.querySelector('[data-theme-toggle]');
        if (!toggle) return;

        function sync() {
            var isDark = root.getAttribute('data-theme') === DARK;

            // aria-pressed is what makes this a switch to a screen reader rather than a
            // button labelled "Dark" that appears to do nothing.
            toggle.setAttribute('aria-pressed', isDark ? 'true' : 'false');
        }

        sync();
        toggle.hidden = false;

        toggle.addEventListener('click', function () {
            var next = root.getAttribute('data-theme') !== DARK;

            apply(next);
            remember(next ? DARK : 'light');
            sync();
        });
    }

    // This file runs in <head>, so the header does not exist yet.
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', wire);
    } else {
        wire();
    }
}());
