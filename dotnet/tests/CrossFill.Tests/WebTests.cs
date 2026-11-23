using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace CrossFill.Tests;

/// <summary>Web app tests that need no browser (plain HTTP). Mirrors python/tests/test_web.py.</summary>
[Collection("web")]
public class WebTests(WebAppFixture app)
{
    private HttpClient Http => app.Http;

    private Task<HttpResponseMessage> Transfer(object payload) => Http.PostAsJsonAsync($"{app.BaseUrl}/api/transfer", payload);

    private static Dictionary<string, string> With(params (string Key, string Value)[] changes)
    {
        var d = Samples.All.ToDictionary();
        foreach (var (k, v) in changes) d[k] = v;
        return d;
    }

    private static async Task<string?> Error(HttpResponseMessage r) =>
        (string?)(await r.Content.ReadFromJsonAsync<JsonObject>())!["error"];

    [Fact]
    public async Task RejectsMissingFields()
    {
        var r = await Transfer(new { name = "", email = "" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task TransferAcceptsOnlyJson()
    {
        // A cross-site form post cannot set Content-Type: application/json, so a
        // JSON-looking text/plain body must not start a transfer.
        string body = System.Text.Json.JsonSerializer.Serialize(Samples.All);
        var r = await Http.PostAsync($"{app.BaseUrl}/api/transfer",
            new StringContent(body, System.Text.Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task TransferRequiresEveryField()
    {
        // Form A data must be complete (newsletter is optional) and the topic chosen.
        var r = await Transfer(With(("topic", ""), ("phone", "")));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("Please fill in: Phone, Topic.", await Error(r));

        r = await Transfer(With(("topic", "billing")));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("choose a topic", await Error(r));
    }

    [Fact]
    public async Task LocalFormBServesSameFields()
    {
        // The built-in test Form B keeps the field ids the mapping expects and
        // records a manual submission in memory.
        string html = await Http.GetStringAsync(app.FormBUrl);
        foreach (string id in new[] { "ufullname", "uemailid", "ucontactno", "umsg", "utopic",
                                      "ucontactpref_email", "ucontactpref_phone", "unewsletter" })
            Assert.Contains($"id=\"{id}\"", html);

        app.ClearSubmissions();
        var full = new Dictionary<string, string>
        {
            ["ufullname"] = "Test", ["uemailid"] = "t@example.com", ["ucontactno"] = "5550100",
            ["umsg"] = "Hello", ["utopic"] = "support", ["ucontactpref"] = "phone",
        };
        var r = await Http.PostAsync(app.FormBUrl, new FormUrlEncodedContent(full));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Received (test only)", await r.Content.ReadAsStringAsync());
        var received = Assert.Single(app.Submissions());
        Assert.Equal(full, received.ToDictionary());
        app.ClearSubmissions();
    }

    [Fact]
    public async Task FormBRejectsIncompleteSubmission()
    {
        // Form B checks every field on the server too, and keeps nothing invalid.
        app.ClearSubmissions();
        var r = await Http.PostAsync(app.FormBUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ufullname"] = "Test", ["uemailid"] = "not-an-email", ["utopic"] = "",
        }));
        string html = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("Not submitted", html);
        Assert.Contains("Phone, Message, Topic, Contact method", html);
        Assert.Contains("Email address is not valid", html);
        Assert.Empty(app.Submissions());
    }

    [Theory]
    [InlineData("evil.example", HttpStatusCode.Forbidden)]
    [InlineData("localhost:5000", HttpStatusCode.OK)]
    [InlineData("[::1]:5000", HttpStatusCode.OK)]
    public async Task RejectsForeignHostHeader(string host, HttpStatusCode expected)
    {
        // DNS-rebinding guard: only loopback Host headers are served.
        var request = new HttpRequestMessage(HttpMethod.Get, $"{app.BaseUrl}/health");
        request.Headers.Host = host;
        Assert.Equal(expected, (await Http.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task SecurityHeaders()
    {
        var r = await Http.GetAsync($"{app.BaseUrl}/health");
        Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task HealthReportsVersion()
    {
        var body = await Http.GetFromJsonAsync<JsonObject>($"{app.BaseUrl}/health");
        Assert.Equal(CrossFillInfo.Version, (string?)body!["version"]);
    }

    [Fact]
    public async Task UnknownPathIsJson404()
    {
        var r = await Http.GetAsync($"{app.BaseUrl}/nope");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("Not found", await Error(r));
    }

    [Fact]
    public async Task FormAShowsMappedSelectors()
    {
        string html = await Http.GetStringAsync($"{app.BaseUrl}/");
        Assert.Contains("#ufullname", html);
        Assert.Contains("#umsg", html);
    }

    [Fact]
    public async Task FormAHasEveryFieldTypePrefilled()
    {
        // The editable Form A offers text, textarea, dropdown, radio and checkbox,
        // each pre-filled with the sample record.
        string html = await Http.GetStringAsync($"{app.BaseUrl}/");
        Assert.Contains("value=\"Ada Lovelace\"", html);
        Assert.Contains("<select id=\"topic\" name=\"topic\" required", html);
        Assert.DoesNotContain(" selected", html);          // you must choose a topic yourself
        Assert.Contains("type=\"radio\" name=\"contact_method\"", html);
        Assert.Contains("type=\"checkbox\" id=\"newsletter\"", html);
    }
}
