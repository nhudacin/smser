using System.Text.RegularExpressions;

namespace Smser.Tests;

/// <summary>
/// The generate gate: nothing saves until the person has confirmed their own number is not
/// in the roster.
///
/// This is the one warning on the page that costs something to ignore. Leave yourself in
/// the list and iPhones split the group into two threads, so half the roster sees half the
/// replies and nobody involved can work out why. It spent its life as a bold line in a
/// hint, which is exactly the shape of thing people skip.
///
/// Two halves worth testing, and the browser only enforces one of them. The `required`
/// attribute stops a person pressing Generate too early; it does nothing at all to a post
/// that simply omits the field, which is why the server check is here too.
/// </summary>
[TestClass]
public class GenerateGateTests
{
    private static SmserApp _app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => _app = new SmserApp();

    [ClassCleanup]
    public static void Stop() => _app.Dispose();

    [TestMethod]
    public async Task The_checkbox_ships_required_and_unticked()
    {
        var page = await _app.GetPageAsync("/new");

        var checkbox = Regex.Match(page, @"<input[^>]*name=""Input\.OwnNumberExcluded""[^>]*>").Value;

        Assert.AreNotEqual(string.Empty, checkbox, "the gate's checkbox is not on the page");
        StringAssert.Contains(checkbox, "required",
            "without required, the browser lets Generate through and the only thing standing " +
            "between a bad roster and the store is the server check");
        Assert.IsFalse(checkbox.Contains("checked"),
            "a gate that arrives already answered is not a gate");
    }

    /// <summary>
    /// The gate must not take the Import button down with it.
    ///
    /// A `required` field blocks every submit button in its form, not just the one the gate
    /// is about. When the gate first shipped, that meant Import did nothing at all until
    /// the box was ticked — and silently, because a blocked submit is not an error, it is
    /// simply a click that goes nowhere. The photo importer was hit hardest: it reads the
    /// roster, drops the text in the box, calls importButton.click(), and the click was
    /// swallowed. The text arrived and the numbers never did.
    ///
    /// Import is also the wrong place to ask. It parses text into the numbers box and saves
    /// nothing; the gate is a claim about the finished list, which nobody can honestly make
    /// before seeing what the import found.
    /// </summary>
    [TestMethod]
    public async Task Import_is_not_blocked_by_the_gate()
    {
        var page = await _app.GetPageAsync("/new");

        var import = Regex.Match(page, @"<button[^>]*handler=Import[^>]*>").Value;

        Assert.AreNotEqual(string.Empty, import, "the Import button is not on the page");
        StringAssert.Contains(import, "formnovalidate",
            "the gate's required checkbox blocks every submit in the form, so without " +
            "formnovalidate the Import button — and the photo importer that clicks it — " +
            "quietly stops working until the box is ticked");
    }

    /// <summary>
    /// The other half of the same guarantee, on the server: Import saves nothing, so it has
    /// no reason to consult the gate and must not start refusing posts that omit it.
    /// </summary>
    [TestMethod]
    public async Task Import_still_parses_with_the_box_unticked()
    {
        var page = await _app.GetPageAsync("/new");

        var result = await _app.PostFormAsync("/new?handler=Import", new()
        {
            ["__RequestVerificationToken"] = Token(page),
            ["Input.GroupName"] = "soccer team 2023",
            ["Input.RawText"] = "Chris Bohling-2197423219, Nick Hudacin-2196133108"
        });

        StringAssert.Contains(result, "Found 2 numbers",
            "Import should parse without waiting on a confirmation about the final list");
    }

    /// <summary>
    /// The regression that matters: a post that leaves the field out entirely. That is what
    /// a script does, and it is also what a browser sends for an unticked checkbox.
    /// </summary>
    [TestMethod]
    public async Task A_post_without_the_confirmation_saves_nothing()
    {
        var page = await _app.GetPageAsync("/new");

        var result = await _app.PostFormAsync("/new", new()
        {
            ["__RequestVerificationToken"] = Token(page),
            ["Input.GroupName"] = "soccer team 2023",
            ["Input.Numbers"] = "(219) 555-0113\n(312) 555-0147"
        });

        StringAssert.Contains(result, "Tick the box",
            "the page came back without saying what is missing");

        // A save redirects to /new/{id}; getting the form back means nothing was stored.
        StringAssert.Contains(result, "name=\"Input.OwnNumberExcluded\"");
    }

    [TestMethod]
    public async Task The_numbers_are_still_checked_when_the_box_is_ticked()
    {
        var page = await _app.GetPageAsync("/new");

        var result = await _app.PostFormAsync("/new", new()
        {
            ["__RequestVerificationToken"] = Token(page),
            ["Input.GroupName"] = "soccer team 2023",
            ["Input.Numbers"] = "nothing here is a phone number",
            ["Input.OwnNumberExcluded"] = "true"
        });

        StringAssert.Contains(result, "No usable phone numbers",
            "ticking the gate should not buy a pass on the rest of the validation");
    }

    private static string Token(string html) =>
        Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;

}
