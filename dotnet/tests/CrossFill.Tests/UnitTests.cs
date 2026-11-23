using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace CrossFill.Tests;

/// <summary>Fast unit tests: no browser, no server. Mirrors python/tests/test_unit.py.</summary>
public class UnitTests
{
    private static JsonSchema Schema() => JsonSchema.FromText(File.ReadAllText(MappingLoader.SchemaPath));

    private static void AssertMatchesSchema(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = Schema().Evaluate(doc.RootElement);
        Assert.True(result.IsValid, $"{path} does not match the mapping schema");
    }

    // --- mapping --------------------------------------------------------------

    [Fact]
    public void BundledMappingIsValid()
    {
        using var _ = EnvScope.Clean();
        var m = MappingLoader.Load();
        Assert.Equal("http://127.0.0.1:5000/form-b", m.TargetUrl);
        Assert.Equal("#ufullname", m.Fields["name"]);
        Assert.Equal(1, m.Version);
    }

    [Fact]
    public void BundledFilesAreTheSharedSpec()
    {
        Assert.Equal(File.ReadAllBytes(Repo.Spec("mappings", "form_b.json")), File.ReadAllBytes(MappingLoader.DefaultMappingPath));
        Assert.Equal(File.ReadAllBytes(Repo.Spec("schema", "mapping.schema.json")), File.ReadAllBytes(MappingLoader.SchemaPath));
    }

    [Fact]
    public void BundledMappingMatchesJsonSchema() => AssertMatchesSchema(MappingLoader.DefaultMappingPath);

    public static TheoryData<string> ExampleMappings() =>
        new(Directory.GetFiles(Repo.Examples("mappings"), "*.json").Order().Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(ExampleMappings))]
    public void ExampleMappingsAreValid(string name)
    {
        using var _ = EnvScope.Clean();
        string path = Repo.Examples("mappings", name);
        MappingLoader.Load(path);
        AssertMatchesSchema(path);
    }

    [Fact]
    public void ExampleDataFilesLoad()
    {
        Assert.Equal("true", Sources.LoadRecord(Repo.Examples("data", "record.json"))["newsletter"]);
        Assert.Equal(2, Sources.LoadRecords(Repo.Examples("data", "contacts.csv")).Count);
    }

    [Fact]
    public void TargetUrlOverride()
    {
        using (EnvScope.Clean())
            Assert.Equal("https://a.example/x", MappingLoader.Load(targetUrl: "https://a.example/x").TargetUrl);
        using (new EnvScope(CrossFillInfo.TargetUrlEnv, "https://b.example/y"))
            Assert.Equal("https://b.example/y", MappingLoader.Load().TargetUrl);
    }

    [Fact]
    public void DefaultsAreFilled()
    {
        var m = MappingLoader.Validate(JsonNode.Parse("""{"target_url": "https://x.example", "fields": {}}"""));
        Assert.Equal("form", m.FormSelector);
        Assert.Equal("#email", MappingLoader.SourceFields(m)["email"]);
    }

    [Theory]
    [InlineData("""{"fields": {}}""", "target_url")]
    [InlineData("""{"target_url": "https://x.example"}""", "fields")]
    [InlineData("""{"target_url": "javascript:alert(1)", "fields": {}}""", "http")]
    [InlineData("""{"target_url": "https://x.example", "fields": {"a": ""}}""", "fields.a")]
    [InlineData("""{"target_url": "https://x.example", "fields": {}, "feilds": {}}""", "typos")]
    [InlineData("""{"target_url": "https://x.example", "fields": {}, "version": 9}""", "version")]
    [InlineData("""{"target_url": "https://x.example", "fields": {}, "synonyms": {"a": []}}""", "synonyms")]
    [InlineData("""{"target_url": "https://x.example", "fields": {}, "source": {"url": "ftp://x"}}""", "source.url")]
    [InlineData("[]", "object")]
    public void InvalidMappings(string json, string message)
    {
        var e = Assert.Throws<MappingException>(() => MappingLoader.Validate(JsonNode.Parse(json)));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void UnreadableMappingFiles()
    {
        using var tmp = new TempDir();
        Assert.Contains("not found", Assert.Throws<MappingException>(() => MappingLoader.Load(Path.Combine(tmp.Path, "missing.json"))).Message);
        string broken = tmp.Write("broken.json", "{ nope");
        Assert.Contains("line 1", Assert.Throws<MappingException>(() => MappingLoader.Load(broken)).Message);
    }

    // --- matcher --------------------------------------------------------------

    [Fact]
    public void ScorePrefersSpecificSynonyms()
    {
        Assert.True(Matcher.Score("name", "ufullname full name", "text") > Matcher.Score("name", "uname", "text"));
        Assert.Equal(0, Matcher.Score("email", "unrelated", "text"));
    }

    [Fact]
    public void TypeHintOnlyBreaksTies()
    {
        Assert.True(Matcher.Score("email", "uemailid", "email") > Matcher.Score("email", "uemailid", "text"));
        Assert.Equal(0, Matcher.Score("email", "nothing here", "email"));   // type alone is not a match
    }

    [Fact]
    public void ExtraSynonymsWin()
    {
        var extra = new OrderedDictionary<string, IReadOnlyList<string>> { ["topic"] = ["Subject"] };
        Assert.Equal("subject", Matcher.SynonymsFor("topic", extra)[0]);
        Assert.Equal(0, Matcher.Score("topic", "your subject", "text"));
        Assert.True(Matcher.Score("topic", "your subject", "text", extra) > 0);
    }

    [Fact]
    public void BestCandidateSkipsTaken()
    {
        Candidate[] candidates =
        [
            new(0, "text", "ufullname"),
            new(1, "email", "uemailid"),
            new(2, "text", "full name backup"),
        ];
        Assert.Equal(0, Matcher.BestCandidate("name", candidates, new HashSet<int>()));
        Assert.Equal(2, Matcher.BestCandidate("name", candidates, new HashSet<int> { 0 }));
        Assert.Equal(1, Matcher.BestCandidate("email", candidates, new HashSet<int>()));
        Assert.Null(Matcher.BestCandidate("message", candidates, new HashSet<int>()));
    }

    // --- sources --------------------------------------------------------------

    [Fact]
    public void JsonRecords()
    {
        using var tmp = new TempDir();
        string f = tmp.Write("r.json", """{"name": " Ada ", "age": 36, "ok": true, "none": null}""");
        Assert.Equal(
            new Dictionary<string, string> { ["name"] = "Ada", ["age"] = "36", ["ok"] = "true", ["none"] = "" },
            Sources.LoadRecord(f));
    }

    [Fact]
    public void CsvRecords()
    {
        using var tmp = new TempDir();
        string f = tmp.Write("r.csv", "﻿name,email\nAda,a@x.io\nGrace,g@x.io\n");
        Assert.Equal(["Ada", "Grace"], Sources.LoadRecords(f).Select(r => r["name"]));
        Assert.Equal("g@x.io", Sources.LoadRecord(f, 1)["email"]);
        Assert.Contains("out of range", Assert.Throws<SourceException>(() => Sources.LoadRecord(f, 5)).Message);
    }

    [Theory]
    [InlineData("r.txt", "x", "unsupported")]
    [InlineData("r.json", "[]", "no records")]
    [InlineData("r.json", """{"a": {"b": 1}}""", "plain value")]
    [InlineData("r.json", "{", "invalid JSON")]
    public void BadDataFiles(string name, string content, string message)
    {
        using var tmp = new TempDir();
        string f = tmp.Write(name, content);
        Assert.Contains(message, Assert.Throws<SourceException>(() => Sources.LoadRecords(f)).Message);
    }

    [Theory]
    [InlineData(1.5, "1.5")]
    [InlineData(100.0, "100.0")]
    [InlineData(0.001, "0.001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(-2.25, "-2.25")]
    public void FloatsPrintLikePython(double value, string expected) => Assert.Equal(expected, PyText.Float(value));

    // --- cli ------------------------------------------------------------------

    private static async Task<(int Code, string Out, string Err)> RunCli(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = await CrossFill.Cli.Cli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task CliValidate()
    {
        using var _ = EnvScope.Clean();
        using var tmp = new TempDir();
        string bad = tmp.Write("bad.json", """{"fields": {}}""");
        Assert.Equal(0, (await RunCli("validate", MappingLoader.DefaultMappingPath)).Code);
        var (code, output, _) = await RunCli("validate", MappingLoader.DefaultMappingPath, bad);
        Assert.Equal(2, code);
        Assert.Contains("OK", output);
        Assert.Contains("FAIL", output);
    }

    [Fact]
    public async Task CliRunUsageErrors()
    {
        using var tmp = new TempDir();
        Assert.Equal(2, (await RunCli("run", "--data", Path.Combine(tmp.Path, "missing.json"))).Code);
        var (code, _, err) = await RunCli("run");
        Assert.Equal(2, code);
        Assert.Contains("give --source-url or --data", err);
        Assert.Equal(2, (await RunCli("run", "--data", "a.json", "--source-url", "http://x")).Code);
    }

    [Fact]
    public async Task CliLegacyFormMapsToRun()
    {
        // Old-style invocation without a subcommand still parses as `run`.
        using var tmp = new TempDir();
        var (code, _, err) = await RunCli("--data", Path.Combine(tmp.Path, "missing.json"));
        Assert.Equal(2, code);
        Assert.Contains("Data file not found", err);
    }

    [Fact]
    public async Task CliVersion()
    {
        var (code, output, _) = await RunCli("--version");
        Assert.Equal(0, code);
        Assert.Contains(CrossFillInfo.Version, output);
    }

    [Fact]
    public async Task CliWithoutCommandShowsHelp()
    {
        var (code, output, _) = await RunCli();
        Assert.Equal(2, code);
        Assert.Contains("validate", output);
    }
}
