using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Response security headers, applied to every response the web app produces.
///
/// Lives in ServiceDefaults rather than in Smser.Library because the Library is
/// deliberately host-agnostic — it holds the parser and the storage contract and takes
/// no ASP.NET dependency, so a console tool or a future worker can reference it.
/// </summary>
public static class SecurityHeaders
{
    /// <summary>
    /// The whole policy. Every source is same-origin except QR images, which are
    /// generated per-request and inlined as <c>data:</c> URIs rather than written to
    /// storage and served back — so <c>img-src</c> has to allow <c>data:</c>.
    ///
    /// Note there is no <c>'unsafe-inline'</c> anywhere. That is the reason all page
    /// styling lives in <c>wwwroot/css/site.css</c> and all behaviour in
    /// <c>wwwroot/js/site.js</c> instead of in <c>&lt;style&gt;</c>/<c>onclick</c>
    /// attributes: an inline-free policy is only worth writing if the markup honours it,
    /// and one inline handler added later will fail visibly in the console rather than
    /// silently weakening the header.
    /// </summary>
    private const string Policy =
        "default-src 'self'; " +
        "style-src 'self'; " +
        // 'wasm-unsafe-eval' is what lets the photo importer compile the Tesseract
        // WebAssembly module. It is narrower than it sounds: it permits WebAssembly
        // compilation and nothing else — no eval, no new Function, no inline script — and
        // the only bytes it applies to are served from this origin under script-src 'self'.
        // Without it the browser refuses to instantiate the module and photo import fails
        // with a console error and no other symptom.
        "script-src 'self' 'wasm-unsafe-eval'; " +
        // The OCR engine runs in a worker. Loaded from its own URL rather than a blob
        // (workerBlobURL: false in photo-ocr.js), which is what keeps blob: out of here.
        "worker-src 'self'; " +
        // data: covers both the QR code and the downscaled photo preview, which is a
        // canvas data URL for the same reason — so blob: is not needed.
        "img-src 'self' data:; " +
        "font-src 'self'; " +
        // No plugins. Cheap, and closes an XSS vector that survives script-src.
        "object-src 'none'; " +
        // Stops an injected <base> tag repointing every relative script URL at somebody
        // else's origin — an attack that works even under a strict script-src.
        "base-uri 'self'; " +
        // The roster form posts to this app and nowhere else.
        "form-action 'self'; " +
        // Clickjacking. Supersedes X-Frame-Options in modern browsers; both are sent
        // because X-Frame-Options is what older ones understand.
        "frame-ancestors 'none'";

    /// <summary>
    /// Adds the response headers. Call early — before static files and before the
    /// endpoints — so they are present on every response including 404s and errors,
    /// which are exactly the responses an attacker is most interested in.
    /// </summary>
    public static IApplicationBuilder UseSmserSecurityHeaders(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            // OnStarting rather than setting them here: a downstream handler that
            // replaces the response (the exception handler, a redirect result) would
            // otherwise drop them. This runs after the response is final and before the
            // first byte goes out.
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;

                // Indexed assignment, not Append — appending would emit a duplicate if
                // anything upstream already set one, and browsers treat a duplicated
                // X-Frame-Options as invalid and some ignore it entirely.
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

                // This app asks for none of these. Naming them denies them to anything
                // injected into a page as well.
                headers["Permissions-Policy"] =
                    "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";

                headers["Content-Security-Policy"] = Policy;

                // The Server header is not removable from here — Kestrel writes it below
                // the middleware layer, so a Remove() call in this callback looks correct
                // and does nothing. It is suppressed with AddServerHeader = false where
                // the host builds Kestrel.

                return Task.CompletedTask;
            });

            await next();
        });
    }
}
