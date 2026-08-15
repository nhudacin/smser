using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Smser.Web.Pages;

/// <summary>
/// Reached through <c>UseStatusCodePagesWithReExecute</c>, which re-runs the pipeline
/// against this path while preserving the original status code — so a bad roster link
/// renders this page and still answers 404 to anything reading the status.
/// </summary>
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class NotFoundModel : PageModel
{
    public void OnGet() { }
}
