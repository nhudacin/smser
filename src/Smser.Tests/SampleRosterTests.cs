namespace Smser.Tests;

/// <summary>
/// Runs every roster in <c>samples/</c> through the parser and checks it against the
/// <c>.expected</c> file sitting next to it.
///
/// These exist so the samples cannot quietly stop being true. They are the files a person
/// opens and pastes into the running app to see what the importer does, and a sample that
/// no longer matches the parser is worse than no sample at all — it teaches the wrong
/// thing to whoever reads it next. Adding a case is two files and no code.
/// </summary>
[TestClass]
public class SampleRosterTests
{
    private static string SamplesDirectory => Path.Combine(AppContext.BaseDirectory, "Samples");

    public static IEnumerable<object[]> Samples =>
        Directory.EnumerateFiles(SamplesDirectory, "*.txt")
                 .OrderBy(path => path, StringComparer.Ordinal)
                 .Select(path => new object[] { Path.GetFileName(path) });

    [TestMethod]
    public void The_samples_are_actually_there()
    {
        // Without this, a broken Content glob in the csproj turns the whole class into
        // zero test cases and the suite still reports green.
        Assert.IsTrue(Directory.Exists(SamplesDirectory), $"no sample directory at {SamplesDirectory}");
        Assert.IsTrue(Samples.Any(), "sample rosters were not copied to the test output");
    }

    [TestMethod]
    [DynamicData(nameof(Samples))]
    public void Sample_parses_to_its_expected_numbers(string fileName)
    {
        var rosterPath = Path.Combine(SamplesDirectory, fileName);
        var expectedPath = Path.ChangeExtension(rosterPath, ".expected");

        Assert.IsTrue(File.Exists(expectedPath),
            $"{fileName} has no .expected file beside it — every sample needs one, even if it is empty");

        var expected = File.ReadAllLines(expectedPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        var actual = PhoneNumberParser.Parse(File.ReadAllText(rosterPath)).ToArray();

        CollectionAssert.AreEqual(expected, actual,
            $"{fileName}\n  expected: {string.Join(", ", expected)}\n  actual:   {string.Join(", ", actual)}");
    }

    [TestMethod]
    [DynamicData(nameof(Samples))]
    public void Sample_contains_only_numbers_reserved_for_fiction(string fileName)
    {
        // This repo is public and the subject of the app is other people's phone numbers.
        // 555-0100..555-0199 is the only block NANPA reserves for fictional use, so
        // anything the parser accepts out of a sample has to land inside it. This is the
        // check that keeps a real number from arriving in a future sample by accident.
        var numbers = PhoneNumberParser.Parse(File.ReadAllText(Path.Combine(SamplesDirectory, fileName)));

        foreach (var number in numbers)
        {
            var exchangeAndLine = number[4..];

            Assert.IsTrue(
                exchangeAndLine.StartsWith("55501", StringComparison.Ordinal),
                $"{fileName} yields {PhoneNumberParser.Format(number)}, which is outside the reserved 555-0100..555-0199 range");
        }
    }
}
