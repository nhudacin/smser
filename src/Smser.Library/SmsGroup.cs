namespace Smser.Library;

/// <summary>
/// A saved roster: the numbers, the name someone gave the list, and the text they
/// originally pasted in.
/// </summary>
public sealed record SmsGroup
{
    /// <summary>The <see cref="ShortId"/> that appears in the roster's URL.</summary>
    public required string Id { get; init; }

    /// <summary>Human label — "soccer team 2023". Never used as a key.</summary>
    public required string GroupName { get; init; }

    /// <summary>
    /// The text the numbers were parsed out of, kept so that reopening a roster
    /// re-populates the import box and the parse can be corrected and re-run rather than
    /// re-pasted from scratch.
    /// </summary>
    public string RawText { get; init; } = string.Empty;

    /// <summary>Normalised 11-digit numbers, in roster order.</summary>
    public required IReadOnlyList<string> Numbers { get; init; }

    /// <summary>
    /// Null for a roster whose stored entity predates the field. Nothing on the display
    /// path depends on these; they exist so a retention sweep has something to sort on.
    /// </summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <inheritdoc cref="CreatedAt"/>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>The <c>sms:</c> link for this roster. Derived, never stored.</summary>
    public string SmsUrl => SmsLink.Build(Numbers);
}

/// <summary>
/// Size limits on a roster, set by what a Table Storage entity can hold.
///
/// A table property caps at 32,768 UTF-16 characters and an entity at 1 MiB. These sit
/// under both with room to spare, and are enforced twice: by the form, so an oversized
/// paste gets a validation message, and by <see cref="SmsGroupStore"/>, so nothing can
/// reach storage large enough to come back as an opaque 400 from the service.
/// </summary>
public static class RosterLimits
{
    /// <summary>Longest accepted paste, in characters.</summary>
    public const int MaxRawTextLength = 32_000;

    /// <summary>Longest accepted roster name, in characters.</summary>
    public const int MaxGroupNameLength = 200;

    /// <summary>
    /// Most numbers in one roster. At 12 stored characters each this is 24,000 — inside
    /// the per-property cap — and it is far past any number of people who can be in a
    /// group text that anybody wants to be in.
    /// </summary>
    public const int MaxNumbers = 2_000;
}
