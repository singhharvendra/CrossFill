"""Mapping files: load and validate the JSON that describes a target form.

A mapping tells CrossFill where the target form lives and, optionally, which
CSS selector holds each field. Anything not listed is found by the automatic
matcher (see :mod:`crossfill.matcher`).

The format is documented in ``docs/mapping-reference.md`` and described by the
JSON Schema in ``crossfill/schema/mapping.schema.json``. Validation here is
deliberately dependency-free so the core package stays small.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

SCHEMA_VERSION = 1
PACKAGE_DIR = Path(__file__).resolve().parent
DEFAULT_MAPPING = PACKAGE_DIR / "mappings" / "form_b.json"
SCHEMA_FILE = PACKAGE_DIR / "schema" / "mapping.schema.json"

# Fields read from a source web page when the mapping has no "source" section.
DEFAULT_SOURCE_FIELDS = {
    "name": "#name",
    "email": "#email",
    "phone": "#phone",
    "message": "#message",
}

_ALLOWED_KEYS = {
    "$schema", "version", "name", "description", "target_url",
    "form_selector", "fields", "source", "synonyms",
}
_ALLOWED_URL_SCHEMES = {"http", "https", "file"}


class MappingError(ValueError):
    """Raised when a mapping file is missing, unreadable or invalid."""


def _check_url(url: Any, where: str) -> None:
    if not isinstance(url, str) or not url.strip():
        raise MappingError(f"{where} must be a non-empty string")
    scheme = urlparse(url).scheme.lower()
    if scheme not in _ALLOWED_URL_SCHEMES:
        raise MappingError(
            f"{where} must start with http://, https:// or file:// (got {url!r})"
        )


def _check_selector_map(value: Any, where: str) -> None:
    if not isinstance(value, dict):
        raise MappingError(f"{where} must be an object of field name -> CSS selector")
    for key, selector in value.items():
        if not isinstance(selector, str) or not selector.strip():
            raise MappingError(f"{where}.{key} must be a non-empty CSS selector string")


def validate_mapping(mapping: Any, origin: str = "mapping") -> dict:
    """Validate a mapping dict and fill in defaults. Returns the same dict.

    Raises :class:`MappingError` with a message that names the bad key.
    """
    if not isinstance(mapping, dict):
        raise MappingError(f"{origin}: top level must be a JSON object")

    unknown = set(mapping) - _ALLOWED_KEYS
    if unknown:
        raise MappingError(f"{origin}: unknown key(s) {sorted(unknown)}; check for typos")

    version = mapping.setdefault("version", SCHEMA_VERSION)
    if version != SCHEMA_VERSION:
        raise MappingError(
            f"{origin}: unsupported version {version!r}; this CrossFill reads version {SCHEMA_VERSION}"
        )

    for key in ("target_url", "fields"):
        if key not in mapping:
            raise MappingError(f"{origin}: missing required key '{key}'")
    _check_url(mapping["target_url"], f"{origin}: target_url")
    _check_selector_map(mapping["fields"], f"{origin}: fields")

    form_selector = mapping.setdefault("form_selector", "form")
    if not isinstance(form_selector, str) or not form_selector.strip():
        raise MappingError(f"{origin}: form_selector must be a non-empty CSS selector")

    source = mapping.get("source")
    if source is not None:
        if not isinstance(source, dict):
            raise MappingError(f"{origin}: source must be an object")
        if "url" in source:
            _check_url(source["url"], f"{origin}: source.url")
        if "fields" in source:
            _check_selector_map(source["fields"], f"{origin}: source.fields")

    synonyms = mapping.get("synonyms")
    if synonyms is not None:
        if not isinstance(synonyms, dict) or not all(
            isinstance(words, list) and words and all(isinstance(w, str) and w for w in words)
            for words in synonyms.values()
        ):
            raise MappingError(
                f"{origin}: synonyms must map each field name to a non-empty list of strings"
            )

    return mapping


def load_mapping(path: str | Path | None = None, *, target_url: str | None = None) -> dict:
    """Load and validate a mapping file.

    ``path`` defaults to the bundled local test mapping. ``target_url`` (or
    the ``CROSSFILL_TARGET_URL`` environment variable) overrides the file's
    ``target_url`` so one mapping can be pointed at staging or production.
    """
    path = Path(path) if path else DEFAULT_MAPPING
    try:
        with open(path, encoding="utf-8") as f:
            mapping = json.load(f)
    except FileNotFoundError:
        raise MappingError(f"Mapping file not found: {path}") from None
    except json.JSONDecodeError as exc:
        raise MappingError(f"{path}: invalid JSON at line {exc.lineno}: {exc.msg}") from None

    override = target_url or os.environ.get("CROSSFILL_TARGET_URL")
    if override:
        mapping["target_url"] = override
    return validate_mapping(mapping, origin=str(path))


def source_fields(mapping: dict) -> dict:
    """Selectors used to read a source web page."""
    return (mapping.get("source") or {}).get("fields") or dict(DEFAULT_SOURCE_FIELDS)
