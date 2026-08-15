using Microsoft.AspNetCore.DataProtection;
using Smser.Web.Services;

namespace Smser.Tests;

/// <summary>
/// The two bot rules, in isolation.
///
/// The rules themselves are three lines each; what these pin down is the behaviour around
/// the edges, which is where a rule like this does its damage. A honeypot that is too eager
/// or a timestamp that fails closed does not look broken — it looks like the form silently
/// refusing to work, for some fraction of real people, with no error anyone can reproduce.
/// </summary>
[TestClass]
public class FormGuardTests
{
    /// <summary>
    /// A clock the test moves by hand. Sleeping for real would make the elapsed-time rule
    /// cost a second and a half per assertion and still be timing-dependent on CI.
    /// </summary>
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static FormGuard Guard(out TestClock clock)
    {
        clock = new TestClock();

        // Ephemeral keys: this needs a working protector, not a persisted one.
        return new FormGuard(DataProtectionProvider.Create(nameof(FormGuardTests)), clock);
    }

    [TestMethod]
    public void A_form_filled_in_at_human_speed_passes()
    {
        var guard = Guard(out var clock);
        var token = guard.IssueTimestamp();

        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.AreEqual(FormVerdict.Ok, guard.Inspect(honeypot: "", token));
    }

    [TestMethod]
    public void Anything_in_the_honeypot_is_refused()
    {
        var guard = Guard(out var clock);
        var token = guard.IssueTimestamp();
        clock.Advance(TimeSpan.FromMinutes(5));

        // Even at a leisurely five minutes, and even for one character.
        Assert.AreEqual(FormVerdict.Honeypot, guard.Inspect("x", token));
    }

    [TestMethod]
    public void Whitespace_in_the_honeypot_is_not_a_bot()
    {
        // A stray space is not evidence of anything, and treating it as such would turn
        // any browser quirk that submits " " into a form nobody can use.
        var guard = Guard(out var clock);
        var token = guard.IssueTimestamp();
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.AreEqual(FormVerdict.Ok, guard.Inspect("   ", token));
    }

    [TestMethod]
    public void An_instant_submission_is_refused()
    {
        var guard = Guard(out _);
        var token = guard.IssueTimestamp();

        // No clock movement at all: posted back in the same instant it was rendered.
        Assert.AreEqual(FormVerdict.TooFast, guard.Inspect(honeypot: null, token));
    }

    [TestMethod]
    public void Someone_who_is_merely_quick_still_gets_through()
    {
        // The floor is meant to catch scripts, not fast people. Two seconds is a plausible
        // gap between an Import re-rendering the page and a decisive click on Generate.
        var guard = Guard(out var clock);
        var token = guard.IssueTimestamp();

        clock.Advance(TimeSpan.FromSeconds(2));

        Assert.AreEqual(FormVerdict.Ok, guard.Inspect(honeypot: null, token));
    }

    [TestMethod]
    public void A_missing_timestamp_is_allowed_through()
    {
        // Deliberate. Data protection keys do not survive a restart unless persisted, so
        // failing closed here would reject the next save from everyone holding a rendered
        // page every time the app deploys. See the comment in FormGuard.
        var guard = Guard(out _);

        Assert.AreEqual(FormVerdict.Ok, guard.Inspect(honeypot: null, timestamp: null));
        Assert.AreEqual(FormVerdict.Ok, guard.Inspect(honeypot: null, timestamp: ""));
    }

    [TestMethod]
    public void A_timestamp_this_app_cannot_read_is_allowed_through()
    {
        var guard = Guard(out _);

        // Garbage, and a token minted under a different key ring — which is what every
        // token in flight looks like after a key rotation.
        var stranger = new FormGuard(DataProtectionProvider.Create("somebody-else"), new TestClock());

        Assert.AreEqual(FormVerdict.Ok, guard.Inspect(honeypot: null, "not-a-token"));
        Assert.AreEqual(FormVerdict.Ok, guard.Inspect(honeypot: null, stranger.IssueTimestamp()));
    }

    [TestMethod]
    public void A_back_dated_timestamp_does_not_help()
    {
        // The whole reason the timestamp is encrypted rather than written plainly: a caller
        // who works out what the field is for cannot mint one that claims to be older.
        var guard = Guard(out _);

        var forged = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds().ToString();

        // Unreadable rather than believed — it falls into the fail-open path above, so the
        // honeypot and the rate limiter are what catch this caller, not this rule.
        Assert.AreEqual(FormVerdict.Ok, guard.Inspect(honeypot: null, forged));
        Assert.AreNotEqual(forged, guard.IssueTimestamp(),
            "the timestamp must not be readable, or it can be back-dated at will");
    }

    [TestMethod]
    public void An_import_round_trip_does_not_restart_the_clock()
    {
        // The regression this rule nearly shipped with. The roster form posts to itself for
        // Import before anything is saved, so re-minting the timestamp on each render meant
        // the clock restarted mid-flow and pressing Generate straight after an Import was
        // refused as a robot. Caught by driving a real browser, not by any test above.
        var guard = Guard(out var clock);

        var first = guard.IssueTimestamp();
        clock.Advance(TimeSpan.FromSeconds(20));

        var carried = guard.CarryOrIssue(first);

        Assert.AreEqual(first, carried, "the Import round trip minted a new timestamp");

        // Half a second later — far inside the floor, but twenty seconds after the form was
        // actually put on screen, which is the thing being measured.
        clock.Advance(TimeSpan.FromMilliseconds(500));

        Assert.AreEqual(FormVerdict.Ok, guard.Inspect(honeypot: null, carried));
    }

    [TestMethod]
    public void A_timestamp_that_cannot_be_read_is_replaced_rather_than_carried()
    {
        var guard = Guard(out _);

        var issued = guard.CarryOrIssue("not-a-token");

        Assert.AreNotEqual("not-a-token", issued);
        Assert.AreEqual(FormVerdict.TooFast, guard.Inspect(honeypot: null, issued),
            "the replacement should be a live timestamp, starting from now");
    }

    [TestMethod]
    public void A_form_rendered_fresh_gets_a_new_timestamp()
    {
        var guard = Guard(out _);

        Assert.AreNotEqual(string.Empty, guard.CarryOrIssue(null));
        Assert.AreNotEqual(string.Empty, guard.CarryOrIssue(""));
    }

    [TestMethod]
    public void The_honeypot_is_checked_before_the_clock()
    {
        // Both rules broken at once should report the honeypot, because that is the
        // unambiguous one — a filled hidden field has no innocent explanation, where a
        // fast submission has several.
        var guard = Guard(out _);

        Assert.AreEqual(FormVerdict.Honeypot, guard.Inspect("spam", guard.IssueTimestamp()));
    }
}
