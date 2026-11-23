# Mapping file reference

A mapping is a small JSON file that describes **one target form**. CrossFill
reads it, opens `target_url`, and fills the fields inside `form_selector`.

Editors that understand JSON Schema (VS Code, JetBrains) give autocompletion
and inline errors when the file starts with a `$schema` line:

```json
{ "$schema": "../spec/schema/mapping.schema.json" }
```

Check a mapping at any time with:

```bash
crossfill validate my-site.json
crossfill run --data record.json --mapping my-site.json --dry-run
```

## Keys

| Key | Required | Type | Meaning |
|-----|----------|------|---------|
| `version` | no (default `1`) | integer | Format version. Only `1` exists today. |
| `name`, `description` | no | string | For people reading the file. |
| `target_url` | **yes** | string | Page that holds the form. `http`, `https` or `file` only. Overridden by `--target-url` or `CROSSFILL_TARGET_URL`. |
| `form_selector` | no (default `"form"`) | CSS selector | The **one** form to fill. Everything outside it (hidden login pop-ups, newsletter boxes, search bars) is never touched. Use a precise selector such as `form#contact` or `form:has(#email)`. |
| `fields` | **yes** (may be `{}`) | object | Field name → CSS selector, looked up *inside* `form_selector`. Fields you leave out are found by automatic matching. |
| `source.url` | no | string | Default source page, so `--source-url` can be omitted. |
| `source.fields` | no | object | Field name → selector on the **source** page. Default: `#name`, `#email`, `#phone`, `#message`. |
| `synonyms` | no | object | Field name → list of extra words for the automatic matcher, checked before the built-in ones. |

Unknown top-level keys are rejected, so a typo such as `"feilds"` fails
loudly instead of being silently ignored.

## Field names

Field names are just the keys of your data. They join three things:

```
data record          mapping.fields            target page
{"email": "..."}  →  "email": "#uemailid"   →  <input id="uemailid">
```

You can use any names you like. The built-in matcher already knows
`name`, `first_name`, `last_name`, `email`, `phone`, `message`, `company`,
`address`, `city`, `zip` and `country`. For anything else, either give a
selector or add `synonyms`.

## How each element type is filled

| Element | Value in your data | What CrossFill does |
|---------|--------------------|---------------------|
| text, email, tel, number, date, `<textarea>` | any text | Types it, then reads it back. If the page reformats it (e.g. a phone mask), the field is reported as `mismatched`. |
| `<select>` | option **value** or visible **label** (case-insensitive) | Selects that option. Reports an error if no option matches. It never waits for an option that isn't there. |
| checkbox | `true`, `yes`, `y`, `1`, `on`, `checked`, `x` → checked, anything else → unchecked | Sets the state explicitly. |
| radio group | option value or its label | Point the selector at the whole group, e.g. `input[name='plan']`. |

Empty values are **skipped** and listed in the result; CrossFill never clears a
field because the source was blank.

## Automatic matching

For each unmapped field, CrossFill scores every visible, enabled input in the
form using its `name`, `id`, `placeholder`, `aria-label` and `<label>` text.
Earlier synonyms score higher, and a matching input type (`type=email` for
`email`) breaks ties. Elements already claimed by an explicit selector are
never picked again.

These are **never** auto-matched, so they need an explicit selector:
checkboxes, radios, file inputs, passwords, hidden, disabled or read-only
fields.

## Result

`crossfill run` prints one machine-readable line:

```
CROSSFILL_RESULT {"ok": true, "target_url": "...", "dry_run": false,
                  "data": {...}, "filled": {"email": "#uemailid"},
                  "verified": {...}, "unmatched": [], "mismatched": [],
                  "skipped": [], "error": null}
```

and a human summary on stderr. Exit codes: `0` everything filled and verified,
`1` incomplete or failed, `2` bad arguments, mapping or data file.
