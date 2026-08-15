using QRCoder;
using QRCoder.Exceptions;

namespace Smser.Web.Services;

/// <summary>
/// Renders the roster's <c>sms:</c> link as a QR code, inline as a <c>data:</c> URI.
///
/// Generated per request rather than stored. A PNG for a large roster runs to tens of
/// kilobytes, which is an order of magnitude more than the roster itself — the original
/// app kept it in storage alongside the numbers, which made every saved list expensive
/// to hold and impossible to fix once a rendering bug shipped. Regenerating costs about
/// a millisecond.
/// </summary>
public sealed class QrCodeGenerator
{
    /// <summary>
    /// Widest QR the page will draw, in CSS pixels. The module size is chosen to land
    /// near this so a 40-number roster and a 4-number roster produce images of roughly
    /// the same file size rather than the former being a 2000px monster.
    /// </summary>
    private const int TargetPixels = 640;

    /// <summary>
    /// Renders <paramref name="smsUrl"/>, or null if the roster is too large to fit in a
    /// QR code at all.
    ///
    /// The ceiling is a property of the format, not of this code: a version-40 symbol at
    /// error correction L holds 2,953 bytes, which at twelve characters per number works
    /// out to roughly 240 numbers. Past that the page shows the link and the mobile
    /// button and simply omits the code, which is the honest outcome — the alternative is
    /// an unhandled <see cref="DataTooLongException"/> taking down a page that had a
    /// perfectly good roster on it.
    /// </summary>
    public string? CreateDataUri(string smsUrl)
    {
        if (string.IsNullOrEmpty(smsUrl)) return null;

        try
        {
            using var generator = new QRCodeGenerator();

            // Error correction L. The code is read once, on a phone, from a screen a few
            // inches away — the redundancy that L gives up buys capacity, which is the
            // constraint that actually binds here.
            using var data = generator.CreateQrCode(Payload(smsUrl), QRCodeGenerator.ECCLevel.L);

            var modules = data.ModuleMatrix.Count;
            var pixelsPerModule = Math.Max(2, TargetPixels / Math.Max(modules, 1));

            var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule);

            return "data:image/png;base64," + Convert.ToBase64String(png);
        }
        catch (DataTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Wraps the link in single quotes before encoding it.
    ///
    /// Carried over verbatim from the original app, where the line was marked
    /// "DO NOT DELETE" — the quotes change how phone camera apps treat the decoded
    /// content of a non-http scheme, and removing them changed what happened when the
    /// code was scanned. The original recorded the behaviour but not the mechanism, and
    /// this is not the place to guess at one: it is preserved because scanning is the
    /// primary way anyone uses this app, and it is the one part of the pipeline that
    /// cannot be verified from a unit test.
    /// </summary>
    private static string Payload(string smsUrl) => $"'{smsUrl}'";
}
