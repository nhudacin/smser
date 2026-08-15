using Smser.Web.Services;

namespace Smser.Tests;

[TestClass]
public class QrCodeGeneratorTests
{
    private readonly QrCodeGenerator _generator = new();

    [TestMethod]
    public void Renders_a_roster_as_an_inline_png()
    {
        var link = SmsLink.Build(["12195550113", "13125550147"]);

        var uri = _generator.CreateDataUri(link);

        Assert.IsNotNull(uri);
        StringAssert.StartsWith(uri, "data:image/png;base64,");

        // PNG magic number, so this is asserting an actual image rather than any
        // non-empty string that happens to be base64.
        var bytes = Convert.FromBase64String(uri["data:image/png;base64,".Length..]);
        CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
    }

    [TestMethod]
    public void An_empty_link_produces_nothing()
    {
        Assert.IsNull(_generator.CreateDataUri(string.Empty));
    }

    [TestMethod]
    public void A_roster_too_large_for_a_qr_symbol_returns_null_rather_than_throwing()
    {
        // Past the 2,953-byte ceiling of a version-40 symbol at error correction L. The
        // page falls back to showing the link; what it must not do is throw, because the
        // roster itself saved perfectly well.
        var link = SmsLink.Build(Padding(400));

        Assert.IsTrue(link.Length > 2953, "test input is not actually over the limit");
        Assert.IsNull(_generator.CreateDataUri(link));
    }

    [TestMethod]
    public void A_roster_just_inside_the_limit_still_renders()
    {
        // The boundary the page's "too long for a QR code" message is really about.
        var link = SmsLink.Build(Padding(200));

        Assert.IsNotNull(_generator.CreateDataUri(link));
    }

    /// <summary>
    /// <paramref name="count"/> distinct normalised numbers, used purely to push the
    /// payload past a length. Only the byte count matters to a QR symbol, so these vary
    /// the area code and keep the reserved-for-fiction 555-0100 line rather than
    /// generating something that could belong to somebody.
    /// </summary>
    private static IEnumerable<string> Padding(int count) =>
        Enumerable.Range(0, count).Select(i => $"1{200 + i}5550100");
}
