using System.Net;
using System.Text.RegularExpressions;
using Smser.Web.Services;

namespace Smser.Tests;

/// <summary>
/// The bot checks as the page actually renders and enforces them.
///
/// The rules are unit tested in <see cref="FormGuardTests"/>; what is checked here is the
/// half that lives in markup, which is the half that rots. A honeypot rendered as
/// <c>type="hidden"</c>, or left in the tab order, or echoed back with the value that was
/// posted, is still a honeypot by every unit test and is useless, invisible to a keyboard
/// user, or a permanent lockout respectively.
/// </summary>
[TestClass]
public class BotProtectionTests
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

    private static string Honeypot(string html) =>
        Regex.Match(html, $@"<input[^>]*name=""{FormGuard.HoneypotField}""[^>]*>").Value;

    private static string Token(string html) =>
        Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;

    [TestMethod]
    public void The_honeypot_is_on_the_form()
    {
        Assert.AreNotEqual(string.Empty, Honeypot(_page), "the honeypot field is not being rendered");
    }

    [TestMethod]
    public void The_honeypot_is_not_a_hidden_input()
    {
        // The bots this catches skip type="hidden" precisely because it is where honeypots
        // are usually put. It has to be an input they would plausibly fill in.
        var field = Honeypot(_page);

        Assert.IsFalse(field.Contains(@"type=""hidden""", StringComparison.OrdinalIgnoreCase),
            "a hidden input is the one kind of honeypot the bots worth catching skip");
        StringAssert.Contains(field, @"type=""text""");
    }

    [TestMethod]
    public void The_honeypot_is_kept_away_from_people()
    {
        // Off screen for anyone looking, out of the tab order for anyone on a keyboard, and
        // hidden from assistive tech. Losing any one of these turns the trap on the people
        // it is supposed to be invisible to.
        var field = Honeypot(_page);

        StringAssert.Contains(field, @"tabindex=""-1""", "a keyboard user could tab into the trap");
        StringAssert.Contains(field, @"autocomplete=""off""", "the browser could fill the trap in");
        Assert.IsTrue(Regex.IsMatch(_page, @"<div class=""offscreen"" aria-hidden=""true"">"),
            "the honeypot's wrapper must be off screen and hidden from assistive technology");
    }

    [TestMethod]
    public async Task The_stylesheet_hides_the_honeypot_without_display_none()
    {
        // If .offscreen ever becomes display:none the field stops being bait, because the
        // bots check for exactly that. This asserts on the rule, since nothing else would
        // notice the trap had quietly stopped working. Read over HTTP rather than off disk,
        // so it is the stylesheet the app actually serves that is being checked.
        var css = await _app.GetPageAsync("/css/site.css");

        var rule = Regex.Match(css, @"\.offscreen\s*\{([^}]*)\}").Groups[1].Value;

        Assert.AreNotEqual(string.Empty, rule, "the .offscreen rule is gone");
        StringAssert.Contains(rule, "position: absolute");
        Assert.IsFalse(rule.Contains("display: none", StringComparison.OrdinalIgnoreCase),
            "display:none stops this being a honeypot — the bots skip it");
        Assert.IsFalse(rule.Contains("visibility: hidden", StringComparison.OrdinalIgnoreCase),
            "visibility:hidden stops this being a honeypot for the same reason");
    }

    [TestMethod]
    public void The_honeypot_never_renders_with_a_value()
    {
        // The lockout case. If a password manager fills this in and the page echoes it
        // back, that person can never submit the form again — every retry re-posts it.
        StringAssert.Contains(Honeypot(_page), @"value=""""");
    }

    [TestMethod]
    public void The_timestamp_is_on_the_form_and_is_not_readable()
    {
        var field = Regex.Match(_page, $@"<input[^>]*name=""{FormGuard.TimestampField}""[^>]*value=""([^""]*)""");

        Assert.IsTrue(field.Success, "the form timestamp is not being rendered");

        var value = field.Groups[1].Value;

        Assert.AreNotEqual(string.Empty, value, "an empty timestamp disables the elapsed-time rule");
        Assert.IsFalse(long.TryParse(value, out _),
            "the timestamp is in the clear, so anyone can back-date it");
    }

    [TestMethod]
    public async Task A_submission_that_fills_the_honeypot_is_refused()
    {
        using var app = new SmserApp();
        var page = await app.GetPageAsync("/new");

        var response = await app.PostFormRawAsync("/new", new()
        {
            ["__RequestVerificationToken"] = Token(page),
            ["Input.GroupName"] = "soccer team 2023",
            ["Input.Numbers"] = "(219) 555-0113",
            [FormGuard.HoneypotField] = "https://example.com"
        });

        // A save that goes through answers 302 to its own new URL. Re-rendering the form
        // is what "nothing was written" looks like from out here — and it is also proof
        // the refusal happened before the storage call, which no test here could satisfy.
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            "the roster was saved despite the honeypot being filled in");

        StringAssert.Contains(await response.Content.ReadAsStringAsync(),
            "did not look like a form a person filled in");
    }

    [TestMethod]
    public async Task An_ordinary_submission_is_not_accused_of_being_a_bot()
    {
        // The other half of the previous test, and the one that would catch the guard
        // firing on everybody: an empty form should complain about the missing numbers,
        // not about being a robot.
        using var app = new SmserApp();
        var page = await app.GetPageAsync("/new");

        var response = await app.PostFormRawAsync("/new", new()
        {
            ["__RequestVerificationToken"] = Token(page),
            ["Input.GroupName"] = "soccer team 2023",
            ["Input.Numbers"] = ""
        });

        var html = await response.Content.ReadAsStringAsync();

        Assert.IsFalse(html.Contains("did not look like a form a person filled in", StringComparison.Ordinal),
            "an ordinary submission was refused as a bot");
        StringAssert.Contains(html, "No usable phone numbers here");
    }
}
