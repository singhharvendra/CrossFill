using System.Text.Json;
using System.Text.Json.Nodes;

namespace CrossFill;

/// <summary>Where to read data from when the source is a web page.</summary>
public sealed class MappingSource
{
    /// <summary>URL of the source page, or <c>null</c> to require one on the command line.</summary>
    public string? Url { get; init; }

    /// <summary>Field name to CSS selector on the source page, or <c>null</c> for the defaults.</summary>
    public OrderedDictionary<string, string>? Fields { get; init; }
}

/// <summary>
/// A validated mapping file: where the target form lives and, optionally, which
/// CSS selector holds each field. Anything not listed is found by the
/// automatic matcher (see <see cref="Matcher"/>).
/// </summary>
/// <remarks>
/// The format is documented in <c>docs/mapping-reference.md</c> and described by the
/// JSON Schema in <c>spec/schema/mapping.schema.json</c>.
/// </remarks>
public sealed class Mapping
{
    /// <summary>Mapping format version (always 1).</summary>
    public int Version { get; init; } = MappingLoader.SchemaVersion;

    /// <summary>Human-readable name of the target.</summary>
    public string? Name { get; init; }

    /// <summary>Free-text description.</summary>
    public string? Description { get; init; }

    /// <summary>URL of the page that contains the target form.</summary>
    public required string TargetUrl { get; set; }

    /// <summary>CSS selector of the one form to fill (default <c>form</c>).</summary>
    public string FormSelector { get; init; } = "form";

    /// <summary>Field name to CSS selector inside <see cref="FormSelector"/>.</summary>
    public OrderedDictionary<string, string> Fields { get; init; } = new();

    /// <summary>Optional source page settings.</summary>
    public MappingSource? Source { get; init; }

    /// <summary>Extra words for the automatic matcher, per field.</summary>
    public OrderedDictionary<string, IReadOnlyList<string>>? Synonyms { get; init; }
}

/// <summary>Load and validate the JSON that describes a target form.</summary>
public static class MappingLoader
{
    /// <summary>The mapping format version this CrossFill reads.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The bundled sample mapping for the local test Form B.</summary>
    public static string DefaultMappingPath { get; } = Path.Combine(AppContext.BaseDirectory, "mappings", "form_b.json");

    /// <summary>The JSON Schema for mapping files.</summary>
    public static string SchemaPath { get; } = Path.Combine(AppContext.BaseDirectory, "schema", "mapping.schema.json");

    /// <summary>Fields read from a source web page when the mapping has no <c>source</c> section.</summary>
    public static IReadOnlyDictionary<string, string> DefaultSourceFields { get; } = new OrderedDictionary<string, string>
    {
        ["name"] = "#name",
        ["email"] = "#email",
        ["phone"] = "#phone",
        ["message"] = "#message",
    };

    private static readonly HashSet<string> AllowedKeys =
    [
        "$schema", "version", "name", "description", "target_url",
        "form_selector", "fields", "source", "synonyms",
    ];

    private static readonly HashSet<string> AllowedUrlSchemes = ["http", "https", "file"];

    /// <summary>
    /// Load and validate a mapping file.
    /// </summary>
    /// <param name="path">Defaults to the bundled local test mapping.</param>
    /// <param name="targetUrl">
    /// Overrides the file's <c>target_url</c> (as does the <c>CROSSFILL_TARGET_URL</c>
    /// environment variable) so one mapping can be pointed at staging or production.
    /// </param>
    /// <exception cref="MappingException">The file is missing, not JSON, or invalid.</exception>
    public static Mapping Load(string? path = null, string? targetUrl = null)
    {
        path = string.IsNullOrEmpty(path) ? DefaultMappingPath : path;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new MappingException($"Mapping file not found: {path}");
        }
        catch (JsonException e)
        {
            throw new MappingException($"{path}: invalid JSON at line {(e.LineNumber ?? 0) + 1}: {JsonMessage(e)}");
        }

        string? overrideUrl = targetUrl ?? Environment.GetEnvironmentVariable(CrossFillInfo.TargetUrlEnv);
        if (!string.IsNullOrEmpty(overrideUrl) && node is JsonObject obj)
            obj["target_url"] = overrideUrl;
        return Validate(node, path);
    }

    /// <summary>Validate a parsed mapping and fill in defaults.</summary>
    /// <exception cref="MappingException">With a message that names the bad key.</exception>
    public static Mapping Validate(JsonNode? mapping, string origin = "mapping")
    {
        if (mapping is not JsonObject obj)
            throw new MappingException($"{origin}: top level must be a JSON object");

        var unknown = obj.Select(p => p.Key).Where(k => !AllowedKeys.Contains(k)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
            throw new MappingException($"{origin}: unknown key(s) {PyText.List(unknown)}; check for typos");

        if (obj.ContainsKey("version") && !IsVersionOne(obj["version"]))
            throw new MappingException(
                $"{origin}: unsupported version {PyText.Repr(obj["version"])}; this CrossFill reads version {SchemaVersion}");

        foreach (string key in new[] { "target_url", "fields" })
            if (!obj.ContainsKey(key))
                throw new MappingException($"{origin}: missing required key '{key}'");
        string targetUrl = CheckUrl(obj["target_url"], $"{origin}: target_url");
        var fields = CheckSelectorMap(obj["fields"], $"{origin}: fields");

        string formSelector = "form";
        if (obj.ContainsKey("form_selector"))
        {
            if (AsString(obj["form_selector"]) is not { } fs || fs.Trim().Length == 0)
                throw new MappingException($"{origin}: form_selector must be a non-empty CSS selector");
            formSelector = fs;
        }

        MappingSource? source = null;
        if (obj["source"] is { } sourceNode)
        {
            if (sourceNode is not JsonObject src)
                throw new MappingException($"{origin}: source must be an object");
            source = new MappingSource
            {
                Url = src.ContainsKey("url") ? CheckUrl(src["url"], $"{origin}: source.url") : null,
                Fields = src.ContainsKey("fields") ? CheckSelectorMap(src["fields"], $"{origin}: source.fields") : null,
            };
        }

        OrderedDictionary<string, IReadOnlyList<string>>? synonyms = null;
        if (obj["synonyms"] is { } synNode)
        {
            const string error = "synonyms must map each field name to a non-empty list of strings";
            if (synNode is not JsonObject synObj)
                throw new MappingException($"{origin}: {error}");
            synonyms = new();
            foreach (var (key, value) in synObj)
            {
                if (value is not JsonArray words || words.Count == 0)
                    throw new MappingException($"{origin}: {error}");
                var list = new List<string>();
                foreach (var w in words)
                {
                    if (AsString(w) is not { Length: > 0 } word)
                        throw new MappingException($"{origin}: {error}");
                    list.Add(word);
                }
                synonyms[key] = list;
            }
        }

        return new Mapping
        {
            Version = SchemaVersion,
            Name = AsString(obj["name"]),
            Description = AsString(obj["description"]),
            TargetUrl = targetUrl,
            FormSelector = formSelector,
            Fields = fields,
            Source = source,
            Synonyms = synonyms,
        };
    }

    /// <summary>Selectors used to read a source web page.</summary>
    public static IReadOnlyDictionary<string, string> SourceFields(Mapping mapping) =>
        mapping.Source?.Fields is { Count: > 0 } f ? f : new OrderedDictionary<string, string>(DefaultSourceFields);

    private static bool IsVersionOne(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.GetValue<double>() == SchemaVersion;

    private static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static string CheckUrl(JsonNode? node, string where)
    {
        if (AsString(node) is not { } url || url.Trim().Length == 0)
            throw new MappingException($"{where} must be a non-empty string");
        if (!AllowedUrlSchemes.Contains(UrlScheme(url)))
            throw new MappingException($"{where} must start with http://, https:// or file:// (got {PyText.Repr(url)})");
        return url;
    }

    /// <summary>The URL scheme, lower case, parsed like Python's <c>urllib.parse.urlparse</c>.</summary>
    private static string UrlScheme(string url)
    {
        url = url.TrimStart(' ', '\t', '\n', '\r', '\f', '\v', '\0');
        int colon = url.IndexOf(':');
        if (colon <= 0 || !char.IsAsciiLetter(url[0])) return "";
        string scheme = url[..colon];
        return scheme.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.') ? scheme.ToLowerInvariant() : "";
    }

    private static OrderedDictionary<string, string> CheckSelectorMap(JsonNode? node, string where)
    {
        if (node is not JsonObject obj)
            throw new MappingException($"{where} must be an object of field name -> CSS selector");
        var result = new OrderedDictionary<string, string>();
        foreach (var (key, value) in obj)
        {
            if (AsString(value) is not { } selector || selector.Trim().Length == 0)
                throw new MappingException($"{where}.{key} must be a non-empty CSS selector string");
            result[key] = selector;
        }
        return result;
    }

    /// <summary>The parser's message without System.Text.Json's position suffix.</summary>
    internal static string JsonMessage(JsonException e)
    {
        string msg = e.Message;
        foreach (string marker in new[] { " Path:", " LineNumber:" })
        {
            int i = msg.IndexOf(marker, StringComparison.Ordinal);
            if (i >= 0) msg = msg[..i];
        }
        return msg.Trim();
    }
}
