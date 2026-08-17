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
    public void The_tablist_is_hidden_until_script_reveals_it()
    {
        // None of the photo path works without JavaScript. A tab that switches to a dead
        // control is worse than no tab, so the tablist ships hidden and photo-ocr.js is
        // what turns it on — the job the photo field itself used to do before the tabs.
        Assert.IsTrue(
            Regex.IsMatch(_page, @"<div class=""tabs"" data-import-tabs role=""tablist""[^>]*\shidden"),
            "the tablist must render with the hidden attribute");
    }

    [TestMethod]
    public void The_photo_panel_is_hidden_until_script_reveals_it()
    {
        // The other half of the same guarantee: with no script the tabs never appear, so
        // a photo panel that did not ship hidden would simply be stacked below the
        // textarea with nothing able to hide it again.
        Assert.IsTrue(
            Regex.IsMatch(_page, @"id=""panel-photo""[^>]*\shidden"),
            "the photo panel must render with the hidden attribute");
    }

    [TestMethod]
    public void The_paste_panel_ships_visible()
    {
        // With JavaScript off this is the whole field: label, textarea, hint, exactly as
        // it was before the tabs existed. Shipping this panel hidden too — the easy
        // mistake when adding a third tab, say — would leave a page with no way to enter
        // a roster at all, and every other test here would still pass.
        var panel = Regex.Match(_page, @"<div class=""tab-panel"" id=""panel-paste""[^>]*>");

        Assert.IsTrue(panel.Success, "the paste panel is missing");
        Assert.IsFalse(panel.Value.Contains("hidden", StringComparison.Ordinal),
            "the paste panel must not ship hidden — it is the no-JavaScript form");
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
    public void The_paste_button_is_hidden_until_script_reveals_it()
    {
        // Same rule again, and it bites harder here: a browser that cannot read the
        // clipboard gets a button that names a thing it cannot do. .photo-actions has a
        // CSS rule to make this attribute actually take effect on a .button — see the
        // comment there — so shipping it visible would defeat both halves at once.
        Assert.IsTrue(
            Regex.IsMatch(_page, @"data-photo-paste hidden"),
            "the paste button must render with the hidden attribute");
    }

    [TestMethod]
    public void The_paste_button_does_not_submit_the_form()
    {
        // Inside a form a bare <button> submits it, which here would post the roster
        // half-finished instead of reading the clipboard. The other photo controls carry
        // type="button" for the same reason.
        var button = Regex.Match(_page, @"<button[^>]*data-photo-paste[^>]*>").Value;

        Assert.AreNotEqual(string.Empty, button, "the paste button is missing");
        StringAssert.Contains(button, @"type=""button""");
    }

    [TestMethod]
    public async Task The_paste_button_is_wired_to_a_clipboard_read()
    {
        // The button and the read are two halves of one feature and only one is visible.
        // This is also the only paste path a phone has: the paste *event* below needs a
        // focused editable element to fire into, and this panel has none.
        var script = await _app.GetPageAsync("/js/photo-ocr.js");

        StringAssert.Contains(script, "navigator.clipboard.read()",
            "the paste button is revealed but nothing reads the clipboard");
    }

    [TestMethod]
    public async Task Reading_the_clipboard_is_not_deferred_behind_an_await()
    {
        // Reading the clipboard is something a user gesture permits, not something the page
        // may do whenever it likes. Both Safari and Chrome have dropped that permission by
        // the time an awaited promise resolves, so moving the call off the top of the click
        // handler breaks the feature on every device it was added for — and breaks it as a
        // silently rejected promise, which looks exactly like a denied permission.
        var script = await _app.GetPageAsync("/js/photo-ocr.js");

        StringAssert.Contains(script, "reading = navigator.clipboard.read();",
            "the clipboard read must stay a direct call in the click handler");
    }

    [TestMethod]
    public async Task A_page_that_reads_badly_is_tried_every_way_up()
    {
        // EXIF is the cheap answer and is tried first, but it only exists when the camera
        // wrote a tag — a roster photographed sideways on a table has none, and then the
        // pixels are the only evidence there is. Without this the read is gibberish and
        // nothing anywhere reports a problem, because a confident misreading and a correct
        // one look identical from the outside.
        var script = await _app.GetPageAsync("/js/photo-ocr.js");

        StringAssert.Contains(script, "findUpright",
            "a low-confidence read is accepted as final, so a sideways page stays sideways");
        StringAssert.Contains(script, "[0, 90, 180, 270]",
            "the orientation search must consider every quarter turn");
    }

    [TestMethod]
    public async Task Turning_the_page_cannot_make_the_read_worse()
    {
        // The probes are small and can be wrong. A rotated read is only allowed to replace
        // the original by beating it, so the worst case of a bad guess is wasted seconds
        // rather than a worse result than not having tried.
        var script = await _app.GetPageAsync("/js/photo-ocr.js");

        StringAssert.Contains(script, "second.confidence > first.confidence",
            "a turned read must have to beat the one it replaces");
    }

    [TestMethod]
    public async Task The_orientation_search_reuses_one_worker()
    {
        // createWorker re-initialises the WebAssembly core each time. The search can want
        // five reads, and paying that initialisation five times would cost more than the
        // reads it is there to make.
        var script = await _app.GetPageAsync("/js/photo-ocr.js");

        Assert.AreEqual(1, Regex.Matches(script, @"Tesseract\.createWorker").Count,
            "the engine should be started once per photo, not once per read");
    }

    [TestMethod]
    public void The_paste_fallback_ships_hidden()
    {
        // It is the answer to a failure that has not happened yet. Shown up front it would
        // be a second paste control sitting beside the button that usually works.
        Assert.IsTrue(
            Regex.IsMatch(_page, @"data-photo-paste-fallback hidden"),
            "the paste fallback must render with the hidden attribute");
    }

    [TestMethod]
    public void The_paste_fallback_is_something_a_paste_can_land_in()
    {
        // The whole point of it. A paste event needs an editable element to fire into, and
        // on iOS that is the only way to get an image off the clipboard at all — Safari's
        // clipboard.read() hands back an item that describes itself as holding nothing.
        // Without contenteditable this is a div, and a div cannot be pasted into.
        var target = Regex.Match(_page, @"<div[^>]*data-photo-paste-target[^>]*>").Value;

        Assert.AreNotEqual(string.Empty, target, "the paste target is missing");
        StringAssert.Contains(target, "contenteditable=\"true\"");
    }

    [TestMethod]
    public async Task An_unadvertised_clipboard_item_is_still_asked_for_an_image()
    {
        // Safari on iOS returns one ClipboardItem with an empty types list for a photo
        // copied out of Photos. Trusting that list means never finding an image that is
        // demonstrably there, so the types are probed rather than read.
        var script = await _app.GetPageAsync("/js/photo-ocr.js");

        StringAssert.Contains(script, "probeForImage",
            "an item that advertises no types is taken at its word, which loses the photo");
    }

    [TestMethod]
    public void The_diagnostics_panel_ships_hidden()
    {
        // It is a debugging aid, not part of the app. Shipping it visible would put a wall
        // of user-agent strings and byte counts under the roster form for everybody.
        Assert.IsTrue(
            Regex.IsMatch(_page, @"data-photo-debug hidden"),
            "the diagnostics panel must render with the hidden attribute");
    }

    [TestMethod]
    public async Task The_diagnostics_are_opt_in()
    {
        // Revealed by ?debug=1 and nothing else. The check is a literal in the script, so a
        // rename that left the markup behind would silently turn diagnostics off for good —
        // and the only way anyone would find out is the next time they were needed.
        var script = await _app.GetPageAsync("/js/photo-ocr.js");

        StringAssert.Contains(script, "debug=1",
            "nothing in the script reveals the diagnostics panel");
    }

    [TestMethod]
    public async Task Diagnostics_do_not_import_over_themselves()
    {
        // Import is a form post, so it navigates — and everything the diagnostics recorded
        // goes with the old document. Auto-importing would wipe the log at the exact moment
        // it became worth reading, which is the whole reason the panel exists.
        var script = await _app.GetPageAsync("/js/photo-ocr.js");

        StringAssert.Contains(script, "if (DEBUG) {",
            "the finished read must stop short of Import while diagnostics are on");
    }

    [TestMethod]
    public async Task The_photo_is_turned_upright_when_the_decoder_did_not()
    {
        // A decoder is free to accept imageOrientation: 'from-image' and ignore it, which
        // hands the OCR a page lying on its side — and that fails as gibberish rather than
        // as an error, so nothing else notices. Only a phone writes the tag, which is why
        // this can only ever go wrong on the device hardest to debug.
        var script = await _app.GetPageAsync("/js/photo-ocr.js");

        StringAssert.Contains(script, "readJpegHeader",
            "nothing reads the stored orientation, so nothing can tell whether the " +
            "decoder applied it");
        StringAssert.Contains(script, "context.rotate(",
            "the orientation is read but never acted on");
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
    public async Task A_finished_read_returns_to_the_paste_tab()
    {
        // The reason the tabs exist. The photo tab fills the paste box, so once it has,
        // it hands back — landing the reader on the text they can now correct rather than
        // on a spent progress bar. Nothing in the markup shows whether this still happens.
        var script = await _app.GetPageAsync("/js/photo-ocr.js");
        var finish = script[script.IndexOf("function finish(", StringComparison.Ordinal)..];

        StringAssert.Contains(finish, "selectTab('paste')",
            "a finished read must return to the paste tab, where the text it read now is");
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
