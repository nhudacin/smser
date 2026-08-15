using System.Security.Cryptography;

namespace Smser.Library;

/// <summary>
/// Short, URL-safe, unguessable ids for saved rosters — the <c>abc12345</c> in
/// <c>/new/abc12345</c>.
/// </summary>
public static class ShortId
{
    /// <summary>
    /// Digits and lowercase letters, and deliberately no uppercase.
    ///
    /// The original app used a 62-character mixed-case alphabet. Two things here make
    /// that the wrong choice now. Routing is configured with <c>LowercaseUrls</c>, which
    /// lowercases generated paths *including route parameter values* — so a mixed-case
    /// id would be minted correctly and then mangled by the redirect that hands it to
    /// the user. And Table Storage RowKeys are case-sensitive, so the mangled link would
    /// 404 rather than degrade. A single-case alphabet removes the failure mode instead
    /// of working around it, and makes a link survive being read out over the phone.
    /// </summary>
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// Eight characters of a 36-character alphabet: 41.4 bits, or 2.8e12 ids — within a
    /// rounding error of the 41.7 bits the original got from seven mixed-case characters,
    /// so dropping uppercase costs nothing.
    ///
    /// A roster is only as private as its link, since there is no sign-in, so this has
    /// to be large enough that scanning for other people's rosters is not worthwhile. At
    /// 41.4 bits a scan finds a hit about every 2.8e12/N attempts for N stored rosters,
    /// against a rate-limited endpoint.
    /// </summary>
    public const int Length = 8;

    /// <summary>
    /// Generates a new id from a cryptographic RNG.
    ///
    /// <see cref="RandomNumberGenerator.GetInt32(int)"/> rather than <c>GetBytes</c> plus
    /// <c>% 36</c>: 256 is not a multiple of 36, so the modulo approach is measurably
    /// biased toward the first four characters of the alphabet. GetInt32 rejects and
    /// re-draws instead.
    /// </summary>
    public static string Create()
    {
        return string.Create(Length, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
        });
    }

    /// <summary>
    /// Turns a value off the URL into an id, or reports that it is not one.
    ///
    /// Uppercase is accepted and folded down — someone retyping a link is not going to
    /// preserve case, and there is no other id it could collide with. Everything else is
    /// rejected here rather than at the storage call, because the value becomes a Table
    /// Storage RowKey and RowKeys answer <c>/</c>, <c>\</c>, <c>#</c>, <c>?</c> and
    /// control characters with a 400 — so a hand-edited URL would otherwise surface as an
    /// unhandled exception and a 500 where 404 is the honest answer.
    /// </summary>
    public static bool TryNormalise(string? raw, out string id)
    {
        id = string.Empty;
        if (raw is not { Length: Length }) return false;

        var lowered = raw.ToLowerInvariant();
        foreach (var c in lowered)
        {
            if (!Alphabet.Contains(c, StringComparison.Ordinal)) return false;
        }

        id = lowered;
        return true;
    }

    /// <summary>Whether <paramref name="id"/> is already a normalised id.</summary>
    public static bool IsValid(string? id) =>
        TryNormalise(id, out var normalised) && string.Equals(id, normalised, StringComparison.Ordinal);
}
