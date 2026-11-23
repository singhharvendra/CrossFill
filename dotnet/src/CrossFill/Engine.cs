using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using PlaywrightTimeoutException = System.TimeoutException;   // what Playwright throws on a timeout

namespace CrossFill;

/// <summary>What a transfer did. Contains field values only in <see cref="Data"/> and <see cref="Verified"/>.</summary>
public sealed class TransferResult
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Every field was filled and read back as expected.</summary>
    public bool Ok { get; set; }

    /// <summary>The target form's URL.</summary>
    public required string TargetUrl { get; init; }

    /// <summary>Fields were matched but nothing was typed.</summary>
    public bool DryRun { get; init; }

    /// <summary>Values read from the source.</summary>
    public OrderedDictionary<string, string> Data { get; set; } = new();

    /// <summary>Field → selector used (or planned).</summary>
    public OrderedDictionary<string, string> Filled { get; } = new();

    /// <summary>Field → value now in the target.</summary>
    public OrderedDictionary<string, string> Verified { get; } = new();

    /// <summary>Fields with no target element.</summary>
    public List<string> Unmatched { get; } = [];

    /// <summary>Filled, but the read-back value differs.</summary>
    public List<string> Mismatched { get; } = [];

    /// <summary>Empty in the source, left alone.</summary>
    public List<string> Skipped { get; } = [];

    /// <summary>Why the transfer failed, if it did.</summary>
    public string? Error { get; set; }

    /// <summary>The result as one line of JSON (snake_case keys, as printed after <c>CROSSFILL_RESULT</c>).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Human-readable one-paragraph summary (contains no field values).</summary>
    public string Summary()
    {
        string state = DryRun ? "PLANNED (dry run)" : Ok ? "OK" : "INCOMPLETE";
        var lines = new List<string> { $"CrossFill {state}: {TargetUrl}" };
        foreach (var (key, how) in Filled)
        {
            string flag = Mismatched.Contains(key) ? " (MISMATCH)" : "";
            lines.Add($"  {key,-14} -> {how}{flag}");
        }
        if (Unmatched.Count > 0) lines.Add($"  unmatched: {string.Join(", ", Unmatched)}");
        if (Skipped.Count > 0) lines.Add($"  skipped (empty): {string.Join(", ", Skipped)}");
        if (Error is not null) lines.Add($"  error: {Error}");
        return string.Join("\n", lines);
    }
}

/// <summary>
/// CrossFill engine: source data → target form.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Read the source data (a web page, or a record passed in by the caller).</item>
/// <item>Open the target form.</item>
/// <item>Match fields: explicit mapping selectors first, automatic matching for the rest.</item>
/// <item>Fill each field according to its element type (text, select, checkbox, radio)
/// and read the value back to verify it.</item>
/// <item>Leave the form unsubmitted. CrossFill never clicks Submit; a person reviews the
/// filled form and submits it.</item>
/// </list>
/// Field values are never written to the log, only field names and selectors, so logs
/// are safe to share in bug reports.
/// </remarks>
public static class Engine
{
    /// <summary>Values a checkbox treats as "checked" (case-insensitive).</summary>
    public static IReadOnlySet<string> Truthy { get; } =
        new HashSet<string>(["true", "yes", "y", "1", "on", "checked", "x"], StringComparer.Ordinal);

    private const string Mark = "data-crossfill-filled";   // set on elements already claimed by a selector

    private const string OptionJs = """
        (sel, wanted) => {
          const w = wanted.trim().toLowerCase();
          for (const o of sel.options) {
            if (o.value.toLowerCase() === w || o.label.trim().toLowerCase() === w) return o.value;
          }
          return null;
        }
        """;

    private const string RadioJs = """
        (el, wanted) => {
          const w = wanted.trim().toLowerCase();
          const label = el.labels && el.labels[0] ? el.labels[0].innerText.trim().toLowerCase() : '';
          return el.value.toLowerCase() === w || label === w;
        }
        """;

    /// <summary>Launch Playwright's Chromium; fall back to installed Chrome or Edge.</summary>
    public static async Task<IBrowser> LaunchBrowserAsync(IPlaywright playwright, bool headless)
    {
        var errors = new List<string>();
        foreach (string? channel in new[] { null, "chrome", "msedge" })
        {
            try
            {
                return await playwright.Chromium.LaunchAsync(new() { Headless = headless, Channel = channel });
            }
            catch (PlaywrightException e)
            {
                errors.Add($"{(channel is null ? "bundled chromium" : $"channel {channel}")}: {e.GetType().Name}");
            }
        }
        throw new InvalidOperationException(
            "No browser could be started. Run 'crossfill playwright install chromium'. " + string.Join("; ", errors));
    }

    internal static async Task<(string Tag, string Kind)> DescribeAsync(ILocator loc)
    {
        var parts = await loc.EvaluateAsync<string[]>(
            "e => [e.tagName.toLowerCase(), (e.getAttribute('type') || '').toLowerCase()]");
        return (parts[0], parts[1]);
    }

    /// <summary>
    /// Put <paramref name="value"/> into the element(s) matched by <paramref name="targets"/>.
    /// </summary>
    /// <returns>The value read back, and whether it matches what was expected.</returns>
    /// <remarks><paramref name="targets"/> may match a radio group; for every other type the first match is used.</remarks>
    /// <exception cref="FillException">No select option or radio button matches the value.</exception>
    public static async Task<(string Value, bool Matches)> FillElementAsync(ILocator targets, string value)
    {
        var el = targets.First;
        var (tag, kind) = await DescribeAsync(el);
        await el.ScrollIntoViewIfNeededAsync();

        if (tag == "select")
        {
            string? option = await el.EvaluateAsync<string?>(OptionJs, value);
            if (option is null)
                throw new FillException($"no option matching {PyText.Repr(value)}");
            await el.SelectOptionAsync(new SelectOptionValue { Value = option });
            string got = await el.InputValueAsync();
            return (got, got == option);
        }

        if (tag == "input" && kind == "checkbox")
        {
            bool want = Truthy.Contains(value.Trim().ToLowerInvariant());
            await el.SetCheckedAsync(want);
            bool got = await el.IsCheckedAsync();
            return (got ? "true" : "false", got == want);
        }

        if (tag == "input" && kind == "radio")
        {
            int count = await targets.CountAsync();
            for (int i = 0; i < count; i++)
            {
                var radio = targets.Nth(i);
                if (await radio.EvaluateAsync<bool>(RadioJs, value))
                {
                    await radio.CheckAsync();
                    return (await radio.InputValueAsync(), await radio.IsCheckedAsync());
                }
            }
            throw new FillException($"no radio button matching {PyText.Repr(value)}");
        }

        await el.FillAsync(value);
        string text = await el.InputValueAsync();
        return (text, text == value);
    }

    /// <summary>Open the target form, match fields and fill them (or just plan, on dry run).</summary>
    public static async Task FillTargetAsync(
        IPage page, Mapping mapping, IReadOnlyDictionary<string, string> data, TransferResult result,
        bool dryRun = false, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        logger.LogInformation("Opening target {Url}", mapping.TargetUrl);
        await page.GotoAsync(mapping.TargetUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        var form = page.Locator(mapping.FormSelector).First;
        await form.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 15000 });

        var allFields = form.Locator("input, textarea, select");
        var plan = new List<(string Key, ILocator Targets, string How)>();

        var pending = new List<string>();
        foreach (var (key, value) in data)
        {
            if (string.IsNullOrEmpty(value)) result.Skipped.Add(key);
            else pending.Add(key);
        }

        // Pass 1: explicit selectors claim their elements first, so the
        // auto-matcher can never pick an element a selector already owns.
        var remaining = new List<string>();
        foreach (string key in pending)
        {
            mapping.Fields.TryGetValue(key, out string? selector);
            var targets = selector is null ? null : form.Locator(selector);
            if (targets is not null && await targets.CountAsync() > 0)
            {
                await targets.First.EvaluateAsync($"e => e.setAttribute('{Mark}', '')");
                plan.Add((key, targets, selector!));
            }
            else
            {
                if (selector is not null)
                    logger.LogWarning("Selector {Selector} for '{Key}' not found; trying auto-match",
                        PyText.Repr(selector), key);
                remaining.Add(key);
            }
        }

        // Pass 2: automatic matching for everything else.
        var taken = new HashSet<int>(await form.EvaluateAsync<int[]>(
            $"f => [...f.querySelectorAll('input, textarea, select')]" +
            $".flatMap((e, i) => e.hasAttribute('{Mark}') ? [i] : [])"));
        foreach (string key in remaining)
        {
            int? idx = await Matcher.AutoMatchAsync(form, key, taken, mapping.Synonyms);
            if (idx is null)
            {
                logger.LogWarning("No field found for '{Key}'", key);
                result.Unmatched.Add(key);
                continue;
            }
            taken.Add(idx.Value);
            plan.Add((key, allFields.Nth(idx.Value), $"auto-match #{idx.Value}"));
        }

        foreach (var (key, targets, how) in plan)
        {
            result.Filled[key] = how;
            if (dryRun) continue;
            string got;
            bool same;
            try
            {
                (got, same) = await FillElementAsync(targets, data[key]);
            }
            catch (FillException e)
            {
                logger.LogWarning("Could not fill '{Key}': {Reason}", key, e.Message);
                result.Mismatched.Add(key);
                continue;
            }
            result.Verified[key] = got;
            if (!same)
            {
                logger.LogWarning("'{Key}' reads back differently after filling", key);
                result.Mismatched.Add(key);
            }
            logger.LogInformation("Filled '{Key}' via {How}", key, how);
        }

        // Remove our bookkeeping attribute so the page is left as we found it.
        await form.EvaluateAsync($"f => f.querySelectorAll('[{Mark}]').forEach(e => e.removeAttribute('{Mark}'))");
    }

    /// <summary>
    /// Copy data into the target form described by <paramref name="mapping"/>. Never submits.
    /// </summary>
    /// <param name="sourceUrl">Source page to read, if <paramref name="data"/> is not given
    /// (defaults to the mapping's <c>source.url</c>).</param>
    /// <param name="mapping">The target; defaults to the bundled local test mapping.</param>
    /// <param name="data">The record to copy. When given, no source page is read.</param>
    /// <param name="sourceFields">Selectors to read on the source page (default: the mapping's).</param>
    /// <param name="headless">Run without a visible browser window.</param>
    /// <param name="keepOpen">Keep the browser open until the user closes the page.</param>
    /// <param name="dryRun">Match fields and report the plan without typing anything.</param>
    /// <param name="onFilled">Called once the form is filled (before any review wait), so a
    /// caller such as the web app can report progress while the browser stays open.</param>
    /// <param name="logger">Receives progress messages; never field values.</param>
    /// <exception cref="ArgumentException">Neither data nor a source URL was given.</exception>
    public static async Task<TransferResult> RunTransferAsync(
        string? sourceUrl = null,
        Mapping? mapping = null,
        IReadOnlyDictionary<string, string>? data = null,
        IReadOnlyDictionary<string, string>? sourceFields = null,
        bool headless = false,
        bool keepOpen = false,
        bool dryRun = false,
        Action<TransferResult>? onFilled = null,
        ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        mapping ??= MappingLoader.Load();
        sourceUrl = string.IsNullOrEmpty(sourceUrl) ? mapping.Source?.Url : sourceUrl;
        if (data is null && string.IsNullOrEmpty(sourceUrl))
            throw new ArgumentException("Give either data or a source URL (or set source.url in the mapping).");

        var result = new TransferResult { TargetUrl = mapping.TargetUrl, DryRun = dryRun };
        bool reported = false;
        void Report()
        {
            if (onFilled is null || reported) return;
            reported = true;
            onFilled(result);
        }

        using var playwright = await Playwright.CreateAsync();
        var browser = await LaunchBrowserAsync(playwright, headless);
        try
        {
            var page = await browser.NewPageAsync();
            page.SetDefaultTimeout(20000);

            if (data is not null)
            {
                result.Data = new OrderedDictionary<string, string>();
                foreach (var (k, v) in data) result.Data[k] = (v ?? "").Trim();
            }
            else
            {
                logger.LogInformation("Reading source {Url}", sourceUrl);
                result.Data = await Sources.ReadWebSourceAsync(
                    page, sourceUrl!, sourceFields ?? MappingLoader.SourceFields(mapping));
            }
            if (!result.Data.Values.Any(v => v.Length > 0))
                throw new InvalidOperationException("The source is empty - nothing to transfer.");

            await FillTargetAsync(page, mapping, result.Data, result, dryRun, logger);
            result.Ok = result.Unmatched.Count == 0 && result.Mismatched.Count == 0;

            Report();

            if (keepOpen && !headless)
            {
                // Leave the window up so the user can review and submit by hand.
                var closed = new TaskCompletionSource();
                page.Close += (_, _) => closed.TrySetResult();
                browser.Disconnected += (_, _) => closed.TrySetResult();
                if (!page.IsClosed) await closed.Task;
            }
        }
        catch (PlaywrightTimeoutException e)
        {
            if (!reported)
            {
                result.Ok = false;
                result.Error = $"Timed out: {e.Message}";
            }
        }
        catch (Exception e)
        {
            // After the result was reported, an error here only means the user
            // closed the review window, which is expected.
            if (!reported)
            {
                result.Ok = false;
                result.Error = e.Message;
            }
        }
        finally
        {
            Report();
            try { await browser.CloseAsync(); }
            catch (PlaywrightException) { /* the user may already have closed the browser */ }
        }
        return result;
    }
}
