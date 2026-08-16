using System.Net;
using System.Text.RegularExpressions;

namespace Smser.Tests;

/// <summary>
/// The parts of the "Sideline" redesign that fail silently.
///
/// A visual regression is usually obvious the moment someone looks at the page. These are
/// the ones that are not: a stylesheet the policy refuses to apply, a font that 404s and
/// falls back to Georgia, a QR code that stops scanning in dark mode. Each is invisible
/// in the light-mode browser window the change was made in.
/// </summary>
[TestClass]
public class AppearanceWiringTests
{
    private static SmserApp _app = null!;
    private static string _home = null!;
    private static string _new = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        _app = new SmserApp();
        _home = await _app.GetPageAsync("/");
        _new = await _app.GetPageAsync("/new");
    }

    [ClassCleanup]
    public static void Stop() => _app.Dispose();

    [TestMethod]
    public void No_page_carries_an_inline_style_attribute()
    {
        // The single hardest constraint in the handoff, and the easiest to break: the
        // design arrived as prototypes built entirely from inline style attributes, and
        // style-src has no 'unsafe-inline'. A style attribute copied across from a
        // prototype is not a policy violation the browser reports usefully — it simply
        // drops the declaration, so the element renders unstyled and nothing says why.
        foreach (var (name, html) in new[] { ("/", _home), ("/new", _new) })
        {
            Assert.IsFalse(Regex.IsMatch(html, @"<[^>]+\sstyle\s*="),
                $"{name} has an inline style attribute, which the CSP will drop");
        }
    }

    [TestMethod]
    public async Task Every_weight_of_the_display_face_is_served()
    {
        // Bitter is self-hosted so the policy needs no third-party origins. A missing file
        // is a silent fallback to Georgia — the page still renders, just not as designed.
        string[] faces =
        [
            "/lib/bitter/bitter-latin.woff2",
            "/lib/bitter/bitter-latin-ext.woff2",
            "/lib/bitter/bitter-italic-latin.woff2",
            "/lib/bitter/bitter-italic-latin-ext.woff2"
        ];

        foreach (var face in faces)
        {
            var response = await _app.HeadAsync(face);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"{face} is not being served");
        }
    }

    [TestMethod]
    public async Task The_stylesheet_asks_for_the_faces_that_exist()
    {
        // The @font-face src and the files on disk have to stay in step. Renaming one
        // without the other leaves a policy-clean page with no display face on it.
        var css = await _app.GetPageAsync("/css/site.css");

        foreach (Match match in Regex.Matches(css, @"url\(""\.\./([^""]+)""\)"))
        {
            var response = await _app.HeadAsync("/" + match.Groups[1].Value);

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                $"site.css asks for {match.Groups[1].Value}, which is not served");
        }
    }

    [TestMethod]
    public async Task The_qr_code_keeps_a_literal_white_plate()
    {
        // The one colour in the file that must not become a variable. A QR symbol has to
        // stay dark-on-light to scan; var(--bg-raised) would invert it in dark mode and
        // the code would stop working for every camera — on the screen of the phone it is
        // most likely being scanned from.
        var css = await _app.GetPageAsync("/css/site.css");

        var rule = Regex.Match(css, @"\.qr\s*\{([^}]*)\}").Groups[1].Value;

        Assert.AreNotEqual(string.Empty, rule, "the .qr rule is gone");
        StringAssert.Contains(rule, "background: #ffffff",
            "the QR plate must be a literal white, or dark mode makes it unscannable");
    }

    [TestMethod]
    public void The_wordmark_is_type_rather_than_an_image()
    {
        // It used to be an SVG with viewBox="0 0" — invalid, so the browser fell back to a
        // 300px box and the glyphs floated inside it. Type has no aspect ratio to get
        // wrong.
        Assert.IsTrue(Regex.IsMatch(_home, @"<a class=""brand"" href=""/"">SMSer<span class=""brand-dot"">\.</span></a>"),
            "the header wordmark is not being rendered as type");

        var header = Regex.Match(_home, @"<header.*?</header>", RegexOptions.Singleline).Value;

        Assert.IsFalse(header.Contains("<img", StringComparison.OrdinalIgnoreCase),
            "the header still carries an image");
    }

    [TestMethod]
    public async Task The_replaced_svgs_are_gone()
    {
        // Deleting the markup reference is not deleting the asset. Both were text in a box
        // rather than drawn marks, and leaving them served invites their reuse.
        foreach (var asset in new[] { "/smser.svg", "/nick.svg" })
        {
            var response = await _app.HeadAsync(asset);

            Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode, $"{asset} is still being served");
        }
    }

    [TestMethod]
    public async Task Dark_is_chosen_rather_than_detected()
    {
        // The palette used to be behind prefers-color-scheme, which handed the decision to
        // the handset. Putting that media query back would silently take the default away
        // from every reader whose phone is set to dark — which is most of them — and the
        // switch would then disagree with what they see on first load.
        var css = await _app.GetPageAsync("/css/site.css");

        // Comments stripped first. The stylesheet names prefers-color-scheme in a comment
        // explaining why it is *not* used, and that comment is the thing most likely to
        // stop a future reader putting it back — so the assertion has to look at the rules
        // rather than the file.
        var rules = Regex.Replace(css, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        Assert.IsFalse(Regex.IsMatch(rules, @"@media[^{]*prefers-color-scheme"),
            "the OS preference must not pick the palette; dark is opt-in via the switch");
        StringAssert.Contains(css, ":root[data-theme=\"dark\"]",
            "the dark palette is gone — the switch would have nothing to turn on");
    }

    [TestMethod]
    public void The_theme_switch_is_hidden_until_script_reveals_it()
    {
        // Same rule as the photo control and the paste hint: without JavaScript it cannot
        // switch anything, and the page is light either way.
        Assert.IsTrue(Regex.IsMatch(_home, @"<button type=""button"" class=""theme-toggle"" data-theme-toggle aria-pressed=""false"" hidden>"),
            "the theme switch must render hidden, with aria-pressed");
    }

    [TestMethod]
    public async Task The_theme_script_is_loaded_before_the_page_paints()
    {
        // In <head>, not with site.js at the bottom. Loaded late it still works, but a
        // reader who chose dark gets a full flash of the light palette on every
        // navigation — which looks like a bug and is invisible to anyone testing in light.
        var head = Regex.Match(_home, @"<head>.*?</head>", RegexOptions.Singleline).Value;

        StringAssert.Contains(head, "/js/theme.js", "theme.js must load in <head>");

        var response = await _app.HeadAsync("/js/theme.js");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, "theme.js is not being served");
    }

    [TestMethod]
    public void The_step_numerals_are_hidden_from_assistive_technology()
    {
        // They are ornament — the headings already carry the order, and "one, Paste
        // anything" is worse than "Paste anything".
        Assert.AreEqual(3, Regex.Matches(_home, @"<p class=""card-step"" aria-hidden=""true"">").Count,
            "each feature card needs a decorative, aria-hidden numeral");
    }
}
