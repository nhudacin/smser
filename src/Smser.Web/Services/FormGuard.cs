using Microsoft.AspNetCore.DataProtection;

namespace Smser.Web.Services;

/// <summary>What a form submission looked like. Anything but <see cref="Ok"/> is not saved.</summary>
public enum FormVerdict
{
    /// <summary>Looks like a person filled it in.</summary>
    Ok,

    /// <summary>The hidden field came back with something in it. Only a machine fills that.</summary>
    Honeypot,

    /// <summary>Submitted faster than a person can read the page, let alone type into it.</summary>
    TooFast
}

/// <summary>
/// Two cheap checks that a form was filled in by a person rather than posted by a script.
///
/// Neither is a wall, and neither is meant to be. Saving a roster is anonymous by design,
/// so the defence is layered: antiforgery already forces a bot to fetch the page before it
/// can post to it, the rate limiter caps how often it can, and these two catch the large
/// remaining class of bot that fetches, fills in every input it can find, and posts back
/// immediately. What gets past all of that is a bot written for this app specifically,
/// which no amount of this stops — the rate limit is what bounds the damage there.
///
/// The deliberate non-goal is a captcha. This app tells people no third party ever sees
/// their roster, and every captcha worth having is a third-party script on the page that
/// posts to a third-party host, on the one page where the roster lives.
/// </summary>
public sealed class FormGuard
{
    /// <summary>
    /// Field name for the issued timestamp. Short and meaningless-looking on purpose —
    /// it sits next to the antiforgery token in the markup and is no more interesting.
    /// </summary>
    public const string TimestampField = "__ts";

    /// <summary>
    /// Field name for the honeypot. This form has no website field and never will, so
    /// anything arriving in it was put there by something filling in inputs by rote.
    ///
    /// Named for bait value, but chosen to be one of the few plausible-looking names
    /// browser autofill does not treat as a profile field — an <c>Email</c> or
    /// <c>Address</c> honeypot gets filled in by the browser itself and locks real people
    /// out of the form.
    /// </summary>
    public const string HoneypotField = "Website";

    /// <summary>
    /// Floor on how long the form must have been on screen before a save is believed.
    ///
    /// Deliberately far below human speed rather than near it. The target is the script
    /// that posts back in single-digit milliseconds; anything tuned closer to how fast a
    /// person *could* click starts rejecting people who are simply quick, and this form
    /// legitimately gets submitted seconds after an Import re-renders it.
    /// </summary>
    private static readonly TimeSpan MinimumOnScreen = TimeSpan.FromMilliseconds(1500);

    // Versioned: changing the payload format later must invalidate old tokens rather than
    // silently misread them.
    private const string Purpose = "Smser.FormGuard.v1";

    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;

    public FormGuard(IDataProtectionProvider protection, TimeProvider clock)
    {
        _protector = protection.CreateProtector(Purpose);
        _clock = clock;
    }

    /// <summary>
    /// Mints the timestamp that goes in the form. Encrypted rather than written plainly,
    /// so it cannot be back-dated by a caller who noticed what it is for.
    /// </summary>
    public string IssueTimestamp() =>
        _protector.Protect(_clock.GetUtcNow().ToUnixTimeMilliseconds().ToString());

    /// <summary>
    /// The timestamp to put on the form being sent back: the one that came in, if it can
    /// still be read, and a new one otherwise.
    ///
    /// Carrying it through is what makes the elapsed-time rule measure the right thing —
    /// how long this person has had the form, not how long since the last round trip. The
    /// roster form posts to itself for Import before it is ever saved, so minting a new
    /// one on each render restarts the clock mid-flow, and someone who presses Generate
    /// straight after an Import gets told they are a robot. The photo importer makes that
    /// worse by running the Import automatically, leaving a gap measured in milliseconds.
    /// </summary>
    public string CarryOrIssue(string? posted) => Readable(posted) ? posted! : IssueTimestamp();

    private bool Readable(string? timestamp)
    {
        if (string.IsNullOrEmpty(timestamp)) return false;

        try
        {
            return long.TryParse(_protector.Unprotect(timestamp), out _);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Judges a submission. <paramref name="honeypot"/> and <paramref name="timestamp"/>
    /// are the posted values of <see cref="HoneypotField"/> and
    /// <see cref="TimestampField"/>.
    /// </summary>
    public FormVerdict Inspect(string? honeypot, string? timestamp)
    {
        if (!string.IsNullOrWhiteSpace(honeypot)) return FormVerdict.Honeypot;

        // A missing or unreadable timestamp is not treated as bot traffic, and that is a
        // decision rather than an oversight. Data protection keys do not survive a restart
        // on App Service unless they are persisted, so every deploy would otherwise reject
        // the next save from everyone holding an already-rendered page. Failing open here
        // costs one of three layers against a bot that strips the field; failing closed
        // costs real saves every time the app restarts.
        if (string.IsNullOrEmpty(timestamp)) return FormVerdict.Ok;

        long issued;
        try
        {
            if (!long.TryParse(_protector.Unprotect(timestamp), out issued)) return FormVerdict.Ok;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return FormVerdict.Ok;
        }

        var age = _clock.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(issued);

        // Only the floor is enforced. There is no ceiling on purpose: a tab left open over
        // lunch is a real person, and antiforgery already has its own expiry for the case
        // that actually matters.
        return age < MinimumOnScreen ? FormVerdict.TooFast : FormVerdict.Ok;
    }
}
