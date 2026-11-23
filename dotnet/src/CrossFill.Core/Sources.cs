using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace CrossFill;

/// <summary>
/// Where CrossFill reads data from.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A <b>web page</b> (the original Form A flow): values are read from the
/// selectors in the mapping's <c>source.fields</c>.</item>
/// <item>A <b>JSON file</b>: one object, or a list of objects (pick one with <c>record</c>).</item>
/// <item>A <b>CSV file</b>: the header row gives field names, each other row is a record.</item>
/// </list>
/// Every source returns a flat, ordered field → value dictionary.
/// </remarks>
public static class Sources
{
    /// <summary>Read every record from a .json or .csv file.</summary>
    /// <exception cref="SourceException">The file is missing, of an unsupported type, or malformed.</exception>
    public static IReadOnlyList<OrderedDictionary<string, string>> LoadRecords(string path)
    {
        string suffix = Path.GetExtension(path).ToLowerInvariant();
        List<OrderedDictionary<string, string>> records;
        try
        {
            records = suffix switch
            {
                ".json" => ReadJson(path),
                ".csv" => ReadCsv(path),
                _ => throw new SourceException($"{path}: unsupported data file type '{suffix}' (use .json or .csv)"),
            };
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SourceException($"Data file not found: {path}");
        }
        catch (JsonException e)
        {
            throw new SourceException($"{path}: invalid JSON at line {(e.LineNumber ?? 0) + 1}: {MappingLoader.JsonMessage(e)}");
        }
        if (records.Count == 0)
            throw new SourceException($"{path}: no records found");
        return records;
    }

    /// <summary>Read one record (0-based <paramref name="index"/>) from a .json or .csv file.</summary>
    public static OrderedDictionary<string, string> LoadRecord(string path, int index = 0)
    {
        var records = LoadRecords(path);
        if (index < 0 || index >= records.Count)
            throw new SourceException($"{path}: record {index} out of range (file has {records.Count})");
        return records[index];
    }

    /// <summary>Open a source page and read the value behind each selector.</summary>
    public static async Task<OrderedDictionary<string, string>> ReadWebSourceAsync(
        IPage page, string sourceUrl, IReadOnlyDictionary<string, string> fields)
    {
        await page.GotoAsync(sourceUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        var data = new OrderedDictionary<string, string>();
        foreach (var (key, selector) in fields)
        {
            var loc = page.Locator(selector);
            int count = await loc.CountAsync();
            if (count == 0) continue;
            var el = loc.First;
            var (tag, kind) = await Engine.DescribeAsync(el);
            if (tag == "input" && kind == "radio")
            {
                // A radio group: the value of the chosen button, or "" if none is chosen.
                string value = "";
                for (int i = 0; i < count; i++)
                {
                    if (await loc.Nth(i).IsCheckedAsync())
                    {
                        value = (await loc.Nth(i).InputValueAsync()).Trim();
                        break;
                    }
                }
                data[key] = value;
            }
            else if (tag == "input" && kind == "checkbox")
            {
                data[key] = await el.IsCheckedAsync() ? "true" : "false";
            }
            else if (tag is "input" or "textarea" or "select")
            {
                data[key] = (await el.InputValueAsync()).Trim();
            }
            else
            {
                // plain text element, e.g. a <span> on a read-only record page
                data[key] = (await el.InnerTextAsync()).Trim();
            }
        }
        return data;
    }

    // --- JSON -----------------------------------------------------------------

    private static List<OrderedDictionary<string, string>> ReadJson(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];
        return items.Select((item, i) => Flatten(item, $"{path} record {i}")).ToList();
    }

    private static OrderedDictionary<string, string> Flatten(JsonElement record, string origin)
    {
        if (record.ValueKind != JsonValueKind.Object)
            throw new SourceException($"{origin}: each record must be an object of field -> value");
        var output = new OrderedDictionary<string, string>();
        foreach (var prop in record.EnumerateObject())
        {
            output[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Object or JsonValueKind.Array => throw new SourceException(
                    $"{origin}: field '{prop.Name}' must be a plain value, not {PyText.TypeName(prop.Value.ValueKind)}"),
                JsonValueKind.Null => "",
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => PyText.Number(prop.Value.GetRawText()),
                _ => prop.Value.GetString()!.Trim(),
            };
        }
        return output;
    }

    // --- CSV ------------------------------------------------------------------

    private static List<OrderedDictionary<string, string>> ReadCsv(string path)
    {
        // UTF-8, with or without a byte-order mark.
        string text;
        using (var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true))
            text = reader.ReadToEnd();

        var rows = ParseCsv(text);
        var result = new List<OrderedDictionary<string, string>>();
        if (rows.Count == 0) return result;
        var header = rows[0];
        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Count == 0) continue;            // blank lines are skipped
            string origin = $"{path} record {result.Count}";
            if (row.Count > header.Count)
                throw new SourceException($"{origin}: field 'None' must be a plain value, not list");
            var record = new OrderedDictionary<string, string>();
            for (int c = 0; c < header.Count; c++)
                record[header[c]] = c < row.Count ? row[c].Trim() : "";
            result.Add(record);
        }
        return result;
    }

    /// <summary>
    /// Split CSV text into rows, following the same rules as Python's <c>csv</c> module
    /// (Excel dialect): comma separated, <c>"</c> quotes, <c>""</c> for a literal quote,
    /// line breaks allowed inside quotes. A blank line is an empty row.
    /// </summary>
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false, fieldStarted = false;
        int i = 0;

        void EndField()
        {
            row.Add(field.ToString());
            field.Clear();
            fieldStarted = false;
        }

        void EndRow()
        {
            if (fieldStarted || row.Count > 0) EndField();
            rows.Add(row);
            row = [];
        }

        while (i < text.Length)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i += 2; continue; }
                    inQuotes = false;
                }
                else field.Append(c);
                i++;
                continue;
            }
            switch (c)
            {
                case '"' when !fieldStarted:
                    inQuotes = fieldStarted = true;
                    break;
                case ',':
                    // Ends this field. A trailing empty field is still added by EndRow,
                    // because the row already has fields.
                    EndField();
                    break;
                case '\r':
                case '\n':
                    EndRow();
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    break;
                default:
                    field.Append(c);
                    fieldStarted = true;
                    break;
            }
            i++;
        }
        if (fieldStarted || row.Count > 0) EndRow();
        return rows;
    }
}
