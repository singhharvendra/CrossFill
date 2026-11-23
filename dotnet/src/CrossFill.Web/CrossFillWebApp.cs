using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using CrossFill.Web.Pages;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CrossFill.Web;

/// <summary>In-memory state of the demo app. Nothing is written to disk or sent anywhere.</summary>
public sealed class WebState(string mappingPath)
{
    /// <summary>The mapping file for the target form.</summary>
    public string MappingPath { get; } = mappingPath;

    /// <summary>Record id → Form A data, while a transfer reads it.</summary>
    public ConcurrentDictionary<string, OrderedDictionary<string, string>> Records { get; } = new();

    /// <summary>What the local test Form B received. Lock on the list before using it.</summary>
    public List<OrderedDictionary<string, string>> FormBSubmissions { get; } = [];

    /// <summary>The current mapping (re-read on every request, so edits apply at once).</summary>
    public Mapping CurrentMapping() => MappingLoader.Load(MappingPath);
}

/// <summary>
/// CrossFill demo web app.
/// </summary>
/// <remarks>
/// Form A is the page you fill in here. When you click "Transfer", CrossFill asks you to
/// confirm, then a Playwright browser opens Form A, reads the data, opens the target form
/// (<c>target_url</c> in the mapping) and fills the matching fields. The target is never
/// submitted automatically: you review it and press Submit.
///
/// The app also serves its own test target at /form-b, with the same fields as the sample
/// mapping. Submissions to it are kept in memory only.
///
/// Security: the server binds to 127.0.0.1 and rejects requests whose Host header is not a
/// loopback name, which blocks DNS-rebinding attacks from web pages you visit. It is a
/// single-user local tool; do not expose it on a network.
/// </remarks>
public static class CrossFillWebApp
{
    private static readonly HashSet<string> LoopbackHosts = ["127.0.0.1", "localhost", "[::1]"];

    /// <summary>Fictional demo values pre-filled into the editable Form A so it can be tested in one click.</summary>
    public static IReadOnlyDictionary<string, string> SampleRecord { get; } = new OrderedDictionary<string, string>
    {
        ["name"] = "Ada Lovelace",
        ["email"] = "ada@example.com",
        ["phone"] = "123-456-7890",
        ["message"] = "I would like to know more about your services. Please call me back this week.",
        ["topic"] = "",            // left unchosen on purpose: you must pick a topic
        ["contact_method"] = "email",
        ["newsletter"] = "true",
    };

    /// <summary>Build the app on 127.0.0.1:<paramref name="port"/> (0 picks a free port).</summary>
    public static WebApplication Build(int port = 5000, string? mappingPath = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = [],
        });
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddRazorComponents();
        builder.Services.AddSingleton(new WebState(
            Path.GetFullPath(string.IsNullOrEmpty(mappingPath) ? MappingLoader.DefaultMappingPath : mappingPath)));

        var app = builder.Build();
        app.Use(OnlyLoopbackHosts);
        app.Use(SecurityHeaders);

        app.MapGet("/", FormA);
        app.MapMethods("/form-b", ["GET", "POST"], FormB);
        app.MapPost("/api/transfer", TransferAsync);
        app.MapGet("/health", () => Results.Json(new { ok = true, version = CrossFillInfo.Version }));
        app.MapFallback(() => Results.Json(new { ok = false, error = "Not found" }, statusCode: 404));
        return app;
    }

    /// <summary>Start the demo app on 127.0.0.1 (never on a public interface) and run until Ctrl+C.</summary>
    /// <exception cref="MappingException">The mapping is invalid (checked before the server starts).</exception>
    public static async Task ServeAsync(int port = 5000, string? mappingPath = null, TextWriter? output = null)
    {
        var app = Build(port, mappingPath);
        app.Services.GetRequiredService<WebState>().CurrentMapping();   // fail fast on a bad mapping
        (output ?? Console.Out).WriteLine($"CrossFill running at http://127.0.0.1:{port}  (Ctrl+C to stop)");
        await app.RunAsync();
    }

    /// <summary>'127.0.0.1:5000' → '127.0.0.1', '[::1]:5000' → '[::1]'.</summary>
    internal static string HostName(string host)
    {
        if (host.StartsWith('['))
            return host[..(host.IndexOf(']') + 1)];
        int colon = host.LastIndexOf(':');
        return colon >= 0 ? host[..colon] : host;
    }

    private static async Task OnlyLoopbackHosts(HttpContext ctx, Func<Task> next)
    {
        if (!LoopbackHosts.Contains(HostName(ctx.Request.Host.Value ?? "").ToLowerInvariant()))
        {
            ApplySecurityHeaders(ctx.Response);
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { ok = false, error = "Forbidden: CrossFill only answers on localhost" });
            return;
        }
        await next();
    }

    private static Task SecurityHeaders(HttpContext ctx, Func<Task> next)
    {
        ctx.Response.OnStarting(() =>
        {
            ApplySecurityHeaders(ctx.Response);
            return Task.CompletedTask;
        });
        return next();
    }

    private static void ApplySecurityHeaders(HttpResponse resp)
    {
        resp.Headers.TryAdd("X-Content-Type-Options", "nosniff");
        resp.Headers.TryAdd("X-Frame-Options", "DENY");
        resp.Headers.TryAdd("Referrer-Policy", "no-referrer");
    }

    /// <summary>Form A: the source form you fill in.</summary>
    private static RazorComponentResult FormA(HttpContext ctx, WebState state)
    {
        state.Records.TryGetValue(ctx.Request.Query["record"].ToString(), out var record);
        var mapping = state.CurrentMapping();
        bool readOnly = record is { Count: > 0 };
        return new RazorComponentResult<FormAPage>(new Dictionary<string, object?>
        {
            [nameof(FormAPage.Record)] = readOnly ? record : SampleRecord,
            [nameof(FormAPage.TargetUrl)] = mapping.TargetUrl,
            [nameof(FormAPage.Fields)] = ContactValidation.Fields,
            [nameof(FormAPage.TargetFields)] = mapping.Fields
                .Where(f => ContactValidation.Fields.Contains(f.Key))
                .ToDictionary(f => f.Key, f => f.Value),
            [nameof(FormAPage.ReadOnly)] = readOnly,
        });
    }

    /// <summary>
    /// Form B: a local test target with the same fields as the sample mapping.
    /// Submissions stay in memory on this machine and are never forwarded.
    /// </summary>
    private static async Task<RazorComponentResult> FormB(HttpContext ctx, WebState state)
    {
        OrderedDictionary<string, string>? submitted = null;
        var errors = new List<string>();
        if (HttpMethods.IsPost(ctx.Request.Method))
        {
            var form = new OrderedDictionary<string, string>();
            if (ctx.Request.HasFormContentType)
                foreach (var (key, values) in await ctx.Request.ReadFormAsync())
                    form[key] = values.FirstOrDefault() ?? "";
            errors = ContactValidation.Validate(form, ContactValidation.FormBRequired);
            if (errors.Count == 0)
            {
                submitted = form;
                lock (state.FormBSubmissions) state.FormBSubmissions.Add(submitted);
            }
        }
        return new RazorComponentResult<FormBPage>(new Dictionary<string, object?>
        {
            [nameof(FormBPage.Submitted)] = submitted,
            [nameof(FormBPage.Errors)] = errors,
        })
        { StatusCode = errors.Count > 0 ? 400 : 200 };
    }

    /// <summary>Python's <c>str()</c> of a JSON value, so the API accepts the same payloads.</summary>
    private static string PayloadText(JsonNode? node) => node switch
    {
        null => "None",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.True => "True",
        JsonValue v when v.GetValueKind() == JsonValueKind.False => "False",
        _ => PyText.Repr(node),
    };

    private static async Task<IResult> TransferAsync(HttpContext ctx, WebState state)
    {
        // Only JSON bodies are read, so a plain cross-site form post (which cannot send
        // Content-Type: application/json) never starts a transfer.
        JsonObject payload = new();
        if (ctx.Request.HasJsonContentType())
        {
            try
            {
                payload = await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                payload = new JsonObject();
            }
        }
        var data = new OrderedDictionary<string, string>();
        foreach (string k in ContactValidation.Fields)
            data[k] = payload.TryGetPropertyValue(k, out var v) ? PayloadText(v).Trim() : "";

        var errors = ContactValidation.Validate(data, ContactValidation.FormARequired);
        if (errors.Count > 0)
            return Results.Json(new { ok = false, error = string.Join(" ", errors) }, statusCode: 400);

        string recordId = Guid.NewGuid().ToString("N");
        state.Records[recordId] = data;
        string sourceUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}/?record={recordId}";
        bool headless = Environment.GetEnvironmentVariable(CrossFillInfo.HeadlessEnv) == "1";

        // The transfer runs in the background so the browser can stay open for review
        // after we reply; we answer as soon as the form has been filled.
        var filled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                var mapping = state.CurrentMapping();
                var result = await Engine.RunTransferAsync(
                    sourceUrl, mapping, headless: headless,
                    keepOpen: !headless,   // leave the browser up so you can review Form B
                    onFilled: r => filled.TrySetResult(r.ToJson()));
                filled.TrySetResult(result.ToJson());
            }
            catch (Exception e)
            {
                filled.TrySetResult(JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "CrossFill stopped without a result.\ncrossfill: " + e.Message,
                }));
            }
        });

        string json;
        try
        {
            json = await filled.Task;
        }
        finally
        {
            state.Records.TryRemove(recordId, out _);
        }
        bool ok = JsonNode.Parse(json)?["ok"]?.GetValue<bool>() == true;
        return Results.Content(json, "application/json", statusCode: ok ? 200 : 500);
    }
}
