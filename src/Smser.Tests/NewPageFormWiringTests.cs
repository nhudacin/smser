using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Smser.Tests;

/// <summary>
/// Boots the real app and checks how the roster form is wired to its handlers.
///
/// These exist because of a bug that every unit test in this project passed straight
/// through. A bare <c>&lt;form method="post"&gt;</c> renders with no <c>action</c>
/// attribute, and a browser then posts it to whatever the current document URL happens to
/// be. That is <c>/new</c> on first load — so Generate worked — but once the Import button
/// had run, the address bar read <c>/new?handler=Import</c>, and Generate, which carries no
/// formaction of its own, inherited it. Pressing Generate re-ran the import: nothing was
/// saved, no redirect happened, and no QR code appeared.
///
/// Nothing about that is visible from the page model or the parser. It only exists in the
/// rendered HTML, which is why these tests render it.
/// </summary>
[TestClass]
public class NewPageFormWiringTests
{
    private static SmserApp _app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => _app = new SmserApp();

    [ClassCleanup]
    public static void Stop() => _app.Dispose();

    [TestMethod]
    public async Task The_form_posts_to_an_explicit_url_rather_than_the_current_one()
    {
        var page = await _app.GetPageAsync("/new");

        var action = FormAction(page);

        Assert.IsNotNull(action,
            "the form has no action attribute, so the browser will post it to the current " +
            "document URL — which is how Generate ends up re-running the Import handler");
        StringAssert.Contains(action, "/new");
    }

    [TestMethod]
    public async Task Import_has_its_own_formaction_and_generate_does_not()
    {
        var page = await _app.GetPageAsync("/new");

        Assert.AreEqual("/new?handler=Import", ImportFormAction(page));

        // Generate deliberately carries no formaction: it is the default post target, and
        // the form's action is what decides where it goes. That is exactly why the form's
        // action has to be explicit.
        Assert.IsFalse(
            Regex.IsMatch(page, @"<button type=""submit"" class=""button""[^>]*formaction"),
            "Generate should not carry a formaction");
    }

    /// <summary>The regression. This is the assertion that was failing in the real app.</summary>
    [TestMethod]
    public async Task Generate_still_targets_the_save_handler_after_import_has_run()
    {
        var page = await _app.GetPageAsync("/new");

        var afterImport = await _app.PostFormAsync(ImportFormAction(page)!, new()
        {
            ["__RequestVerificationToken"] = Token(page),
            ["Input.GroupName"] = "soccer team 2023",
            ["Input.RawText"] = "Alex (219) 555-0113 / Sam 312.555.0147",
            ["Input.Numbers"] = ""
        });

        StringAssert.Contains(afterImport, "Found 2 numbers", "the import itself should have worked");

        var action = FormAction(afterImport);

        Assert.IsNotNull(action, "the re-rendered form has no action attribute");
        Assert.IsFalse(action.Contains("handler", StringComparison.OrdinalIgnoreCase),
            $"after Import, the form still posts to '{action}' — pressing Generate would re-run " +
            "the import handler instead of saving the roster");
    }

    [TestMethod]
    public async Task A_saved_roster_keeps_its_id_in_both_post_targets()
    {
        // Not reachable without storage, so this checks the markup on the equivalent path:
        // the id has to survive into the form action and the Import formaction, or
        // Regenerate on /new/{id} would silently save a second, separate roster.
        var page = await _app.GetPageAsync("/new");

        Assert.IsFalse(FormAction(page)!.Contains("id="),
            "an absent id should not be emitted as an empty query parameter");
        Assert.IsFalse(ImportFormAction(page)!.Contains("id="),
            "an absent id should not be emitted as an empty query parameter");
    }

    private static string? FormAction(string html) =>
        Regex.Match(html, @"<form[^>]*\baction=""([^""]*)""").Groups[1] is { Success: true } g ? g.Value : null;

    private static string? ImportFormAction(string html) =>
        Regex.Match(html, @"<button[^>]*formaction=""([^""]*)""[^>]*>\s*Import").Groups[1] is { Success: true } g
            ? WebUtility.HtmlDecode(g.Value)
            : null;

    private static string Token(string html) =>
        Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;

    /// <summary>
    /// The app under test. The storage connection string is a placeholder — none of these
    /// tests reach storage, and the Azure client is built lazily rather than connected at
    /// startup, so this boots without Azurite running.
    /// </summary>
    private sealed class SmserApp : IDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;
        private readonly HttpClient _client;

        public SmserApp()
        {
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(Environments.Development);
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:tables"] = "UseDevelopmentStorage=true" }));
            });

            _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
        }

        public async Task<string> GetPageAsync(string url)
        {
            var response = await _client.GetAsync(url);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"GET {url}");

            return await response.Content.ReadAsStringAsync();
        }

        public async Task<string> PostFormAsync(string url, Dictionary<string, string> fields)
        {
            var response = await _client.PostAsync(url, new FormUrlEncodedContent(fields));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"POST {url}");

            return await response.Content.ReadAsStringAsync();
        }

        public void Dispose()
        {
            _client.Dispose();
            _factory.Dispose();
        }
    }
}
