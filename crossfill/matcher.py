"""Automatic field matching.

When a mapping does not give a selector for a field (or the selector is not
found on the page), CrossFill looks at every visible input inside the target
form and scores it against a list of synonyms using its name, id,
placeholder, aria-label and <label> text.

Scoring is a pure function (:func:`score`) so it can be unit tested without a
browser. Mappings can add their own synonyms through the ``synonyms`` key.
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence

SYNONYMS: dict[str, list[str]] = {
    "name": ["fullname", "full name", "full_name", "yourname", "name"],
    "first_name": ["firstname", "first name", "first_name", "fname", "given"],
    "last_name": ["lastname", "last name", "last_name", "lname", "surname", "family"],
    "email": ["emailid", "email", "e-mail", "mail"],
    "phone": ["contactno", "contact no", "mobile", "phone", "telephone", "tel", "contact"],
    "message": ["message", "msg", "comment", "query", "enquiry", "inquiry", "description"],
    "company": ["company", "organisation", "organization", "employer", "business"],
    "address": ["address", "street", "addr"],
    "city": ["city", "town"],
    "zip": ["zip", "postcode", "postal", "pincode", "pin code"],
    "country": ["country", "nation"],
}

# Bonus points when the element type agrees with the field.
_TYPE_HINTS = {
    ("email", "email"): 20,
    ("phone", "tel"): 20,
    ("message", "textarea"): 20,
}

# Collects visible, fillable fields inside the form with the text we score on.
# Checkboxes and radios are skipped: guessing them is too risky, so they must
# be mapped with an explicit selector.
_COLLECT_JS = """
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
"""


def synonyms_for(field_key: str, extra: Mapping[str, Sequence[str]] | None = None) -> list[str]:
    """Synonyms for a field, most specific first. Mapping-supplied words come first."""
    words = list((extra or {}).get(field_key, []))
    words += SYNONYMS.get(field_key, [field_key])
    seen: set[str] = set()
    return [w.lower() for w in words if not (w.lower() in seen or seen.add(w.lower()))]


def score(
    field_key: str,
    text: str,
    el_type: str,
    extra_synonyms: Mapping[str, Sequence[str]] | None = None,
) -> int:
    """Return a match score (higher is better, 0 = no match)."""
    best = 0
    for rank, word in enumerate(synonyms_for(field_key, extra_synonyms)):
        if word in text:
            best = max(best, 100 - rank)  # earlier synonyms are more specific
    if best:
        best += _TYPE_HINTS.get((field_key, el_type), 0)
    return best


def best_candidate(
    field_key: str,
    candidates: Sequence[Mapping],
    taken: set[int],
    extra_synonyms: Mapping[str, Sequence[str]] | None = None,
) -> int | None:
    """Pick the best unused candidate index for ``field_key`` (pure, testable)."""
    best_idx, best_score = None, 0
    for c in candidates:
        if c["index"] in taken:
            continue
        s = score(field_key, c["text"], c["type"], extra_synonyms)
        if s > best_score:
            best_idx, best_score = c["index"], s
    return best_idx


def collect_candidates(form_locator) -> list[dict]:
    """Describe every visible, fillable element in the form."""
    return form_locator.evaluate(_COLLECT_JS)


def auto_match(
    form_locator,
    field_key: str,
    taken: set[int],
    extra_synonyms: Mapping[str, Sequence[str]] | None = None,
) -> int | None:
    """Find the index (among form.querySelectorAll('input, textarea, select'))
    of the best matching element for field_key, skipping already-used ones."""
    return best_candidate(field_key, collect_candidates(form_locator), taken, extra_synonyms)
