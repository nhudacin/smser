using System.Globalization;
using System.Text.RegularExpressions;
using Jint;

namespace Smser.Tests;

/// <summary>
/// The photo importer's rotation maths, executed rather than grepped for.
///
/// Every other test of photo-ocr.js asserts that some string appears in the served file.
/// That catches a deletion and nothing else, and the gap is not theoretical: the
/// orientation search shipped, passed CI, and picked the wrong answer on the first real
/// sideways photo it was given. The cause was one <c>Math.min(1, …)</c> in turned(), which
/// meant the function could only ever shrink — so the probes ran far below the size where
/// tesseract's confidence tracks orientation at all, and the winning re-read was then held
/// down there too and discarded for scoring worse than the sideways read it was replacing.
/// Every string assertion in the suite passed throughout.
///
/// So turned() is pulled out of the served script and run here on Jint, against a canvas
/// stub that records what it was asked to draw. No browser, no WebAssembly, no OCR — this
/// is only the geometry, which is the part that was wrong.
/// </summary>
[TestClass]
public class PhotoGeometryTests
{
    private static SmserApp _app = null!;
    private static string _script = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        _app = new SmserApp();
        _script = await _app.GetPageAsync("/js/photo-ocr.js");
    }

    [ClassCleanup]
    public static void Stop() => _app.Dispose();

    [TestMethod]
    public void A_small_photo_is_enlarged_rather_than_left_alone()
    {
        // The regression. A photo pasted on iOS arrives around 600×800, and under the old
        // clamp came back at exactly that — the scale factor could not exceed 1, so asking
        // for a 1600px read of an 800px image quietly got you the 800px image.
        var canvas = Turn(600, 800, degrees: 0, maxEdge: 1600);

        Assert.AreEqual(1200, canvas.Width, "a 600×800 photo asked for at 1600 must be enlarged to it");
        Assert.AreEqual(1600, canvas.Height);
    }

    [TestMethod]
    public void A_quarter_turn_swaps_the_edges_and_still_enlarges()
    {
        // Both halves at once, because they interact: the scale has to be measured against
        // the image the right way up, or a page about to be stood upright gets sized as if
        // it were still on its side.
        var canvas = Turn(600, 800, degrees: 270, maxEdge: 1600);

        Assert.AreEqual(1600, canvas.Width, "the 800px edge becomes the width and is scaled to maxEdge");
        Assert.AreEqual(1200, canvas.Height);
    }

    [TestMethod]
    public void A_large_photo_still_comes_down()
    {
        // The behaviour that was already right and must stay right. A 4000px phone photo
        // costs several seconds of OCR for detail tesseract cannot use.
        var canvas = Turn(4000, 3000, degrees: 0, maxEdge: 2000);

        Assert.AreEqual(2000, canvas.Width);
        Assert.AreEqual(1500, canvas.Height);
    }

    [TestMethod]
    public void Enlargement_is_capped()
    {
        // Upscaling buys accuracy up to a point. Past that it only buys OCR time on a page
        // that was never going to read, so a thumbnail is not blown up to the full edge.
        var canvas = Turn(100, 100, degrees: 0, maxEdge: 2000);

        Assert.AreEqual(400, canvas.Width, "a 100px image must stop at the upscale cap, not reach 2000");
        Assert.AreEqual(400, canvas.Height);
    }

    [TestMethod]
    public void The_probes_are_large_enough_for_confidence_to_mean_anything()
    {
        // The measurement this whole change came from. On the sideways 600×800 roster,
        // confidence by orientation was:
        //
        //     edge    0°   90°  180°  270° (the correct one)
        //      500    22    29    41    34   → picked 180°
        //      800    37    21    29    26   → picked 0°
        //     1600    39    30    37    44   → picked 270°
        //
        // Below roughly 1600 the ranking is noise. This asserts the composition rather
        // than the constant — what matters is the size a real pasted photo is *probed* at,
        // which is PROBE_EDGE and the scaling put together.
        var canvas = Turn(600, 800, degrees: 270, maxEdge: ProbeEdge());

        Assert.IsTrue(Math.Max(canvas.Width, canvas.Height) >= 1600,
            $"probes of a pasted photo came out at {canvas.Width}×{canvas.Height}; below about " +
            "1600 tesseract's confidence does not track orientation and the search picks noise");
    }

    [TestMethod]
    public void A_quarter_turn_anticlockwise_translates_before_it_rotates()
    {
        // Order matters and is easy to get backwards. For 270 the origin moves to the
        // bottom-left corner and the canvas then rotates a negative quarter turn; getting
        // this wrong draws the page off the canvas entirely and reads as a blank photo.
        var canvas = Turn(600, 800, degrees: 270, maxEdge: 1600);

        Assert.AreEqual(
            "translate 0 1200 | rotate -1.5708 | draw 1200 1600",
            canvas.Calls,
            "a 270° turn must translate to the bottom-left, then rotate anticlockwise");
    }

    [TestMethod]
    public void An_upright_page_is_drawn_without_any_transform()
    {
        // The no-op case, which is the one every photo that was already the right way up
        // takes. A stray transform here would turn correct pages into broken ones.
        var canvas = Turn(600, 800, degrees: 0, maxEdge: 1600);

        Assert.AreEqual("draw 1200 1600", canvas.Calls,
            "0° must draw straight onto the canvas");
    }

    // ── running the real function ───────────────────────────────────────────

    /// <summary>
    /// The canvas turned() produced, and the drawing calls it made, joined into one line
    /// so a failure prints the sequence that was wrong rather than the index of the first
    /// character that differed.
    /// </summary>
    private sealed record Canvas(int Width, int Height, string Calls);

    /// <summary>
    /// Runs the served turned() against a stub source and canvas, and reports the canvas it
    /// produced along with the drawing calls it made.
    /// </summary>
    private static Canvas Turn(int width, int height, int degrees, int maxEdge)
    {
        var engine = new Engine();

        // Just enough DOM for turned() to run: a canvas that remembers the size it was
        // given and a context that writes down what it was asked to do. Rounded because a
        // float comparison across two languages is a test that fails for no reason.
        engine.Execute(
            """
            var calls = [];
            var made = null;
            var document = {
                createElement: function () {
                    made = {
                        width: 0,
                        height: 0,
                        getContext: function () {
                            return {
                                imageSmoothingEnabled: false,
                                imageSmoothingQuality: '',
                                translate: function (x, y) { calls.push('translate ' + x + ' ' + y); },
                                rotate: function (a) { calls.push('rotate ' + a.toFixed(4)); },
                                drawImage: function (s, x, y, w, h) { calls.push('draw ' + w + ' ' + h); }
                            };
                        }
                    };
                    return made;
                }
            };
            """);

        // The constants turned() closes over, taken from the served file rather than
        // repeated here — a test that hardcodes them cannot notice them changing.
        engine.Execute($"var MAX_UPSCALE = {Constant("MAX_UPSCALE")};");
        engine.Execute(Function("turned"));

        engine.Execute(
            $$"""
            var source = { width: {{width}}, height: {{height}} };
            var canvas = turned(source, {{degrees}}, {{maxEdge}});
            """);

        return new Canvas(
            (int)engine.Evaluate("canvas.width").AsNumber(),
            (int)engine.Evaluate("canvas.height").AsNumber(),
            engine.Evaluate("calls.join(' | ')").AsString());
    }

    private static int ProbeEdge() =>
        int.Parse(Constant("PROBE_EDGE"), CultureInfo.InvariantCulture);

    private static string Constant(string name)
    {
        var match = Regex.Match(_script, $@"\bvar\s+{Regex.Escape(name)}\s*=\s*(\d+)\s*;");

        Assert.IsTrue(match.Success, $"photo-ocr.js no longer declares {name}");

        return match.Groups[1].Value;
    }

    /// <summary>
    /// Lifts a named function out of the served script by matching braces. Cruder than a
    /// parser, and sufficient: the alternative is a copy of the function in this file,
    /// which would keep passing after the real one broke.
    /// </summary>
    private static string Function(string name)
    {
        var start = _script.IndexOf($"function {name}(", StringComparison.Ordinal);

        Assert.IsTrue(start >= 0, $"photo-ocr.js no longer declares a {name}() to test");

        var depth = 0;

        for (var i = _script.IndexOf('{', start); i < _script.Length; i++)
        {
            if (_script[i] == '{')
            {
                depth++;
            }
            else if (_script[i] == '}' && --depth == 0)
            {
                return _script[start..(i + 1)];
            }
        }

        throw new InvalidOperationException($"{name}() in photo-ocr.js has unbalanced braces");
    }
}
