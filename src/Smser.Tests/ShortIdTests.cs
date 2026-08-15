namespace Smser.Tests;

[TestClass]
public class ShortIdTests
{
    [TestMethod]
    public void Generated_ids_are_the_declared_length_and_alphabet()
    {
        for (var i = 0; i < 200; i++)
        {
            var id = ShortId.Create();

            Assert.AreEqual(ShortId.Length, id.Length);
            Assert.IsTrue(id.All(char.IsAsciiLetterOrDigit), id);
            Assert.AreEqual(id.ToLowerInvariant(), id, "ids must survive LowercaseUrls unchanged");
            Assert.IsTrue(ShortId.IsValid(id), id);
        }
    }

    [TestMethod]
    public void Generated_ids_do_not_repeat()
    {
        // Not a distribution test — just a tripwire for the generator degenerating to a
        // constant or a per-process seed, which is the way this usually breaks.
        var ids = Enumerable.Range(0, 1000).Select(_ => ShortId.Create()).ToHashSet();

        Assert.AreEqual(1000, ids.Count);
    }

    [TestMethod]
    public void Every_character_of_the_alphabet_gets_used()
    {
        // Catches an off-by-one in the alphabet indexing, which would silently shrink the
        // keyspace rather than fail.
        var used = string.Concat(Enumerable.Range(0, 3000).Select(_ => ShortId.Create())).ToHashSet();

        Assert.AreEqual(36, used.Count);
    }

    [TestMethod]
    public void Uppercase_is_folded_down_rather_than_rejected()
    {
        Assert.IsTrue(ShortId.TryNormalise("AB12CD34", out var id));
        Assert.AreEqual("ab12cd34", id);
    }

    [TestMethod]
    public void An_uppercase_id_is_valid_input_but_not_a_canonical_id()
    {
        // The page relies on this pair to redirect a retyped link to its canonical URL.
        Assert.IsTrue(ShortId.TryNormalise("AB12CD34", out _));
        Assert.IsFalse(ShortId.IsValid("AB12CD34"));
    }

    [TestMethod]
    [DataRow(null, "null")]
    [DataRow("", "empty")]
    [DataRow("abc123", "too short")]
    [DataRow("abc123456", "too long")]
    [DataRow("ab/cd123", "RowKey-illegal slash")]
    [DataRow("ab?cd123", "RowKey-illegal question mark")]
    [DataRow("ab#cd123", "RowKey-illegal hash")]
    [DataRow("ab cd123", "space")]
    [DataRow("ab-cd123", "outside the alphabet")]
    public void Rejects_anything_that_is_not_an_id(string? input, string why)
    {
        Assert.IsFalse(ShortId.TryNormalise(input, out _), why);
        Assert.IsFalse(ShortId.IsValid(input), why);
    }
}
