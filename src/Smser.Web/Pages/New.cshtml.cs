using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Smser.Library;
using Smser.Web.Services;

namespace Smser.Web.Pages;

/// <summary>
/// The whole app: paste a roster, get a link and a QR code, save it under a short URL.
///
/// One page serves both <c>/new</c> and <c>/new/{id}</c>. They are the same screen — the
/// second is the first with the form already filled in — and splitting them would mean
/// two copies of the form and two copies of the save handler that have to be kept in
/// step.
/// </summary>
public class NewModel : PageModel
{
    private readonly SmsGroupStore _store;
    private readonly QrCodeGenerator _qrCodes;
    private readonly VisitRecorder _visits;
    private readonly FormGuard _guard;
    private readonly ILogger<NewModel> _logger;

    public NewModel(SmsGroupStore store, QrCodeGenerator qrCodes, VisitRecorder visits,
        FormGuard guard, ILogger<NewModel> logger)
    {
        _store = store;
        _qrCodes = qrCodes;
        _visits = visits;
        _guard = guard;
        _logger = logger;
    }

    /// <summary>The saved roster's id, from the route. Null on <c>/new</c>.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>
    /// The honeypot. Bound so the value posted can be inspected; never rendered back, so a
    /// browser or password manager that fills it in once does not keep the person locked
    /// out of the form on every retry.
    /// </summary>
    [BindProperty(Name = FormGuard.HoneypotField)]
    public string? Website { get; set; }

    /// <summary>When the form that produced this post was rendered. See <see cref="FormGuard"/>.</summary>
    [BindProperty(Name = FormGuard.TimestampField)]
    public string? Timestamp { get; set; }

    /// <summary>
    /// The timestamp for the form about to be rendered — the posted one where there is a
    /// valid one, so an Import round trip does not restart the clock. Lazy rather than set
    /// in each handler, because every path that returns <c>Page()</c> needs one and a
    /// handler added later would otherwise render a form with no token and no warning.
    /// </summary>
    public string IssuedTimestamp => _issued ??= _guard.CarryOrIssue(Timestamp);

    private string? _issued;

    /// <summary>Parsed numbers of the saved roster, for the results panel.</summary>
    public IReadOnlyList<string> Numbers { get; private set; } = [];

    public string? SmsUrl { get; private set; }

    public string? QrDataUri { get; private set; }

    /// <summary>Absolute URL of this roster, for the copy-link button.</summary>
    public string? ShareUrl { get; private set; }

    /// <summary>
    /// True when a roster saved fine but is past what a QR symbol can hold. The link
    /// still works; only the code is missing. See <see cref="QrCodeGenerator"/>.
    /// </summary>
    public bool QrTooLarge { get; private set; }

    /// <summary>Result of the Import button — "Found 14 numbers", or why it found none.</summary>
    public string? ImportMessage { get; private set; }

    public bool HasResult => SmsUrl is not null;

    public class InputModel
    {
        /// <summary>
        /// Optional, and nullable for the same reason <see cref="Numbers"/> is: a
        /// non-nullable string picks up the implicit required-ness MVC infers from the
        /// reference type, so simply dropping the [Required] here would swap one refusal
        /// for another — and a worse-worded one ("The SMS group name field is required.").
        ///
        /// The name is a label for whoever made the list and nothing reads it but them.
        /// Refusing to save a perfectly good roster over a blank field it does not need
        /// was the wrong trade.
        /// </summary>
        [StringLength(RosterLimits.MaxGroupNameLength, ErrorMessage = "Keep the name under {1} characters.")]
        [Display(Name = "SMS group name")]
        public string? GroupName { get; set; }

        [StringLength(RosterLimits.MaxRawTextLength, ErrorMessage = "That is more than {1} characters — paste the roster in a couple of batches.")]
        [Display(Name = "Roster import")]
        public string? RawText { get; set; }

        /// <summary>
        /// Nullable on purpose. A non-nullable string here picks up the implicit
        /// required-ness that MVC infers from the reference type, whose stock message
        /// ("The Phone numbers field is required.") would be reported instead of the one
        /// below that tells the user what to actually do about it.
        /// </summary>
        [StringLength(RosterLimits.MaxRawTextLength, ErrorMessage = "That is more than {1} characters.")]
        [Display(Name = "Phone numbers")]
        public string? Numbers { get; set; }

        /// <summary>
        /// The generate gate: has the person confirmed their own number is not in the list?
        ///
        /// Checked server side as well as by the browser, because the <c>required</c>
        /// attribute on the checkbox is only a courtesy — anything posting the form
        /// directly simply omits the field. Not an attribute on the property: the stock
        /// messages for "this bool must be true" all read like a schema violation rather
        /// than the one thing the person still has to do.
        /// </summary>
        [Display(Name = "My own number is not in this list")]
        public bool OwnNumberExcluded { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (Id is null) return Page();

        // Canonicalise before looking anything up, so a link retyped in caps resolves to
        // the same page rather than 404ing on a case-sensitive RowKey.
        if (!ShortId.TryNormalise(Id, out var id)) return NotFound();
        if (!string.Equals(Id, id, StringComparison.Ordinal)) return RedirectToPage(new { id });

        var roster = await _store.GetAsync(id, cancellationToken);
        if (roster is null) return NotFound();

        Input.GroupName = roster.GroupName;
        Input.RawText = roster.RawText;
        Input.Numbers = string.Join(Environment.NewLine, roster.Numbers.Select(PhoneNumberParser.Format));

        ShowResult(roster.Numbers);

        return Page();
    }

    /// <summary>
    /// The Import button: parse the pasted text into the numbers box without saving
    /// anything.
    ///
    /// Deliberately a real form post rather than script. The parser is the part of this
    /// app worth getting right, and having exactly one implementation of it — server
    /// side, unit tested — is worth a round trip. It also means Import works on a phone
    /// with a flaky connection to a CDN, which is the device this is used from.
    /// </summary>
    public IActionResult OnPostImport()
    {
        // Honeypot only. The elapsed-time rule is not applied here because the photo
        // importer submits this handler automatically the instant the OCR finishes, which
        // is exactly the "too fast to be a person" shape the rule looks for.
        if (Refuse(_guard.Inspect(Website, timestamp: null))) return Page();

        var parsed = PhoneNumberParser.Parse(Input.RawText);

        if (parsed.Count == 0)
        {
            ImportMessage = string.IsNullOrWhiteSpace(Input.RawText)
                ? "Paste the roster text above first, then press Import."
                : "No phone numbers found in that text. Numbers need an area code — check the paste came through.";
            return Page();
        }

        Input.Numbers = string.Join(Environment.NewLine, parsed.Select(PhoneNumberParser.Format));

        // Tag helpers render the posted value from ModelState in preference to the model,
        // so without this the box would redisplay whatever was in it before Import ran.
        ModelState.Remove($"{nameof(Input)}.{nameof(InputModel.Numbers)}");

        ImportMessage = parsed.Count == 1
            ? "Found 1 number. Check it, then Generate."
            : $"Found {parsed.Count} numbers. Check them, then Generate.";

        return Page();
    }

    /// <summary>
    /// Generate: save the roster and redirect to its own URL.
    ///
    /// The numbers box is re-parsed rather than trusted, so hand-edits go through exactly
    /// the same validation and normalisation as an import, and what gets stored is always
    /// canonical 11-digit values.
    /// </summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        // Before any parsing or storage work. This is the handler that mints an id and
        // bills a transaction, so it gets both checks.
        if (Refuse(_guard.Inspect(Website, Timestamp))) return Page();

        if (!Input.OwnNumberExcluded)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(InputModel.OwnNumberExcluded)}",
                "Tick the box to confirm your own number is not in the list.");
        }

        var numbers = PhoneNumberParser.Parse(Input.Numbers);

        if (numbers.Count == 0)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(InputModel.Numbers)}",
                "No usable phone numbers here. Each needs an area code — paste the roster above and press Import.");
        }
        else if (numbers.Count > RosterLimits.MaxNumbers)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(InputModel.Numbers)}",
                $"That is {numbers.Count} numbers; a list holds at most {RosterLimits.MaxNumbers}.");
        }

        if (!ModelState.IsValid) return Page();

        var rawText = Input.RawText ?? string.Empty;

        // Same coalesce as rawText above, and for the same reason: the field is optional, so
        // an untouched box arrives as null and the store takes a string.
        var groupName = Input.GroupName ?? string.Empty;

        if (Id is not null)
        {
            if (!ShortId.TryNormalise(Id, out var existing)) return NotFound();

            await _store.UpdateAsync(existing, groupName, rawText, numbers, cancellationToken);
            _logger.LogInformation("Updated roster {RosterId} with {NumberCount} numbers", existing, numbers.Count);
            RecordRosterEvent(VisitEvents.RosterUpdated, existing, numbers.Count);

            return RedirectToPage(new { id = existing });
        }

        var saved = await _store.CreateAsync(groupName, rawText, numbers, cancellationToken);
        _logger.LogInformation("Created roster {RosterId} with {NumberCount} numbers", saved.Id, numbers.Count);
        RecordRosterEvent(VisitEvents.RosterCreated, saved.Id, numbers.Count);

        return RedirectToPage(new { id = saved.Id });
    }

    /// <summary>
    /// Turns a verdict into a decision. True means the post must not proceed — the reason
    /// has been logged and a message is already on the page.
    /// </summary>
    private bool Refuse(FormVerdict verdict)
    {
        if (verdict is FormVerdict.Ok) return false;

        _logger.LogWarning("Refused a {Verdict} submission from {Ip}",
            verdict, VisitRecorder.ClientIp(HttpContext));

        RecordEvent(verdict is FormVerdict.Honeypot ? VisitEvents.BotHoneypot : VisitEvents.BotTooFast);

        // One message for both verdicts, and vague on purpose. Saying which rule was hit
        // tells whoever is automating this precisely what to change, and the two rules are
        // only worth anything for as long as they are not described on the page that
        // enforces them. A person who trips one is not stuck: the honeypot is never
        // rendered back with a value in it, so trying again works.
        ModelState.AddModelError(string.Empty,
            "That did not look like a form a person filled in, so nothing was saved. Try again.");

        return true;
    }

    /// <summary>
    /// Notes a save in the audit log. The middleware only sees GETs, so without this a
    /// roster's creation is invisible — the first trace of it would be somebody opening
    /// the link afterwards.
    /// </summary>
    private void RecordRosterEvent(string name, string rosterId, int numberCount) =>
        RecordEvent(name, rosterId, numberCount);

    private void RecordEvent(string name, string? rosterId = null, int? numberCount = null) =>
        _visits.Record(new VisitEntry
        {
            OccurredAt = DateTimeOffset.UtcNow,
            Event = name,
            Path = Request.Path.Value ?? "/new",
            RosterId = rosterId,
            Ip = VisitRecorder.ClientIp(HttpContext),
            UserAgent = Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null,
            Referer = Request.Headers.Referer.ToString() is { Length: > 0 } r ? r : null,
            NumberCount = numberCount
        });

    private void ShowResult(IReadOnlyList<string> numbers)
    {
        Numbers = numbers;
        SmsUrl = SmsLink.Build(numbers);
        QrDataUri = _qrCodes.CreateDataUri(SmsUrl);
        QrTooLarge = QrDataUri is null;
        ShareUrl = Url.Page("/New", pageHandler: null, values: new { id = Id }, protocol: Request.Scheme);
    }
}
