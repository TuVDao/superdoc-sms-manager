using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace SuperDoc.Sms.Tests;

/// <summary>
/// Validates the shipped translations as data.
/// </summary>
/// <remarks>
/// The packs are loaded at runtime, so a mistake in one of them is not a compile error - it is a
/// blank label, or a <c>FormatException</c> the moment that string is formatted, in a language
/// nobody on the project reads. Checking them here is the only realistic defence.
/// </remarks>
public class LanguagePackTests
{
    private const string Reference = "en";

    /// <summary>Matches the numbered placeholders that <c>string.Format</c> will substitute.</summary>
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.Compiled);

    public static TheoryData<string> AllPacks
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var file in Directory.GetFiles(RepoLayout.StringsDirectory, "*.json"))
            {
                data.Add(Path.GetFileNameWithoutExtension(file));
            }

            return data;
        }
    }

    [Fact]
    public void TheReferencePackExists()
    {
        Assert.True(
            File.Exists(Path.Combine(RepoLayout.StringsDirectory, $"{Reference}.json")),
            "English is the fallback for every other language and must be present.");
    }

    [Theory]
    [MemberData(nameof(AllPacks))]
    public void EveryPackIsValidJson(string code)
    {
        var exception = Record.Exception(() => Load(code));

        Assert.True(exception is null, $"{code}.json is not readable: {exception?.Message}");
    }

    [Theory]
    [MemberData(nameof(AllPacks))]
    public void EveryPackDeclaresItsIdentity(string code)
    {
        using var document = Load(code);
        var meta = document.RootElement.GetProperty("_meta");

        Assert.Equal(code, meta.GetProperty("code").GetString());

        var name = meta.GetProperty("name").GetString();
        Assert.False(string.IsNullOrWhiteSpace(name), $"{code}.json has no display name.");

        // Both are read unconditionally by the loader, so a missing one is a runtime failure.
        Assert.True(meta.TryGetProperty("rtl", out _), $"{code}.json is missing 'rtl'.");
        Assert.True(meta.TryGetProperty("reviewed", out _), $"{code}.json is missing 'reviewed'.");
    }

    [Theory]
    [MemberData(nameof(AllPacks))]
    public void EveryPackCoversEveryKeyEnglishDefines(string code)
    {
        var english = ReadValues(Reference);
        var pack = ReadValues(code);

        var missing = english.Keys.Where(key => !pack.ContainsKey(key)).OrderBy(k => k).ToList();

        Assert.True(
            missing.Count == 0,
            $"{code}.json is missing {missing.Count} key(s): {string.Join(", ", missing)}");
    }

    [Theory]
    [MemberData(nameof(AllPacks))]
    public void NoPackDefinesKeysEnglishDoesNot(string code)
    {
        var english = ReadValues(Reference);
        var pack = ReadValues(code);

        // A key with no English counterpart can never be reached through the fallback, so it is
        // either a typo or a leftover.
        var extra = pack.Keys.Where(key => !english.ContainsKey(key)).OrderBy(k => k).ToList();

        Assert.True(
            extra.Count == 0,
            $"{code}.json defines {extra.Count} key(s) English does not: {string.Join(", ", extra)}");
    }

    [Theory]
    [MemberData(nameof(AllPacks))]
    public void PlaceholdersMatchTheEnglishOriginal(string code)
    {
        var english = ReadValues(Reference);
        var pack = ReadValues(code);

        List<string> problems = [];

        foreach (var (key, source) in english)
        {
            if (!pack.TryGetValue(key, out var translated))
            {
                continue;
            }

            var expected = Indices(source);
            var actual = Indices(translated);

            if (!expected.SetEquals(actual))
            {
                problems.Add(
                    $"{key}: expected {{{string.Join(",", expected.Order())}}} " +
                    $"but found {{{string.Join(",", actual.Order())}}}");
            }
        }

        // A translation that drops {0} loses the number; one that invents {2} throws
        // FormatException as soon as the string is used.
        Assert.True(
            problems.Count == 0,
            $"{code}.json has placeholder mismatches:{Environment.NewLine}  " +
            string.Join(Environment.NewLine + "  ", problems));
    }

    [Theory]
    [MemberData(nameof(AllPacks))]
    public void NoTranslationIsBlank(string code)
    {
        var blank = ReadValues(code)
            .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => pair.Key)
            .OrderBy(k => k)
            .ToList();

        Assert.True(blank.Count == 0, $"{code}.json has empty value(s): {string.Join(", ", blank)}");
    }

    [Fact]
    public void RightToLeftIsDeclaredWhereTheScriptRequiresIt()
    {
        // Arabic drives FlowDirection; getting this wrong lays the whole window out backwards.
        using var document = Load("ar");

        Assert.True(document.RootElement.GetProperty("_meta").GetProperty("rtl").GetBoolean());
    }

    [Fact]
    public void OnlyReviewedTranslationsClaimToBeReviewed()
    {
        var reviewed = new List<string>();

        foreach (var file in Directory.GetFiles(RepoLayout.StringsDirectory, "*.json"))
        {
            var code = Path.GetFileNameWithoutExtension(file);
            using var document = Load(code);
            if (document.RootElement.GetProperty("_meta").GetProperty("reviewed").GetBoolean())
            {
                reviewed.Add(code);
            }
        }

        // Claiming review the project has not done would mislead users about translation quality.
        Assert.Equal(["en", "vi"], reviewed.Order());
    }

    private static JsonDocument Load(string code) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoLayout.StringsDirectory, $"{code}.json")));

    private static Dictionary<string, string> ReadValues(string code)
    {
        using var document = Load(code);
        Dictionary<string, string> values = [];

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.NameEquals("_meta"))
            {
                continue;
            }

            values[property.Name] = property.Value.GetString() ?? string.Empty;
        }

        return values;
    }

    private static HashSet<int> Indices(string value)
    {
        HashSet<int> indices = [];
        foreach (Match match in Placeholder.Matches(value))
        {
            indices.Add(int.Parse(match.Groups[1].Value));
        }

        return indices;
    }
}
