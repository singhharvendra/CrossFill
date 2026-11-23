using System.Text.Json;
using Microsoft.Playwright;

namespace CrossFill;

/// <summary>A visible, fillable element inside the target form.</summary>
/// <param name="Index">Position among <c>form.querySelectorAll('input, textarea, select')</c>.</param>
/// <param name="Type">The <c>type</c> attribute, or the tag name, lower case.</param>
/// <param name="Text">Name, id, placeholder, aria-label and label text, lower case.</param>
public sealed record Candidate(int Index, string Type, string Text);

/// <summary>
/// Automatic field matching.
/// </summary>
/// <remarks>
/// When a mapping does not give a selector for a field (or the selector is not
/// found on the page), CrossFill looks at every visible input inside the target
/// form and scores it against a list of synonyms using its name, id,
/// placeholder, aria-label and &lt;label&gt; text.
/// Scoring is a pure function (<see cref="Score"/>) so it can be unit tested
/// without a browser. Mappings can add their own synonyms through the
/// <c>synonyms</c> key.
/// </remarks>
public static class Matcher
{
    /// <summary>Built-in synonyms per field, most specific first.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> BuiltInSynonyms { get; } =
        new OrderedDictionary<string, IReadOnlyList<string>>
        {
            ["name"] = ["fullname", "full name", "full_name", "yourname", "name"],
            ["first_name"] = ["firstname", "first name", "first_name", "fname", "given"],
            ["last_name"] = ["lastname", "last name", "last_name", "lname", "surname", "family"],
            ["email"] = ["emailid", "email", "e-mail", "mail"],
            ["phone"] = ["contactno", "contact no", "mobile", "phone", "telephone", "tel", "contact"],
            ["message"] = ["message", "msg", "comment", "query", "enquiry", "inquiry", "description"],
            ["company"] = ["company", "organisation", "organization", "employer", "business"],
            ["address"] = ["address", "street", "addr"],
            ["city"] = ["city", "town"],
            ["zip"] = ["zip", "postcode", "postal", "pincode", "pin code"],
            ["country"] = ["country", "nation"],
        };

    // Bonus points when the element type agrees with the field.
    private static readonly Dictionary<(string Field, string Type), int> TypeHints = new()
    {
        [("email", "email")] = 20,
        [("phone", "tel")] = 20,
        [("message", "textarea")] = 20,
    };

    // Collects visible, fillable fields inside the form with the text we score on.
    // Checkboxes and radios are skipped: guessing them is too risky, so they must
    // be mapped with an explicit selector.
    internal const string CollectJs = """
        (form) => {
          const out = [];
          const els = form.querySelectorAll('input, textarea, select');
          els.forEach((el, i) => {
            const type = (el.getAttribute('type') || el.tagName).toLowerCase();
            const skip = ['hidden', 'submit', 'button', 'checkbox', 'radio', 'file', 'image', 'reset', 'password'];
            if (skip.includes(type)) return;
            if (el.disabled || el.readOnly) return;
            const r = el.getBoundingClientRect();
            const style = window.getComputedStyle(el);
            if (r.width === 0 || r.height === 0 || style.visibility === 'hidden' || style.display === 'none') return;
            const label = el.labels && el.labels[0] ? el.labels[0].innerText : '';
            out.push({
              index: i,
              type,
              text: [el.name, el.id, el.placeholder, el.getAttribute('aria-label'), label]
                      .filter(Boolean).join(' ').toLowerCase(),
            });
          });
          return out;
        }
        """;

    /// <summary>Synonyms for a field, most specific first. Mapping-supplied words come first.</summary>
    public static IReadOnlyList<string> SynonymsFor(
        string fieldKey, IReadOnlyDictionary<string, IReadOnlyList<string>>? extra = null)
    {
        var words = new List<string>();
        if (extra is not null && extra.TryGetValue(fieldKey, out var own))
            words.AddRange(own);
        words.AddRange(BuiltInSynonyms.TryGetValue(fieldKey, out var builtIn) ? builtIn : [fieldKey]);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (string w in words)
        {
            string lower = w.ToLowerInvariant();
            if (seen.Add(lower)) result.Add(lower);
        }
        return result;
    }

    /// <summary>Return a match score (higher is better, 0 = no match).</summary>
    public static int Score(
        string fieldKey, string text, string elementType,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? extraSynonyms = null)
    {
        int best = 0;
        var words = SynonymsFor(fieldKey, extraSynonyms);
        for (int rank = 0; rank < words.Count; rank++)
            if (text.Contains(words[rank], StringComparison.Ordinal))
                best = Math.Max(best, 100 - rank);   // earlier synonyms are more specific
        if (best > 0)
            best += TypeHints.GetValueOrDefault((fieldKey, elementType));
        return best;
    }

    /// <summary>Pick the best unused candidate index for <paramref name="fieldKey"/> (pure, testable).</summary>
    public static int? BestCandidate(
        string fieldKey, IEnumerable<Candidate> candidates, IReadOnlySet<int> taken,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? extraSynonyms = null)
    {
        int? bestIndex = null;
        int bestScore = 0;
        foreach (var c in candidates)
        {
            if (taken.Contains(c.Index)) continue;
            int s = Score(fieldKey, c.Text, c.Type, extraSynonyms);
            if (s > bestScore)
            {
                bestIndex = c.Index;
                bestScore = s;
            }
        }
        return bestIndex;
    }

    /// <summary>Describe every visible, fillable element in the form.</summary>
    public static async Task<IReadOnlyList<Candidate>> CollectCandidatesAsync(ILocator form)
    {
        var items = await form.EvaluateAsync<JsonElement>(CollectJs);
        return items.EnumerateArray()
            .Select(e => new Candidate(
                e.GetProperty("index").GetInt32(),
                e.GetProperty("type").GetString() ?? "",
                e.GetProperty("text").GetString() ?? ""))
            .ToList();
    }

    /// <summary>
    /// Find the index (among <c>form.querySelectorAll('input, textarea, select')</c>) of the
    /// best matching element for <paramref name="fieldKey"/>, skipping already-used ones.
    /// </summary>
    public static async Task<int?> AutoMatchAsync(
        ILocator form, string fieldKey, IReadOnlySet<int> taken,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? extraSynonyms = null) =>
        BestCandidate(fieldKey, await CollectCandidatesAsync(form), taken, extraSynonyms);
}
