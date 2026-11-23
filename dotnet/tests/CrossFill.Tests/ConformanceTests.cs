using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CrossFill.Web;

namespace CrossFill.Tests;

/// <summary>
/// Shared conformance cases from spec/conformance/cases.json. The Python implementation
/// runs the same cases, so both behave identically.
/// </summary>
public class ConformanceTests
{
    private static readonly JsonObject Cases =
        JsonNode.Parse(File.ReadAllText(Repo.Spec("conformance", "cases.json")))!.AsObject();

    public static TheoryData<string> Names(string section)
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases[section]!.AsArray())
            data.Add((string?)c!["name"] ?? (string?)c["file"] ?? (string)c["field"]!);
        return data;
    }

    private static JsonObject Case(string section, string name) =>
        Cases[section]!.AsArray().Select(c => c!.AsObject())
            .Where(c => ((string?)c["name"] ?? (string?)c["file"] ?? (string?)c["field"]) == name)
            .First();

    private static IEnumerable<JsonObject> All(string section) => Cases[section]!.AsArray().Select(c => c!.AsObject());

    private static OrderedDictionary<string, IReadOnlyList<string>>? Synonyms(JsonObject c) =>
        c["synonyms"] is JsonObject s
            ? new(s.Select(p => KeyValuePair.Create(p.Key, (IReadOnlyList<string>)p.Value!.AsArray().Select(w => (string)w!).ToList())))
            : null;

    private static OrderedDictionary<string, string> Strings(JsonNode? node) =>
        new(node!.AsObject().Select(p => KeyValuePair.Create(p.Key, (string)p.Value!)));

    [Theory]
    [MemberData(nameof(Names), "mapping_validation")]
    public void MappingValidation(string name)
    {
        var c = Case("mapping_validation", name);
        var mapping = c["mapping"]!.DeepClone();
        if (c["error"] is { } error)
        {
            var e = Assert.Throws<MappingException>(() => MappingLoader.Validate(mapping));
            Assert.Equal((string)error!, e.Message);
            return;
        }
        var result = MappingLoader.Validate(mapping);
        Assert.Equal((int)c["result"]!["version"]!, result.Version);
        Assert.Equal((string)c["result"]!["form_selector"]!, result.FormSelector);
    }

    [Fact]
    public void SynonymsFor()
    {
        foreach (var c in All("synonyms_for"))
            Assert.Equal(c["expect"]!.AsArray().Select(w => (string)w!), Matcher.SynonymsFor((string)c["field"]!, Synonyms(c)));
    }

    [Fact]
    public void Score()
    {
        foreach (var c in All("score"))
            Assert.True((int)c["expect"]! == Matcher.Score((string)c["field"]!, (string)c["text"]!, (string)c["type"]!, Synonyms(c)),
                $"score({c["field"]}, {c["text"]}, {c["type"]})");
    }

    [Fact]
    public void BestCandidate()
    {
        foreach (var c in All("best_candidate"))
        {
            var candidates = c["candidates"]!.AsArray()
                .Select(x => new Candidate((int)x!["index"]!, (string)x["type"]!, (string)x["text"]!)).ToList();
            var taken = c["taken"]!.AsArray().Select(x => (int)x!).ToHashSet();
            Assert.Equal((int?)c["expect"], Matcher.BestCandidate((string)c["field"]!, candidates, taken, Synonyms(c)));
        }
    }

    [Theory]
    [MemberData(nameof(Names), "records")]
    public void Records(string file)
    {
        var c = Case("records", file);
        string path = Repo.Spec("conformance", "data", file);
        if (c["records"] is JsonArray expected)
        {
            var records = Sources.LoadRecords(path);
            Assert.Equal(expected.Count, records.Count);
            for (int i = 0; i < expected.Count; i++)
                Assert.Equal(Strings(expected[i]).ToList(), records[i].ToList());   // same keys, values and order
            return;
        }
        var e = Assert.Throws<SourceException>(() => Sources.LoadRecords(path));
        string message = e.Message.Replace(path, "{path}");
        if (c["error"] is { } error) Assert.Equal((string)error!, message);
        else Assert.StartsWith((string)c["error_prefix"]!, message);
    }

    [Fact]
    public void ContactValidation_()
    {
        foreach (var c in All("contact_validation"))
        {
            var labels = (string)c["form"]! == "a" ? ContactValidation.FormARequired : ContactValidation.FormBRequired;
            Assert.Equal(c["errors"]!.AsArray().Select(x => (string)x!), ContactValidation.Validate(Strings(c["values"]), labels));
        }
    }

    [Fact]
    public void VersionMatchesPythonPackage()
    {
        string init = File.ReadAllText(Path.Combine(Repo.Root, "python", "crossfill", "__init__.py"));
        string python = Regex.Match(init, "__version__ = \"(.+?)\"").Groups[1].Value;
        Assert.Equal(python, CrossFillInfo.Version);
    }
}
