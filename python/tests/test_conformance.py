"""Shared conformance cases from spec/conformance/cases.json.

The .NET implementation runs the same cases, so both behave identically.
Also checks that the copies of the shared spec files bundled in this package
match spec/ exactly (run ``python sync_spec.py`` to refresh them).
"""

from __future__ import annotations

import copy
import json

import pytest
from conftest import REPO_ROOT, ROOT

from crossfill import MappingError, SourceError, load_records, validate_mapping
from crossfill.matcher import best_candidate, score, synonyms_for
from crossfill.web.app import FORM_A_REQUIRED, FORM_B_REQUIRED, validate_contact
from sync_spec import BUNDLED

SPEC = REPO_ROOT / "spec"
CASES = json.loads((SPEC / "conformance" / "cases.json").read_text(encoding="utf-8"))


def _ids(key):
    return [c.get("name") or c.get("file") or c.get("field") for c in CASES[key]]


@pytest.mark.parametrize("case", CASES["mapping_validation"], ids=_ids("mapping_validation"))
def test_mapping_validation(case):
    mapping = copy.deepcopy(case["mapping"])
    if "error" in case:
        with pytest.raises(MappingError) as exc:
            validate_mapping(mapping)
        assert str(exc.value) == case["error"]
    else:
        result = validate_mapping(mapping)
        for key, value in case["result"].items():
            assert result[key] == value


@pytest.mark.parametrize("case", CASES["synonyms_for"], ids=_ids("synonyms_for"))
def test_synonyms_for(case):
    assert synonyms_for(case["field"], case.get("synonyms")) == case["expect"]


@pytest.mark.parametrize("case", CASES["score"], ids=_ids("score"))
def test_score(case):
    assert score(case["field"], case["text"], case["type"], case.get("synonyms")) == case["expect"]


@pytest.mark.parametrize("case", CASES["best_candidate"], ids=_ids("best_candidate"))
def test_best_candidate(case):
    got = best_candidate(case["field"], case["candidates"], set(case["taken"]), case.get("synonyms"))
    assert got == case["expect"]


@pytest.mark.parametrize("case", CASES["records"], ids=_ids("records"))
def test_records(case):
    path = SPEC / "conformance" / "data" / case["file"]
    if "records" in case:
        assert load_records(path) == case["records"]
        return
    with pytest.raises(SourceError) as exc:
        load_records(path)
    message = str(exc.value).replace(str(path), "{path}")
    if "error" in case:
        assert message == case["error"]
    else:
        assert message.startswith(case["error_prefix"])


@pytest.mark.parametrize("case", CASES["contact_validation"], ids=_ids("contact_validation"))
def test_contact_validation(case):
    labels = FORM_A_REQUIRED if case["form"] == "a" else FORM_B_REQUIRED
    assert validate_contact(case["values"], labels) == case["errors"]


@pytest.mark.parametrize("bundled, shared", BUNDLED, ids=lambda p: p.name)
def test_bundled_spec_files_match(bundled, shared):
    assert (ROOT / bundled).read_bytes() == (REPO_ROOT / shared).read_bytes(), (
        f"{bundled} differs from {shared}; run: python sync_spec.py"
    )
