"""Where CrossFill reads data from.

* A **web page** (the original Form A flow): values are read from the
  selectors in the mapping's ``source.fields``.
* A **JSON file**: one object, or a list of objects (pick one with
  ``record``).
* A **CSV file**: the header row gives field names, each other row is a
  record.

Every source returns a flat ``dict[str, str]``.
"""

from __future__ import annotations

import csv
import json
from pathlib import Path
from typing import Any


class SourceError(ValueError):
    """Raised when source data cannot be read."""


def _flatten(record: Any, origin: str) -> dict[str, str]:
    if not isinstance(record, dict):
        raise SourceError(f"{origin}: each record must be an object of field -> value")
    out = {}
    for key, value in record.items():
        if isinstance(value, (dict, list)):
            raise SourceError(f"{origin}: field '{key}' must be a plain value, not {type(value).__name__}")
        if value is None:
            value = ""
        elif isinstance(value, bool):
            value = "true" if value else "false"
        out[str(key)] = str(value).strip()
    return out


def load_records(path: str | Path) -> list[dict[str, str]]:
    """Read every record from a .json or .csv file."""
    path = Path(path)
    suffix = path.suffix.lower()
    try:
        if suffix == ".json":
            with open(path, encoding="utf-8") as f:
                raw = json.load(f)
            items = raw if isinstance(raw, list) else [raw]
        elif suffix == ".csv":
            with open(path, encoding="utf-8-sig", newline="") as f:
                items = list(csv.DictReader(f))
        else:
            raise SourceError(f"{path}: unsupported data file type '{suffix}' (use .json or .csv)")
    except FileNotFoundError:
        raise SourceError(f"Data file not found: {path}") from None
    except json.JSONDecodeError as exc:
        raise SourceError(f"{path}: invalid JSON at line {exc.lineno}: {exc.msg}") from None
    if not items:
        raise SourceError(f"{path}: no records found")
    return [_flatten(item, f"{path} record {i}") for i, item in enumerate(items)]


def load_record(path: str | Path, index: int = 0) -> dict[str, str]:
    """Read one record (0-based ``index``) from a .json or .csv file."""
    records = load_records(path)
    if not 0 <= index < len(records):
        raise SourceError(f"{path}: record {index} out of range (file has {len(records)})")
    return records[index]


def read_web_source(page, source_url: str, fields: dict[str, str]) -> dict[str, str]:
    """Open a source page and read the value behind each selector."""
    page.goto(source_url, wait_until="domcontentloaded")
    data = {}
    for key, selector in fields.items():
        loc = page.locator(selector)
        if not loc.count():
            continue
        el = loc.first
        tag, kind = el.evaluate(
            "e => [e.tagName.toLowerCase(), (e.getAttribute('type') || '').toLowerCase()]"
        )
        if tag == "input" and kind == "radio":
            # A radio group: the value of the chosen button, or "" if none is chosen.
            data[key] = next((loc.nth(i).input_value().strip() for i in range(loc.count())
                              if loc.nth(i).is_checked()), "")
        elif tag == "input" and kind == "checkbox":
            data[key] = "true" if el.is_checked() else "false"
        elif tag in ("input", "textarea", "select"):
            data[key] = el.input_value().strip()
        else:  # plain text element, e.g. a <span> on a read-only record page
            data[key] = el.inner_text().strip()
    return data
