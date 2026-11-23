# Changelog

All notable changes are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added
- **A .NET implementation** in `dotnet/`, for .NET 11 and .NET 10 (LTS), with
  the same behaviour as the Python one: the `crossfill` tool (`run`,
  `validate`, `serve`, same options, exit codes and `CROSSFILL_RESULT` line),
  the `CrossFill.Core` library, and the demo web app on ASP.NET Core. The tool
  also has `crossfill playwright install chromium` for installing the browser.
- **Shared spec and conformance cases** in `spec/`: the mapping schema, the
  sample mapping and `spec/conformance/cases.json`, which both test suites run
  so the two implementations cannot drift apart.
- Separate CI workflows and release tags for each implementation
  (`py-vX.Y.Z` → PyPI, `dotnet-vX.Y.Z` → NuGet).
- The demo Form A now has a dropdown (Topic), a radio group (Preferred
  contact) and a checkbox (Newsletter) as well as text fields, and opens
  pre-filled with sample data. Form B has matching fields, so every field type
  is transferred.
- **Validation** on Form A and Form B: every field is required except the
  newsletter, the Topic must be chosen from the list, and the email must be
  valid. Both forms check in the browser and on the server.
- **MIT No Attribution (MIT-0)** license: use the code for anything, with no
  credit or license notice needed.

### Changed
- **Repository layout.** The Python package, its tests and `run.bat` moved to
  `python/`. The mapping schema and sample mapping now live in `spec/` (the
  Python package bundles copies). Mapping files should point `$schema` at
  `spec/schema/mapping.schema.json`.
- Form B (the local test target) uses a compact two-column layout, so the whole
  page fits on a laptop screen (1280×720 and up) without scrolling, including
  after a submission or with validation errors. On phones the columns stack.

### Removed
- The citation file (`CITATION.cff`).

### Fixed
- Reading a radio group from a source page now returns the chosen option's
  value, not whether the first button is checked.

## [0.2.0] - 2026-11-22

### Added
- Installable package (`pip install crossfill`) with a `crossfill` command:
  `run`, `validate` and `serve`.
- Read records from **JSON or CSV files** (`--data`, `--record`) as well as
  from a web page.
- **Dropdowns, checkboxes and radio groups** are filled according to their
  type, and every value is read back to check it.
- `--dry-run` matches fields and reports the plan without typing anything.
- Mapping format `version: 1` with a **JSON Schema** for editor autocompletion,
  plus `crossfill validate` with clear errors, including unknown-key typo
  detection.
- Mapping options `source` (source URL and selectors) and `synonyms` (extend
  the matcher).
- More built-in synonyms: first/last name, company, address, city, zip and country.
- `TransferResult` gains `mismatched`, `skipped` and `dry_run`, plus a
  human-readable `summary()`.
- Documented exit codes (0 / 1 / 2) and `-v` logging that never includes field values.
- Demo app: DNS-rebinding guard (loopback `Host` only), security headers, and
  `/health` now reports the version.
- CI on Linux, Windows and macOS; PyPI release workflow; contributor docs and
  templates; a citation file.

### Changed
- The web app moved to `crossfill.web.app`. `python app.py` still works.
- The bundled mapping moved to `crossfill/mappings/form_b.json`.
- `requirements*.txt` were replaced by `pyproject.toml` extras: `.[web]` and `.[dev]`.
- An input-type hint alone no longer counts as a match, which avoids filling
  an unrelated field.
- Explicit selectors claim their elements before auto-matching runs, so a
  field can no longer be filled twice.

## [0.1.0] - 2026-11

### Added
- First version: read Form A, match fields (mapping selectors and automatic
  matching), fill Form B without submitting, local test Form B, web UI.
