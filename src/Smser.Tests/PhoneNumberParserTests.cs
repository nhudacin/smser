namespace Smser.Tests;

/// <summary>
/// The parser is the app, so this is where the coverage is. The cases are grouped by the
/// shape of input they represent rather than by method, because that is how the failures
/// actually arrive: someone pastes a roster and the wrong thing comes out.
/// </summary>
[TestClass]
public class PhoneNumberParserTests
{
    // ── formats a human would type ──────────────────────────────────────────

    [TestMethod]
    [DataRow("2195550113")]
    [DataRow("12195550113")]
    [DataRow("219-555-0113")]
    [DataRow("219.555.0113")]
    [DataRow("219 555 0113")]
    [DataRow("(219) 555-0113")]
    [DataRow("(219)555-0113")]
    [DataRow("1-219-555-0113")]
    [DataRow("1 (219) 555-0113")]
    [DataRow("+1 219 555 0113")]
    [DataRow("+12195550113")]
    [DataRow("+1-219-555-0113")]
    public void Parses_every_common_format_to_the_same_normalised_number(string input)
    {
        CollectionAssert.AreEqual(new[] { "12195550113" }, PhoneNumberParser.Parse(input).ToArray());
    }

    [TestMethod]
    public void Unmatched_parenthesis_still_parses()
    {
        // OCR routinely drops one of the pair.
        CollectionAssert.AreEqual(new[] { "12195550113" }, PhoneNumberParser.Parse("(219 555-0113").ToArray());
        CollectionAssert.AreEqual(new[] { "12195550113" }, PhoneNumberParser.Parse("219) 555-0113").ToArray());
    }

    // ── numbers buried in real pasted text ──────────────────────────────────

    [TestMethod]
    public void Finds_numbers_next_to_names()
    {
        var text = """
            Alex Rivera (219) 555-0113
            Sam Chen    312-555-0147
            Jo Patel    +1 415 555 0199
            """;

        CollectionAssert.AreEqual(
            new[] { "12195550113", "13125550147", "14155550199" },
            PhoneNumberParser.Parse(text).ToArray());
    }

    [TestMethod]
    public void Finds_several_numbers_on_one_line()
    {
        // The original app split on newlines first, so a line like this yielded one
        // mangled number instead of two.
        CollectionAssert.AreEqual(
            new[] { "12195550113", "13125550147" },
            PhoneNumberParser.Parse("home 219-555-0113, cell 312.555.0147").ToArray());
    }

    [TestMethod]
    public void Keeps_the_order_the_numbers_appeared_in()
    {
        var text = "third 415-555-0199 is after second 312-555-0147 which is after first 219-555-0113";

        CollectionAssert.AreEqual(
            new[] { "14155550199", "13125550147", "12195550113" },
            PhoneNumberParser.Parse(text).ToArray());
    }

    [TestMethod]
    public void Removes_duplicates_however_they_were_written()
    {
        var text = "(219) 555-0113 / 2195550113 / +1 219 555 0113 / 1-219-555-0113";

        CollectionAssert.AreEqual(new[] { "12195550113" }, PhoneNumberParser.Parse(text).ToArray());
    }

    [TestMethod]
    public void Ignores_email_addresses_and_prose()
    {
        var text = """
            Team e-mail list, updated 2024-03-14. Questions to coach@example.com.
            Meet at 1600 Pennsylvania Ave, zip 46360, gate 12, bus 7.
            Alex Rivera 219-555-0113
            """;

        CollectionAssert.AreEqual(new[] { "12195550113" }, PhoneNumberParser.Parse(text).ToArray());
    }

    [TestMethod]
    public void A_year_before_a_number_does_not_get_absorbed_into_it()
    {
        // Read left to right, the first ten digits of "2023 219 555 0113" are a
        // structurally valid area code and exchange, and a completely wrong number. This
        // is the case that makes the digit-boundary guards worth having.
        CollectionAssert.AreEqual(
            new[] { "12195550113" },
            PhoneNumberParser.Parse("roster 2023 219 555 0113").ToArray());
    }

    // ── run-together digits, the OCR case ───────────────────────────────────

    [TestMethod]
    public void Splits_a_run_of_concatenated_ten_digit_numbers()
    {
        // Exactly the TODO the original app left in place, which shipped this whole run
        // as a single "number".
        CollectionAssert.AreEqual(
            new[] { "12195550113", "13125550147" },
            PhoneNumberParser.Parse("21955501133125550147").ToArray());
    }

    [TestMethod]
    public void Splits_a_run_of_concatenated_numbers_that_carry_country_codes()
    {
        CollectionAssert.AreEqual(
            new[] { "12195550113", "13125550147" },
            PhoneNumberParser.Parse("1219555011313125550147").ToArray());
    }

    [TestMethod]
    public void Splits_a_run_that_mixes_bare_and_country_coded_numbers()
    {
        // 21 digits: 10 + 11. Greedily taking ten first strands the remainder, so this
        // only works because feasibility is computed before anything is emitted.
        CollectionAssert.AreEqual(
            new[] { "12195550113", "13125550147" },
            PhoneNumberParser.Parse("219555011313125550147").ToArray());
    }

    [TestMethod]
    public void Rejects_a_run_with_junk_stuck_to_it_rather_than_guessing()
    {
        // A row number glued onto the front. Walking this left to right and skipping
        // digits until something fits yields a structurally valid number that could
        // belong to a stranger, indistinguishable downstream from one the user meant.
        // Emitting nothing is the safe answer: the user sees a number missing and can
        // fix it.
        Assert.AreEqual(0, PhoneNumberParser.Parse("00721955501133125550147").Count);
    }

    [TestMethod]
    public void Rejects_a_run_that_does_not_divide_into_whole_numbers()
    {
        // 12 digits is neither one number nor two, whatever it is.
        Assert.AreEqual(0, PhoneNumberParser.Parse("219555011312").Count);
    }

    // ── things that are not phone numbers ───────────────────────────────────

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("no numbers at all here")]
    [DataRow("call me maybe")]
    public void Finds_nothing_in_text_with_no_numbers(string input)
    {
        Assert.AreEqual(0, PhoneNumberParser.Parse(input).Count);
    }

    [TestMethod]
    public void Null_is_empty_not_an_exception()
    {
        Assert.AreEqual(0, PhoneNumberParser.Parse(null).Count);
    }

    [TestMethod]
    [DataRow("555-0113", "seven digits, no area code")]
    [DataRow("123-456-7890", "area code starts with 1")]
    [DataRow("019-555-0113", "area code starts with 0")]
    [DataRow("219-113-0113", "exchange starts with 1")]
    [DataRow("911-555-0113", "N11 area code")]
    [DataRow("219-411-0113", "N11 exchange")]
    public void Rejects_things_that_cannot_be_dialled(string input, string why)
    {
        Assert.AreEqual(0, PhoneNumberParser.Parse(input).Count, why);
    }

    [TestMethod]
    public void Rejects_a_number_with_a_digit_too_many()
    {
        // Truncating this to a valid-looking number is the dangerous failure: it would be
        // saved, texted, and reach a stranger.
        Assert.AreEqual(0, PhoneNumberParser.Parse("219-555-01139").Count);
    }

    [TestMethod]
    public void Rejects_an_eleven_digit_run_that_does_not_start_with_one()
    {
        Assert.AreEqual(0, PhoneNumberParser.Parse("92195550113").Count);
    }

    [TestMethod]
    public void Ignores_an_extension_after_a_number()
    {
        CollectionAssert.AreEqual(
            new[] { "12195550113" },
            PhoneNumberParser.Parse("219-555-0113 x204").ToArray());
    }

    // ── formatting ──────────────────────────────────────────────────────────

    [TestMethod]
    public void Formats_a_normalised_number_for_display()
    {
        Assert.AreEqual("(219) 555-0113", PhoneNumberParser.Format("12195550113"));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("2195550113")]
    [DataRow("not a number")]
    public void Format_passes_through_anything_it_does_not_recognise(string input)
    {
        Assert.AreEqual(input, PhoneNumberParser.Format(input));
    }

    [TestMethod]
    public void Formatted_output_parses_back_to_what_it_came_from()
    {
        // The app round-trips through this: numbers are stored normalised, rendered
        // formatted into the textarea, and re-parsed from the textarea on save.
        var original = PhoneNumberParser.Parse("219-555-0113, 312-555-0147, 415-555-0199");
        var formatted = string.Join(Environment.NewLine, original.Select(PhoneNumberParser.Format));

        CollectionAssert.AreEqual(original.ToArray(), PhoneNumberParser.Parse(formatted).ToArray());
    }

    // ── size ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Handles_a_large_roster()
    {
        // Five area codes across the whole of 555-0100..555-0199, which is the only block
        // NANPA reserves for fictional use — 500 distinct numbers, none of them anyone's.
        string[] areas = ["219", "312", "415", "213", "650"];
        var text = string.Join('\n',
            from area in areas
            from line in Enumerable.Range(100, 100)
            select $"Player: ({area}) 555-0{line}");

        Assert.AreEqual(500, PhoneNumberParser.Parse(text).Count);
    }
}
