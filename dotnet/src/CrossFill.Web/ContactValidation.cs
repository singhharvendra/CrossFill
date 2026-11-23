using System.Text.RegularExpressions;

namespace CrossFill.Web;

/// <summary>Server-side checks for the demo forms (Form A and the test Form B).</summary>
public static partial class ContactValidation
{
    /// <summary>Every Form A field, in display order.</summary>
    public static IReadOnlyList<string> Fields { get; } =
        ["name", "email", "phone", "message", "topic", "contact_method", "newsletter"];

    /// <summary>Choices offered by the demo forms (Form A and Form B use the same values).</summary>
    public static IReadOnlyList<string> Topics { get; } = ["sales", "support", "other"];

    /// <summary>Preferred-contact choices.</summary>
    public static IReadOnlyList<string> ContactMethods { get; } = ["email", "phone"];

    /// <summary>Form A field → itself, for every required field (all but the newsletter).</summary>
    public static IReadOnlyDictionary<string, string> FormARequired { get; } =
        new OrderedDictionary<string, string>(Fields.Where(k => k != "newsletter").Select(k => KeyValuePair.Create(k, k)));

    /// <summary>Form A field → the test Form B's own input name.</summary>
    public static IReadOnlyDictionary<string, string> FormBRequired { get; } = new OrderedDictionary<string, string>
    {
        ["name"] = "ufullname",
        ["email"] = "uemailid",
        ["phone"] = "ucontactno",
        ["message"] = "umsg",
        ["topic"] = "utopic",
        ["contact_method"] = "ucontactpref",
    };

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex EmailRegex();

    /// <summary>
    /// Check one contact record. <paramref name="labels"/> maps our field name → the form's
    /// own field name. Every field is required except the newsletter checkbox.
    /// </summary>
    /// <returns>Readable problems (empty when the record is valid).</returns>
    public static List<string> Validate(IReadOnlyDictionary<string, string> values, IReadOnlyDictionary<string, string> labels)
    {
        string Get(string key) => (values.TryGetValue(labels[key], out var v) ? v ?? "" : "").Trim();

        var errors = new List<string>();
        var missing = labels.Keys.Where(k => Get(k).Length == 0).Select(k => PyText.Capitalize(k.Replace('_', ' '))).ToList();
        if (missing.Count > 0) errors.Add($"Please fill in: {string.Join(", ", missing)}.");
        if (Get("email").Length > 0 && !EmailRegex().IsMatch(Get("email")))
            errors.Add("Email address is not valid.");
        if (Get("topic").Length > 0 && !Topics.Contains(Get("topic").ToLowerInvariant()))
            errors.Add("Please choose a topic from the list.");
        if (Get("contact_method").Length > 0 && !ContactMethods.Contains(Get("contact_method").ToLowerInvariant()))
            errors.Add("Preferred contact must be Email or Phone.");
        return errors;
    }
}
