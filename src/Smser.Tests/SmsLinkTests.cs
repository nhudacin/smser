namespace Smser.Tests;

[TestClass]
public class SmsLinkTests
{
    [TestMethod]
    public void Builds_the_open_addresses_form()
    {
        Assert.AreEqual(
            "sms://open?addresses=12195550113",
            SmsLink.Build(["12195550113"]));
    }

    [TestMethod]
    public void Joins_several_numbers_with_commas_and_no_spaces()
    {
        // A space here is not cosmetic: it ends up in a URL that a messaging app parses,
        // and the ones that do not tolerate it drop every recipient after the first.
        Assert.AreEqual(
            "sms://open?addresses=12195550113,13125550147,14155550199",
            SmsLink.Build(["12195550113", "13125550147", "14155550199"]));
    }

    [TestMethod]
    public void An_empty_roster_gives_an_empty_string_not_a_dangling_link()
    {
        Assert.AreEqual(string.Empty, SmsLink.Build([]));
    }

    [TestMethod]
    public void Keeps_the_roster_order()
    {
        Assert.AreEqual(
            "sms://open?addresses=14155550199,12195550113",
            SmsLink.Build(["14155550199", "12195550113"]));
    }

    [TestMethod]
    public void Round_trips_from_pasted_text_to_link()
    {
        var numbers = PhoneNumberParser.Parse("Alex (219) 555-0113 / Sam 312.555.0147");

        Assert.AreEqual("sms://open?addresses=12195550113,13125550147", SmsLink.Build(numbers));
    }
}
