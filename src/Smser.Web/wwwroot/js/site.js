// The app's only script. Everything on the page works without it — Import and Generate
// are real form posts, and the mobile link is an anchor — so this file is strictly an
// enhancement of the copy-to-clipboard button, which has no no-JS equivalent.
//
// Listeners are attached here rather than with onclick attributes because the CSP in
// ServiceDefaults has no 'unsafe-inline' in script-src, and inline handlers are not
// covered by nonces either.
(function () {
    'use strict';

    var status = document.querySelector('[data-copy-status]');
    var original = status ? status.textContent : null;
    var revert;

    function say(message) {
        if (!status) return;

        status.textContent = message;
        window.clearTimeout(revert);
        revert = window.setTimeout(function () { status.textContent = original; }, 3000);
    }

    document.querySelectorAll('[data-copy]').forEach(function (button) {
        // navigator.clipboard is unavailable on insecure origins, which in practice means
        // a phone hitting the dev machine over plain http on the LAN. Showing the link
        // for manual copying beats a button that silently does nothing.
        if (!navigator.clipboard) {
            button.hidden = true;
            return;
        }

        button.addEventListener('click', function () {
            navigator.clipboard.writeText(button.dataset.copy).then(
                function () { say('Link copied to the clipboard.'); },
                function () { say('Could not copy — the link is in the address bar.'); }
            );
        });
    });
}());
