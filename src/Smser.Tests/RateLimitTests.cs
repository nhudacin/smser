using System.Net;
using System.Text.RegularExpressions;

namespace Smser.Tests;

/// <summary>
/// The three budgets on /new, and the reason there are three.
///
/// Rate limiting is endpoint metadata and a Razor Page is one endpoint covering its GET
/// and its POST, so the limit used to have to be loose enough for the loosest thing the
/// page does. Splitting the budgets inside the policy is what lets the save be held to a
/// few a minute; these tests exist because that split is invisible from the outside
/// until it is wrong, and the way it goes wrong is that reading a roster starts spending
/// the allowance for writing one.
///
/// Each test class gets its own app, and so its own limiter state. Tests within a class
/// share a budget, which is why they count their requests carefully.
/// </summary>
[TestClass]
public class RateLimitTests
{
    private static string Token(string html) =>
        Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;

    /// <summary>A save that fails validation: it exercises the limiter without needing storage.</summary>
    private static Dictionary<string, string> Save(string token) => new()
    {
        ["__RequestVerificationToken"] = token,
        ["Input.GroupName"] = "soccer team 2023",
        ["Input.Numbers"] = ""
    };

    [TestMethod]
    public async Task The_fifth_save_in_a_minute_is_turned_away()
    {
        using var app = new SmserApp();
        var token = Token(await app.GetPageAsync("/new"));

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            using var response = await app.PostFormRawAsync("/new", Save(token));
            codes.Add(response.StatusCode);
        }

        CollectionAssert.AreEqual(
            new[]
            {
                HttpStatusCode.OK,
                HttpStatusCode.OK,
                HttpStatusCode.OK,
                HttpStatusCode.OK,
                HttpStatusCode.TooManyRequests,
                HttpStatusCode.TooManyRequests
            },
            codes,
            $"saves should stop at four a minute; got {string.Join(", ", codes)}");
    }

    [TestMethod]
    public async Task Being_out_of_saves_does_not_stop_you_reading_a_roster()
    {
        // The whole point of the split. Before it, spending the budget on writes would
        // have taken the page away from anyone following a share link from the same
        // address — a household behind one NAT, or an office.
        using var app = new SmserApp();
        var token = Token(await app.GetPageAsync("/new"));

        for (var i = 0; i < 5; i++)
        {
            (await app.PostFormRawAsync("/new", Save(token))).Dispose();
        }

        using var read = await app.GetRawAsync("/new");

        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode,
            "reads are sharing a budget with saves again");
    }

    [TestMethod]
    public async Task Being_out_of_saves_does_not_stop_you_importing()
    {
        // Import parses text and touches no storage, so it has its own, looser budget. It
        // also fires once automatically per photographed page, which is why it must not be
        // spending the save allowance.
        using var app = new SmserApp();
        var token = Token(await app.GetPageAsync("/new"));

        for (var i = 0; i < 5; i++)
        {
            (await app.PostFormRawAsync("/new", Save(token))).Dispose();
        }

        using var import = await app.PostFormRawAsync("/new?handler=Import", new()
        {
            ["__RequestVerificationToken"] = token,
            ["Input.GroupName"] = "soccer team 2023",
            ["Input.RawText"] = "Alex (219) 555-0113"
        });

        Assert.AreEqual(HttpStatusCode.OK, import.StatusCode,
            "imports are sharing a budget with saves");
        StringAssert.Contains(await import.Content.ReadAsStringAsync(), "Found 1 number");
    }

    [TestMethod]
    public async Task A_turned_away_caller_is_told_when_to_come_back()
    {
        using var app = new SmserApp();
        var token = Token(await app.GetPageAsync("/new"));

        HttpResponseMessage? rejected = null;
        for (var i = 0; i < 6 && rejected is null; i++)
        {
            var response = await app.PostFormRawAsync("/new", Save(token));
            if (response.StatusCode == HttpStatusCode.TooManyRequests) rejected = response;
            else response.Dispose();
        }

        Assert.IsNotNull(rejected, "never hit the limit");

        using (rejected)
        {
            Assert.AreEqual("60", rejected.Headers.RetryAfter?.ToString(),
                "a 429 with no Retry-After tells a well-behaved client nothing about when to retry");

            // And the page has to say the right thing. UseStatusCodePagesWithReExecute
            // catches every bodyless 4xx, so without the status code being passed through
            // this renders "No list here" — which sends a throttled person off to check a
            // link that was never the problem.
            var html = await rejected.Content.ReadAsStringAsync();

            StringAssert.Contains(html, "One moment.");
            Assert.IsFalse(html.Contains("No list here", StringComparison.Ordinal),
                "a throttled caller is being told their list does not exist");
        }
    }

    [TestMethod]
    public async Task A_bad_roster_link_still_says_it_is_missing()
    {
        // The other side of that page: teaching it about 429 must not have cost it the
        // 404 it was written for.
        using var app = new SmserApp();

        // Deliberately the wrong length, so it fails the id check and 404s without a
        // storage lookup — no test in this project has storage to reach.
        using var response = await app.GetRawAsync("/new/zzz");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        StringAssert.Contains(html, "No list here");
        Assert.IsFalse(html.Contains("One moment.", StringComparison.Ordinal));
    }
}
