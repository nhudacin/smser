using System.Text.RegularExpressions;

namespace Smser.Library;

/// <summary>
/// Pulls North American phone numbers out of arbitrary pasted text.
///
/// This is the whole point of the app. The text people paste in is a team roster copied
/// off a phone screen, a screenshot run through the camera's "copy as text", or a chunk
/// of a group email — so it arrives as names, jersey numbers, addresses, dates and
/// e-mail addresses with the phone numbers scattered through it, in every format anyone
/// has ever typed one.
///
/// Two passes, because the input splits cleanly into two very different shapes:
///
///  1. <b>Separated numbers</b> — <c>(219) 555-0113</c>, <c>219.555.0113</c>,
///     <c>+1 219 555 0113</c>, <c>12195550113</c>. Matched by <see cref="Nanp"/>, which
///     is anchored with digit-boundary guards so it cannot match a fragment of a longer
///     digit run.
///  2. <b>Run-together digits</b> — <c>21955501133125550147</c>. OCR of a contact list
///     drops the separators between adjacent entries and yields one long run. Those runs
///     are excluded by the guards in pass 1 precisely so <see cref="SplitDigitRun"/> can
///     take them and chop them into numbers.
///
/// Numbers are validated against the real NANP rules — area code and exchange both start
/// 2-9, and neither is an N11 service code — which is what stops "Room 211 555 0113" and
/// "invoice 2023 4567" from parsing as phone numbers. Everything comes back normalised
/// to 11 digits (<c>1</c> + area + exchange + line), deduplicated, in the order it
/// appeared in the text.
///
/// Scope: NANP only (US/Canada/Caribbean). International numbers are not recognised —
/// the app builds an <c>sms:</c> link for a group text, which in practice is a NANP
/// operation, and a parser loose enough to accept arbitrary international formats would
/// accept most of the surrounding junk too.
/// </summary>
public static partial class PhoneNumberParser
{
    /// <summary>
    /// A separated NANP number, with an optional country code.
    ///
    /// The two digit-boundary guards are the load-bearing part. <c>(?&lt;!\d)</c> and
    /// <c>(?!\d)</c> mean a match must be a *whole* run of digits, not a window into a
    /// longer one — so <c>21955501133125550147</c> produces no match here at all and
    /// falls through to <see cref="SplitDigitRun"/>, and a mistyped
    /// <c>219-555-01139</c> is rejected outright rather than silently truncated to a
    /// wrong number that would then be texted.
    ///
    /// <c>(?!11)</c> after the leading digit of the area code and the exchange rejects
    /// N11 service codes (211, 411, 911...). Doing it in the pattern rather than as a
    /// filter afterwards matters: the engine backtracks and can still find a real number
    /// starting one character later, where a post-filter would have consumed the text
    /// and moved on.
    ///
    /// The parentheses are optional and unpaired on purpose — OCR routinely loses one of
    /// them, and <c>(219 555-0113</c> is unambiguous.
    /// </summary>
    [GeneratedRegex(
        @"(?<!\d)(?:\+?1[\s.\-]?)?\(?(?<area>[2-9](?!11)\d{2})\)?[\s.\-]?(?<exchange>[2-9](?!11)\d{2})[\s.\-]?(?<line>\d{4})(?!\d)",
        RegexOptions.CultureInvariant)]
    private static partial Regex Nanp();

    /// <summary>
    /// Shortest run of digits that pass 1 will not look at, and so the length at which
    /// <see cref="SplitDigitRun"/> takes over: 12. Ten digits is a bare number and
    /// eleven is a number with its country code, both of which pass 1 handles.
    /// </summary>
    private const int RunTogetherThreshold = 12;

    /// <summary>Number of digits in a normalised number: <c>1</c> + 10.</summary>
    public const int NormalisedLength = 11;

    /// <summary>
    /// Extracts every phone number from <paramref name="text"/>, normalised to
    /// <c>1XXXXXXXXXX</c>, deduplicated, in order of first appearance.
    /// </summary>
    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        // Both passes record where in the text they found each number so the combined
        // result can be put back into document order below. Without this the run-together
        // numbers would all sort after the separated ones regardless of where they
        // appeared, and the output would no longer line up with the roster the user
        // pasted in — which is the first thing they check.
        var found = new List<(int Index, string Number)>();

        foreach (Match match in Nanp().Matches(text))
        {
            found.Add((match.Index, string.Concat(
                "1",
                match.Groups["area"].ValueSpan,
                match.Groups["exchange"].ValueSpan,
                match.Groups["line"].ValueSpan)));
        }

        foreach (var (start, length) in DigitRuns(text))
        {
            if (length < RunTogetherThreshold) continue;
            SplitDigitRun(text.AsSpan(start, length), start, found);
        }

        found.Sort(static (a, b) => a.Index.CompareTo(b.Index));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var numbers = new List<string>(found.Count);
        foreach (var (_, number) in found)
        {
            if (seen.Add(number)) numbers.Add(number);
        }

        return numbers;
    }

    /// <summary>Every maximal run of consecutive digits in <paramref name="text"/>.</summary>
    private static IEnumerable<(int Start, int Length)> DigitRuns(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsAsciiDigit(text[i])) { i++; continue; }

            var start = i;
            while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
            yield return (start, i - start);
        }
    }

    /// <summary>
    /// Splits a long unbroken run of digits into numbers — but only if the run divides
    /// into valid numbers <b>exactly</b>, with nothing left over at either end.
    ///
    /// The all-or-nothing rule is the important part, and it is worth being explicit
    /// about why, because the obvious implementation is to walk left to right taking
    /// whatever fits and skipping a digit when nothing does. That version is actively
    /// dangerous. Given <c>00721955501133125550147</c> — a roster line whose row number
    /// got glued onto the front — it skips the two zeros, finds that the ten-digit window
    /// starting at the <c>7</c> is structurally valid, and emits it: a number that could
    /// well belong to a stranger, saved and then texted. Nothing downstream can tell it
    /// apart from a number the user meant.
    ///
    /// Requiring an exact tiling removes that class of answer entirely. A run that is
    /// genuinely several numbers with their separators lost tiles perfectly; a run with
    /// junk stuck to it does not tile at all, and is dropped so the user sees a missing
    /// number in the box — which they can fix — rather than a plausible wrong one, which
    /// they cannot spot.
    ///
    /// Feasibility is computed backwards first because greed does not work forwards
    /// either: at any position both an 11-digit and a 10-digit reading can look valid,
    /// and picking the wrong one strands the remainder. <c>feasible[i]</c> answers "can
    /// everything from i onwards be tiled", which makes the forward pass exact.
    /// </summary>
    private static void SplitDigitRun(ReadOnlySpan<char> digits, int offset, List<(int, string)> into)
    {
        var n = digits.Length;

        var feasible = new bool[n + 1];
        feasible[n] = true;

        for (var i = n - 1; i >= 0; i--)
        {
            feasible[i] =
                (i + NormalisedLength <= n && feasible[i + NormalisedLength] && StartsWithCountryCode(digits, i)) ||
                (i + 10 <= n && feasible[i + 10] && IsValidNanp(digits.Slice(i, 10)));
        }

        if (!feasible[0]) return;

        var position = 0;
        while (position < n)
        {
            if (position + NormalisedLength <= n && feasible[position + NormalisedLength] && StartsWithCountryCode(digits, position))
            {
                into.Add((offset + position, digits.Slice(position, NormalisedLength).ToString()));
                position += NormalisedLength;
            }
            else
            {
                into.Add((offset + position, string.Concat("1", digits.Slice(position, 10))));
                position += 10;
            }
        }
    }

    /// <summary>
    /// Whether the eleven digits at <paramref name="index"/> read as a country code
    /// followed by a valid number.
    /// </summary>
    private static bool StartsWithCountryCode(ReadOnlySpan<char> digits, int index) =>
        digits[index] == '1' && IsValidNanp(digits.Slice(index + 1, 10));

    /// <summary>
    /// Whether ten digits form an assignable NANP number: the area code and the exchange
    /// each start 2-9 and neither is an N11 service code. Mirrors the constraints built
    /// into <see cref="Nanp"/>, for the run-splitting path which cannot use it.
    /// </summary>
    private static bool IsValidNanp(ReadOnlySpan<char> ten) =>
        IsValidPrefix(ten[..3]) && IsValidPrefix(ten.Slice(3, 3));

    private static bool IsValidPrefix(ReadOnlySpan<char> three) =>
        three[0] is >= '2' and <= '9' && !(three[1] == '1' && three[2] == '1');

    /// <summary>
    /// Renders a normalised number for display as <c>(219) 555-0113</c>. Anything that
    /// is not a normalised 11-digit number comes back unchanged rather than throwing —
    /// this is only ever called on the display path, where a stored value from an older
    /// format should still render as itself instead of taking the page down.
    /// </summary>
    public static string Format(string normalised)
    {
        if (normalised.Length != NormalisedLength || normalised[0] != '1') return normalised;

        return $"({normalised.Substring(1, 3)}) {normalised.Substring(4, 3)}-{normalised.Substring(7, 4)}";
    }
}
