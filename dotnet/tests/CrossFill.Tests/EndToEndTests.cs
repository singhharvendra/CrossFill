using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace CrossFill.Tests;

/// <summary>
/// End-to-end tests: a real headless Chromium against the local test Form B.
/// Mirrors python/tests/test_e2e.py. Skip them with <c>--filter Category!=browser</c>.
/// </summary>
[Collection("web")]
[Trait("Category", "browser")]
public sealed class EndToEndTests : IDisposable
{
    private readonly WebAppFixture _app;
    private readonly string _sourceUrl;

    public EndToEndTests(WebAppFixture app)
    {
        _app = app;
        app.UseTestEnvironment();
        app.ClearSubmissions();
        // A read-only Form A page pre-filled with the full sample.
        app.State.Records["test"] = Samples.All;
        _sourceUrl = $"{app.BaseUrl}/?record=test";
    }

    public void Dispose() => _app.State.Records.TryRemove("test", out _);

    private Mapping BundledMapping() => MappingLoader.Load(targetUrl: _app.FormBUrl);

    private Mapping InlineMapping(JsonObject fields) => MappingLoader.Validate(new JsonObject
    {
        ["target_url"] = _app.FormBUrl,
        ["form_selector"] = "form:visible",
        ["fields"] = fields,
    });

    [Fact]
    public async Task FillWithMapping()
    {
        var result = await Engine.RunTransferAsync(_sourceUrl, BundledMapping(), headless: true);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(Samples.All.ToList(), result.Data.ToList());
        Assert.Equal(Samples.All.ToDictionary(), result.Verified.ToDictionary());
        Assert.Equal("#ufullname", result.Filled["name"]);
        Assert.Equal("input[name='ucontactpref']", result.Filled["contact_method"]);
        Assert.Empty(_app.Submissions());   // the target is never submitted
    }

    [Fact]
    public async Task AutoMatchWithoutSelectors()
    {
        // With no selectors at all, CrossFill must still find the right fields
        // and ignore the hidden registration form.
        var result = await Engine.RunTransferAsync(_sourceUrl, InlineMapping([]), headless: true);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(Samples.Text.ToDictionary(), result.Verified.ToDictionary());
        Assert.All(result.Filled.Values, v => Assert.StartsWith("auto-match", v));
    }

    [Fact]
    public async Task MixedSelectorsNeverDoubleFill()
    {
        // A field with an explicit selector must not be picked again by the
        // auto-matcher for another key.
        var result = await Engine.RunTransferAsync(
            _sourceUrl, InlineMapping(new JsonObject { ["phone"] = "#ucontactno" }), headless: true);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("#ucontactno", result.Filled["phone"]);
        Assert.Equal(result.Filled.Count, result.Filled.Values.Distinct().Count());
    }

    [Fact]
    public async Task DataFileWithSelectAndCheckbox()
    {
        // Records from a JSON file can fill selects (by label) and checkboxes.
        var record = new JsonObject();
        foreach (var (k, v) in Samples.All) record[k] = v;
        record["topic"] = "Support";
        record["newsletter"] = true;
        using var tmp = new TempDir();
        string dataFile = tmp.Write("record.json", new JsonArray(record).ToJsonString());

        var result = await Engine.RunTransferAsync(mapping: BundledMapping(), data: Sources.LoadRecord(dataFile), headless: true);

        Assert.True(result.Ok, $"{result.Error} {string.Join(",", result.Mismatched)}");
        Assert.Equal("support", result.Verified["topic"]);
        Assert.Equal("true", result.Verified["newsletter"]);
        Assert.Empty(_app.Submissions());
    }

    [Fact]
    public async Task UnknownSelectOptionIsReported()
    {
        var data = new OrderedDictionary<string, string> { ["name"] = "A", ["topic"] = "Billing" };
        var result = await Engine.RunTransferAsync(mapping: BundledMapping(), data: data, headless: true);

        Assert.False(result.Ok);
        Assert.Equal(["topic"], result.Mismatched);
        Assert.Equal("A", result.Verified["name"]);
    }

    [Fact]
    public async Task DryRunTypesNothing()
    {
        var result = await Engine.RunTransferAsync(_sourceUrl, BundledMapping(), headless: true, dryRun: true);

        Assert.True(result.Ok && result.DryRun);
        Assert.Equal(Samples.All.Keys.Order(), result.Filled.Keys.Order());
        Assert.Empty(result.Verified);
    }

    [Fact]
    public async Task CliRunWithDataFile()
    {
        using var tmp = new TempDir();
        string dataFile = tmp.Write("people.csv", "name,email\nAda,ada@example.com\nGrace,grace@example.com\n");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        int code = await CrossFill.Cli.Cli.RunAsync(
            ["run", "--data", dataFile, "--record", "1", "--target-url", _app.FormBUrl, "--headless"], stdout, stderr);

        Assert.True(code == 0, stderr.ToString());
        string line = stdout.ToString().Split('\n').First(l => l.StartsWith(CrossFillInfo.ResultPrefix));
        var body = JsonNode.Parse(line[CrossFillInfo.ResultPrefix.Length..])!;
        Assert.Equal("""{"name":"Grace","email":"grace@example.com"}""", body["verified"]!.ToJsonString());
    }

    [Fact]
    public async Task WebAppFillsWithoutSubmitting()
    {
        // Full flow through /api/transfer: Form B is filled but never submitted,
        // even if a caller asks for it.
        var payload = Samples.All.ToDictionary();
        payload["mode"] = "submit";
        var r = await _app.Http.PostAsJsonAsync($"{_app.BaseUrl}/api/transfer", payload);
        var body = (await r.Content.ReadFromJsonAsync<JsonObject>())!;

        Assert.True(r.StatusCode == HttpStatusCode.OK, body.ToJsonString());
        Assert.True((bool)body["ok"]!);
        Assert.Equal(Samples.All.ToDictionary(),
            body["verified"]!.AsObject().ToDictionary(p => p.Key, p => (string)p.Value!));
        Assert.False(body.ContainsKey("submitted"));
        await Task.Delay(1000);   // give any stray POST time to arrive
        Assert.Empty(_app.Submissions());
    }
}
