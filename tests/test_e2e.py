"""End-to-end tests: a real headless Chromium against the local test Form B."""

from __future__ import annotations

import json
import time

import pytest
import requests
from conftest import ROOT, SAMPLE, TEXT_SAMPLE

from crossfill import load_mapping, run_transfer
from crossfill.mapping import DEFAULT_MAPPING

pytestmark = pytest.mark.browser


def test_fill_with_mapping(form_b, source_url):
    target, received = form_b
    mapping = load_mapping(DEFAULT_MAPPING, target_url=target)

    result = run_transfer(source_url, mapping, headless=True)

    assert result.ok, result.error
    assert result.data == SAMPLE
    assert result.verified == SAMPLE
    assert result.filled["name"] == "#ufullname"
    assert result.filled["contact_method"] == "input[name='ucontactpref']"
    assert received == []   # the target is never submitted


def test_auto_match_without_selectors(form_b, source_url):
    """With no selectors at all, CrossFill must still find the right fields
    and ignore the hidden registration form."""
    target, _ = form_b
    mapping = {"target_url": target, "form_selector": "form:visible", "fields": {}}

    result = run_transfer(source_url, mapping, headless=True)

    assert result.ok, result.error
    assert result.verified == TEXT_SAMPLE
    assert all(v.startswith("auto-match") for v in result.filled.values())


def test_mixed_selectors_never_double_fill(form_b, source_url):
    """A field with an explicit selector must not be picked again by the
    auto-matcher for another key."""
    target, _ = form_b
    mapping = {"target_url": target, "form_selector": "form:visible",
               "fields": {"phone": "#ucontactno"}}

    result = run_transfer(source_url, mapping, headless=True)

    assert result.ok, result.error
    assert result.filled["phone"] == "#ucontactno"
    assert len(set(result.filled.values())) == len(result.filled)


def test_data_file_with_select_and_checkbox(form_b, tmp_path):
    """Records from a JSON file can fill selects (by label) and checkboxes."""
    target, received = form_b
    record = {**SAMPLE, "topic": "Support", "newsletter": True}
    data_file = tmp_path / "record.json"
    data_file.write_text(json.dumps([record]), encoding="utf-8")

    from crossfill import load_record

    result = run_transfer(mapping=load_mapping(DEFAULT_MAPPING, target_url=target),
                          data=load_record(data_file), headless=True)

    assert result.ok, (result.error, result.mismatched)
    assert result.verified["topic"] == "support"
    assert result.verified["newsletter"] == "true"
    assert received == []


def test_unknown_select_option_is_reported(form_b):
    target, _ = form_b
    result = run_transfer(mapping=load_mapping(DEFAULT_MAPPING, target_url=target),
                          data={"name": "A", "topic": "Billing"}, headless=True)

    assert not result.ok
    assert result.mismatched == ["topic"]
    assert result.verified["name"] == "A"


def test_dry_run_types_nothing(form_b, source_url):
    target, _ = form_b
    result = run_transfer(source_url, load_mapping(DEFAULT_MAPPING, target_url=target),
                          headless=True, dry_run=True)

    assert result.ok and result.dry_run
    assert set(result.filled) == set(SAMPLE)
    assert result.verified == {}


def test_cli_run_with_data_file(form_b, tmp_path):
    import subprocess
    import sys

    target, _ = form_b
    data_file = tmp_path / "people.csv"
    data_file.write_text("name,email\nAda,ada@example.com\nGrace,grace@example.com\n",
                         encoding="utf-8")
    proc = subprocess.run(
        [sys.executable, "-m", "crossfill", "run", "--data", str(data_file), "--record", "1",
         "--target-url", target, "--headless"],
        cwd=ROOT, capture_output=True, text=True, timeout=120,
    )
    assert proc.returncode == 0, proc.stderr
    line = next(ln for ln in proc.stdout.splitlines() if ln.startswith("CROSSFILL_RESULT "))
    body = json.loads(line.split(" ", 1)[1])
    assert body["verified"] == {"name": "Grace", "email": "grace@example.com"}


def test_web_app_fills_without_submitting(form_b, crossfill_app):
    """Full flow through /api/transfer: Form B is filled but never submitted,
    even if a caller asks for it."""
    _, received = form_b
    base, _ = crossfill_app

    r = requests.post(f"{base}/api/transfer", json={**SAMPLE, "mode": "submit"}, timeout=120)
    body = r.json()

    assert r.status_code == 200, body
    assert body["ok"]
    assert body["verified"] == SAMPLE
    assert "submitted" not in body
    time.sleep(1)   # give any stray POST time to arrive
    assert received == []
