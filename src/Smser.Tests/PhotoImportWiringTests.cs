using System.Net;
using System.Text.RegularExpressions;

namespace Smser.Tests;

/// <summary>
/// The photo importer's contract with the page, checked against rendered HTML.
///
/// The OCR itself runs in a browser on WebAssembly and cannot be exercised from here —
/// it is verified by driving a real browser against a photo of a roster. What *can* rot
/// silently is the wiring around it: the control appearing for people who have no
/// JavaScript, the camera attribute going missing, a vendored asset not being deployed,
/// or the Content-Security-Policy quietly losing the one directive that lets the engine
/// compile. Each of those fails invisibly in production, so each gets a test.
/// </summary>
[TestClass]
public class PhotoImportWiringTests
{
    private static SmserApp _app = null!;
    private static string _page = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        _app = new SmserApp();
        _page = await _app.GetPageAsync("/new");
    }

    [ClassCleanup]
    public static void Stop() => _app.Dispose();

    [TestMethod]
    public void The_photo_control_is_hidden_until_script_reveals_it()
    {
        // None of it works without JavaScript. A camera button that cannot open a camera
        // is worse than no camera button, so the markup ships hidden and photo-ocr.js is
        // what turns it on.
        Assert.IsTrue(
            Regex.IsMatch(_page, @"<div class=""field"" data-photo hidden"),
            "the photo block must render with the hidden attribute");
    }

    [TestMethod]
    public void The_camera_input_asks_for_the_rear_camera()
    {
        // capture="environment" is what makes a phone open the camera directly instead of
        // the photo library, and "environment" rather than "user" is the difference
        // between pointing at the roster and pointing at your own face.
        StringAssert.Contains(_page, @"capture=""environment""");
    }

    [TestMethod]
    public void Both_a_camera_input_and_a_plain_file_input_are_present()
    {
        // Two inputs on purpose: `capture` takes the photo library away, so choosing an
        // existing photo needs an input without it.
        Assert.IsTrue(Regex.IsMatch(_page, @"data-photo-file"), "missing the plain file input");
        Assert.IsTrue(Regex.IsMatch(_page, @"data-photo-capture"), "missing the camera input");
    }

    [TestMethod]
    public void The_paste_hint_is_hidden_until_script_reveals_it()
    {
        // Same rule as the control itself: the hint is only true once the paste listener
        // exists, so it ships hidden and photo-ocr.js is what turns it on.
        Assert.IsTrue(
            Regex.IsMatch(_page, @"data-photo-paste-hint hidden"),
            "the paste hint must render with the hidden attribute");
    }

    [TestMethod]
    public async Task The_script_still_listens_for_a_pasted_image()
    {
        // The hint above and this listener are two halves of one feature, and only one of
        // them is visible. Deleting the listener leaves a page that invites a paste and
        // then ignores it, which nothing else here would catch.
        var script = await _app.GetPageAsync("/js/photo-ocr.js");

        StringAssert.Contains(script, "addEventListener('paste'",
            "the paste listener is gone, but the page still offers pasting");
    }

    [TestMethod]
    public async Task A_pasted_image_does_not_hijack_a_pasted_roster()
    {
        // The import textarea sits directly below the drop zone and exists to be pasted
        // into. The listener yields whenever the clipboard carries text, so losing that
        // check would break the app's primary input to add a shortcut to its secondary one.
        var script = await _app.GetPageAsync("/js/photo-ocr.js");

        StringAssert.Contains(script, "if (text && text.trim()) return;",
            "the paste listener must stand down when the clipboard holds text");
    }

    [TestMethod]
    public void The_script_can_find_the_import_button_it_submits()
    {
        // After OCR the script clicks Import so the numbers appear without a second tap.
        // It finds the button by this marker; losing it silently downgrades the flow to
        // "text appeared in a box, now press something".
        StringAssert.Contains(_page, "data-photo-import");
    }

    [TestMethod]
    public void The_photo_script_is_only_loaded_on_this_page()
    {
        StringAssert.Contains(_page, "/js/photo-ocr.js");
    }

    [TestMethod]
    public async Task The_home_page_does_not_pay_for_the_photo_script()
    {
        var home = await _app.GetPageAsync("/");

        Assert.IsFalse(home.Contains("photo-ocr.js", StringComparison.Ordinal),
            "the home page has no photo control and should not load the script that drives one");
    }

    [TestMethod]
    public async Task Every_vendored_engine_asset_is_actually_served()
    {
        // These are static files under wwwroot. A rename, a missed commit, or a content
        // type the static file middleware refuses to serve all show up as a 404 at the
        // moment someone takes a photo, which is the worst time to find out.
        string[] assets =
        [
            "/js/photo-ocr.js",
            "/lib/tesseract/tesseract.min.js",
            "/lib/tesseract/worker.min.js",
            "/lib/tesseract/tesseract-core-simd-lstm.js",
            "/lib/tesseract/tesseract-core-simd-lstm.wasm",
            "/lib/tesseract/eng.traineddata.gz"
        ];

        foreach (var asset in assets)
        {
            var response = await _app.HeadAsync(asset);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"{asset} is not being served");
        }
    }

    [TestMethod]
    public async Task The_policy_still_allows_the_engine_to_compile()
    {
        var response = await _app.HeadAsync("/new");
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();

        // Without this the browser refuses to instantiate the WebAssembly module and the
        // only symptom is a console error.
        StringAssert.Contains(csp, "'wasm-unsafe-eval'", "the OCR engine cannot compile without it");

        // The engine runs in a worker loaded from its own URL.
        StringAssert.Contains(csp, "worker-src 'self'");

        // And the preview is a canvas data: URL precisely so blob: is not needed here.
        StringAssert.Contains(csp, "img-src 'self' data:");
        Assert.IsFalse(csp.Contains("blob:", StringComparison.Ordinal),
            "blob: crept into the policy — the photo path is built to not need it");
        Assert.IsFalse(csp.Contains("'unsafe-eval'", StringComparison.Ordinal),
            "'unsafe-eval' is much broader than the 'wasm-unsafe-eval' this needs");
    }
}
