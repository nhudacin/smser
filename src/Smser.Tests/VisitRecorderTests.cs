using System.Net;
using Microsoft.AspNetCore.Http;
using Smser.Web.Services;

namespace Smser.Tests;

/// <summary>
/// What gets logged and what does not.
///
/// The filtering is the part worth testing. App Service polls <c>/alive</c> continuously
/// and browsers pull a dozen static files per page, so a log that records everything is
/// mostly noise, costs real money in transactions, and buries the visits somebody
/// actually wanted to see.
/// </summary>
[TestClass]
public class VisitRecorderTests
{
    private static HttpContext Request(string path, string method = "GET", string ip = "203.0.113.7")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);

        return context;
    }

    [TestMethod]
    [DataRow("/alive", "the platform health probe, hit constantly")]
    [DataRow("/version", "polled by the deploy smoke test")]
    [DataRow("/health", "development diagnostics")]
    public void Infrastructure_endpoints_are_not_logged(string path, string why)
    {
        Assert.IsNull(VisitRecorder.Describe(Request(path)), why);
    }

    [TestMethod]
    [DataRow("/css/site.css")]
    [DataRow("/js/photo-ocr.js")]
    [DataRow("/favicon.ico")]
    [DataRow("/lib/tesseract/tesseract-core-simd-lstm.wasm")]
    public void Static_assets_are_not_logged(string path)
    {
        Assert.IsNull(VisitRecorder.Describe(Request(path)));
    }

    [TestMethod]
    public void Posts_are_not_logged()
    {
        // A save posts and then redirects to a GET. Logging both would count every roster
        // twice; the interesting outcome is recorded explicitly as roster-created.
        Assert.IsNull(VisitRecorder.Describe(Request("/new", "POST")));
    }

    [TestMethod]
    [DataRow("/")]
    [DataRow("/new")]
    [DataRow("/not-found")]
    public void Ordinary_pages_are_logged_as_a_page_view(string path)
    {
        var entry = VisitRecorder.Describe(Request(path));

        Assert.IsNotNull(entry);
        Assert.AreEqual(VisitEvents.Page, entry.Event);
        Assert.AreEqual(path, entry.Path);
        Assert.IsNull(entry.RosterId);
    }

    [TestMethod]
    public void Opening_a_saved_roster_is_logged_against_that_roster()
    {
        var entry = VisitRecorder.Describe(Request("/new/ab12cd34"));

        Assert.IsNotNull(entry);
        Assert.AreEqual(VisitEvents.RosterViewed, entry.Event);
        Assert.AreEqual("ab12cd34", entry.RosterId);
    }

    [TestMethod]
    public void A_retyped_uppercase_link_is_logged_against_the_same_roster()
    {
        // The page canonicalises the id, so the log has to agree or the same roster
        // appears under two names.
        Assert.AreEqual("ab12cd34", VisitRecorder.Describe(Request("/new/AB12CD34"))!.RosterId);
    }

    [TestMethod]
    [DataRow("/new/nope")]
    [DataRow("/new/ab12cd34567")]
    [DataRow("/new/")]
    public void A_path_that_is_not_a_roster_id_is_just_a_page(string path)
    {
        var entry = VisitRecorder.Describe(Request(path));

        Assert.IsNotNull(entry);
        Assert.AreEqual(VisitEvents.Page, entry.Event);
        Assert.IsNull(entry.RosterId);
    }

    [TestMethod]
    public void The_caller_address_and_browser_are_captured()
    {
        var context = Request("/new", ip: "198.51.100.42");
        context.Request.Headers.UserAgent = "Mozilla/5.0 (iPhone)";
        context.Request.Headers.Referer = "https://smser.temprbac.com/";

        var entry = VisitRecorder.Describe(context)!;

        Assert.AreEqual("198.51.100.42", entry.Ip);
        Assert.AreEqual("Mozilla/5.0 (iPhone)", entry.UserAgent);
        Assert.AreEqual("https://smser.temprbac.com/", entry.Referer);
        Assert.AreNotEqual(default, entry.OccurredAt);
    }

    [TestMethod]
    public void A_missing_browser_header_is_null_rather_than_an_empty_string()
    {
        // Empty strings and nulls read differently in a storage browser, and "" would
        // suggest a browser that sent a blank header rather than one that sent none.
        var entry = VisitRecorder.Describe(Request("/"))!;

        Assert.IsNull(entry.UserAgent);
        Assert.IsNull(entry.Referer);
        Assert.IsNull(entry.Country);
    }

    [TestMethod]
    public void A_country_header_is_used_when_a_front_end_supplies_one()
    {
        var context = Request("/");
        context.Request.Headers["CF-IPCountry"] = "US";

        Assert.AreEqual("US", VisitRecorder.Describe(context)!.Country);
    }
}
