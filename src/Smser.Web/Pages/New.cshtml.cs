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
    private readonly ILogger<NewModel> _logger;

    public NewModel(SmsGroupStore store, QrCodeGenerator qrCodes, ILogger<NewModel> logger)
    {
        _store = store;
        _qrCodes = qrCodes;
        _logger = logger;
    }

    /// <summary>The saved roster's id, from the route. Null on <c>/new</c>.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Id { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

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
        [Required(ErrorMessage = "Give the list a name so you can tell it apart later.")]
        [StringLength(RosterLimits.MaxGroupNameLength, ErrorMessage = "Keep the name under {1} characters.")]
        [Display(Name = "SMS group name")]
        public string GroupName { get; set; } = string.Empty;

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

        if (Id is not null)
        {
            if (!ShortId.TryNormalise(Id, out var existing)) return NotFound();

            await _store.UpdateAsync(existing, Input.GroupName, rawText, numbers, cancellationToken);
            _logger.LogInformation("Updated roster {RosterId} with {NumberCount} numbers", existing, numbers.Count);

            return RedirectToPage(new { id = existing });
        }

        var saved = await _store.CreateAsync(Input.GroupName, rawText, numbers, cancellationToken);
        _logger.LogInformation("Created roster {RosterId} with {NumberCount} numbers", saved.Id, numbers.Count);

        return RedirectToPage(new { id = saved.Id });
    }

    private void ShowResult(IReadOnlyList<string> numbers)
    {
        Numbers = numbers;
        SmsUrl = SmsLink.Build(numbers);
        QrDataUri = _qrCodes.CreateDataUri(SmsUrl);
        QrTooLarge = QrDataUri is null;
        ShareUrl = Url.Page("/New", pageHandler: null, values: new { id = Id }, protocol: Request.Scheme);
    }
}
