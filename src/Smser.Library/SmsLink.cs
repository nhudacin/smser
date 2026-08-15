namespace Smser.Library;

/// <summary>
/// Builds the <c>sms:</c> URL that opens a phone's messaging app with the whole roster
/// already in the To: field.
/// </summary>
public static class SmsLink
{
    /// <summary>
    /// The <c>sms://open?addresses=</c> form, carried over unchanged from the original
    /// app because it is the one that works on both platforms in practice.
    ///
    /// It is not what RFC 5724 specifies — the standard form is <c>sms:</c> followed by
    /// comma-separated numbers, with no authority and no query string. iOS accepts this
    /// variant and Android's messaging apps key off the <c>addresses</c> parameter, and
    /// the combination is what actually opens a group draft on a real handset. Changing
    /// it to the RFC form breaks Android silently — the app opens with an empty
    /// recipient list rather than erroring.
    /// </summary>
    private const string Prefix = "sms://open?addresses=";

    /// <summary>
    /// Builds the link for <paramref name="numbers"/>, which are expected to be
    /// normalised 11-digit values as produced by <see cref="PhoneNumberParser.Parse"/>.
    /// An empty roster yields an empty string rather than a link with nothing after the
    /// <c>=</c>, so callers can test it for emptiness directly.
    /// </summary>
    public static string Build(IEnumerable<string> numbers)
    {
        var joined = string.Join(',', numbers);

        return joined.Length == 0 ? string.Empty : Prefix + joined;
    }
}
