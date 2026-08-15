using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Smser.Web.Pages;

/// <summary>
/// Reached through <c>UseStatusCodePagesWithReExecute</c>, which re-runs the pipeline
/// against this path while preserving the original status code — so a bad roster link
/// renders this page and still answers 404 to anything reading the status.
///
/// It answers for the rate limiter too. The two cases need different words because they
/// need different actions from the reader: a 404 means check the link, a 429 means wait.
/// Telling a throttled visitor their list does not exist sends them off to look for a
/// problem that is not there.
/// </summary>
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class NotFoundModel : PageModel
{
    /// <summary>
    /// The status code that was being rendered, from the <c>?code=</c> the re-execute
    /// appends. Defaults to 404 for a direct visit to <c>/not-found</c>, which is the only
    /// way this page is reached without one.
    /// </summary>
    [BindProperty(SupportsGet = true, Name = "code")]
    public int Code { get; set; } = StatusCodes.Status404NotFound;

    public bool WasThrottled => Code == StatusCodes.Status429TooManyRequests;

    public void OnGet() { }
}
