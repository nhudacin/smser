using System.Text.RegularExpressions;

namespace Smser.Tests;

/// <summary>
/// The group name is a label for whoever made the list, and nothing on the server reads it.
/// It used to be [Required], which meant a roster that was otherwise ready to save — numbers
/// parsed, gate ticked — was refused over a blank field the app does not need.
///
/// The subtle half is that removing [Required] is not enough on its own. MVC infers
/// required-ness from a non-nullable reference type, so a plain `string` property would
/// still be refused, only now with the stock message ("The SMS group name field is
/// required.") instead of the written one. That is why the property is `string?`, and it is
/// what these tests are really guarding.
/// </summary>
[TestClass]
public class OptionalGroupNameTests
{
    private static SmserApp _app = null!;

    [ClassInitialize]
    public static void Start(TestContext _) => _app = new SmserApp();

    [ClassCleanup]
    public static void Stop() => _app.Dispose();

    private static string Token(string html) =>
        Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;

    [TestMethod]
    public async Task A_blank_name_is_not_an_error()
    {
        // Posted with no numbers on purpose: that fails on the numbers, which is what keeps
        // this test away from storage. What matters is which errors come back — the numbers
        // one should, and the name one should not.
        var page = await _app.GetPageAsync("/new");

        var result = await _app.PostFormAsync("/new", new()
        {
            ["__RequestVerificationToken"] = Token(page),
            ["Input.GroupName"] = "",
            ["Input.Numbers"] = ""
        });

        Assert.IsFalse(result.Contains("Give the list a name", StringComparison.Ordinal),
            "a blank name is being refused again");

        // The message MVC substitutes when it infers required-ness from a non-nullable
        // string. Its appearance means the property stopped being nullable.
        //
        // Matched exactly rather than on "field is required": the gate checkbox is a
        // non-nullable bool and has always emitted its own copy of that phrase, so a
        // substring test here passes or fails for reasons that have nothing to do with the
        // name. It cost this test one false failure to find that out.
        Assert.IsFalse(result.Contains("The SMS group name field is required", StringComparison.Ordinal),
            "GroupName is picking up MVC's implicit required-ness — it must stay a string?");
    }

    [TestMethod]
    public async Task The_name_field_no_longer_advertises_itself_as_required()
    {
        // The client-side half. [Required] emits data-val-required, and leaving that behind
        // would block the post in the browser long before the server got a say — the failure
        // would look like the button doing nothing.
        var page = await _app.GetPageAsync("/new");

        var field = Regex.Match(page, @"<input[^>]*\bid=""Input_GroupName""[^>]*>").Value;

        Assert.AreNotEqual(string.Empty, field, "the group name input is gone from the page");
        Assert.IsFalse(field.Contains("data-val-required", StringComparison.Ordinal),
            $"the name input still carries client-side required validation: {field}");
    }

    [TestMethod]
    public async Task The_name_is_still_capped()
    {
        // Optional is not unbounded. The length limit is the one rule left on this field and
        // it is what stops a megabyte of text reaching table storage.
        var page = await _app.GetPageAsync("/new");

        var result = await _app.PostFormAsync("/new", new()
        {
            ["__RequestVerificationToken"] = Token(page),
            ["Input.GroupName"] = new string('x', RosterLimits.MaxGroupNameLength + 1),
            ["Input.Numbers"] = "(219) 555-0113"
        });

        StringAssert.Contains(result, "Keep the name under",
            "an over-long name should still be refused");
    }

    [TestMethod]
    public async Task The_label_says_the_field_is_optional()
    {
        // Optional and unmarked is worse than required: it reads as a field you have to
        // fill, so people stop and think about a name they did not want to give.
        var page = await _app.GetPageAsync("/new");

        StringAssert.Contains(page, "label-optional",
            "nothing on the form tells anyone the name can be skipped");
    }
}
