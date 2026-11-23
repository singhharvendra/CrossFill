"""Fast unit tests: no browser, no server."""

from __future__ import annotations

import json

import pytest
from conftest import REPO_ROOT

from crossfill import MappingError, SourceError, load_mapping, load_record, load_records, validate_mapping
from crossfill.cli import main
from crossfill.mapping import DEFAULT_MAPPING, SCHEMA_FILE, source_fields
from crossfill.matcher import best_candidate, score, synonyms_for

# --- mapping ------------------------------------------------------------------


def test_bundled_mapping_is_valid(clean_env):
    m = load_mapping()
    assert m["target_url"] == "http://127.0.0.1:5000/form-b"
    assert m["fields"]["name"] == "#ufullname"
    assert m["version"] == 1


def test_bundled_mapping_matches_json_schema():
    jsonschema = pytest.importorskip("jsonschema")
    schema = json.loads(SCHEMA_FILE.read_text(encoding="utf-8"))
    jsonschema.Draft202012Validator.check_schema(schema)
    jsonschema.validate(json.loads(DEFAULT_MAPPING.read_text(encoding="utf-8")), schema)


@pytest.mark.parametrize("path", sorted((REPO_ROOT / "examples" / "mappings").glob("*.json")),
                         ids=lambda p: p.name)
def test_example_mappings_are_valid(path, clean_env):
    load_mapping(path)
    jsonschema = pytest.importorskip("jsonschema")
    schema = json.loads(SCHEMA_FILE.read_text(encoding="utf-8"))
    jsonschema.validate(json.loads(path.read_text(encoding="utf-8")), schema)


def test_example_data_files_load():
    assert load_record(REPO_ROOT / "examples" / "data" / "record.json")["newsletter"] == "true"
    assert len(load_records(REPO_ROOT / "examples" / "data" / "contacts.csv")) == 2


def test_target_url_override(clean_env, monkeypatch):
    assert load_mapping(target_url="https://a.example/x")["target_url"] == "https://a.example/x"
    monkeypatch.setenv("CROSSFILL_TARGET_URL", "https://b.example/y")
    assert load_mapping()["target_url"] == "https://b.example/y"


def test_defaults_are_filled():
    m = validate_mapping({"target_url": "https://x.example", "fields": {}})
    assert m["form_selector"] == "form"
    assert source_fields(m)["email"] == "#email"


@pytest.mark.parametrize("bad, message", [
    ({"fields": {}}, "target_url"),
    ({"target_url": "https://x.example"}, "fields"),
    ({"target_url": "javascript:alert(1)", "fields": {}}, "http"),
    ({"target_url": "https://x.example", "fields": {"a": ""}}, "fields.a"),
    ({"target_url": "https://x.example", "fields": {}, "feilds": {}}, "typos"),
    ({"target_url": "https://x.example", "fields": {}, "version": 9}, "version"),
    ({"target_url": "https://x.example", "fields": {}, "synonyms": {"a": []}}, "synonyms"),
    ({"target_url": "https://x.example", "fields": {}, "source": {"url": "ftp://x"}}, "source.url"),
    ([], "object"),
])
def test_invalid_mappings(bad, message):
    with pytest.raises(MappingError, match=message):
        validate_mapping(bad)


def test_unreadable_mapping_files(tmp_path):
    with pytest.raises(MappingError, match="not found"):
        load_mapping(tmp_path / "missing.json")
    broken = tmp_path / "broken.json"
    broken.write_text("{ nope", encoding="utf-8")
    with pytest.raises(MappingError, match="line 1"):
        load_mapping(broken)


# --- matcher ------------------------------------------------------------------


def test_score_prefers_specific_synonyms():
    assert score("name", "ufullname full name", "text") > score("name", "uname", "text")
    assert score("email", "unrelated", "text") == 0


def test_type_hint_only_breaks_ties():
    assert score("email", "uemailid", "email") > score("email", "uemailid", "text")
    assert score("email", "nothing here", "email") == 0   # type alone is not a match


def test_extra_synonyms_win():
    assert synonyms_for("topic", {"topic": ["Subject"]})[0] == "subject"
    assert score("topic", "your subject", "text") == 0
    assert score("topic", "your subject", "text", {"topic": ["subject"]}) > 0


def test_best_candidate_skips_taken():
    candidates = [
        {"index": 0, "type": "text", "text": "ufullname"},
        {"index": 1, "type": "email", "text": "uemailid"},
        {"index": 2, "type": "text", "text": "full name backup"},
    ]
    assert best_candidate("name", candidates, set()) == 0
    assert best_candidate("name", candidates, {0}) == 2
    assert best_candidate("email", candidates, set()) == 1
    assert best_candidate("message", candidates, set()) is None


# --- sources ------------------------------------------------------------------


def test_json_records(tmp_path):
    f = tmp_path / "r.json"
    f.write_text(json.dumps({"name": " Ada ", "age": 36, "ok": True, "none": None}), encoding="utf-8")
    assert load_record(f) == {"name": "Ada", "age": "36", "ok": "true", "none": ""}


def test_csv_records(tmp_path):
    f = tmp_path / "r.csv"
    f.write_text("﻿name,email\nAda,a@x.io\nGrace,g@x.io\n", encoding="utf-8")
    assert [r["name"] for r in load_records(f)] == ["Ada", "Grace"]
    assert load_record(f, 1)["email"] == "g@x.io"
    with pytest.raises(SourceError, match="out of range"):
        load_record(f, 5)


@pytest.mark.parametrize("name, content, message", [
    ("r.txt", "x", "unsupported"),
    ("r.json", "[]", "no records"),
    ("r.json", '{"a": {"b": 1}}', "plain value"),
    ("r.json", "{", "invalid JSON"),
])
def test_bad_data_files(tmp_path, name, content, message):
    f = tmp_path / name
    f.write_text(content, encoding="utf-8")
    with pytest.raises(SourceError, match=message):
        load_records(f)


# --- cli ----------------------------------------------------------------------


def test_cli_validate(tmp_path, capsys, clean_env):
    bad = tmp_path / "bad.json"
    bad.write_text('{"fields": {}}', encoding="utf-8")
    assert main(["validate", str(DEFAULT_MAPPING)]) == 0
    assert main(["validate", str(DEFAULT_MAPPING), str(bad)]) == 2
    out = capsys.readouterr().out
    assert "OK" in out and "FAIL" in out


def test_cli_run_usage_errors(tmp_path, capsys):
    assert main(["run", "--data", str(tmp_path / "missing.json")]) == 2
    assert main(["run"]) == 2
    assert "give --source-url or --data" in capsys.readouterr().err


def test_cli_legacy_form_maps_to_run(tmp_path, capsys):
    # Old-style invocation without a subcommand still parses as `run`.
    assert main(["--data", str(tmp_path / "missing.json")]) == 2
    assert "Data file not found" in capsys.readouterr().err


def test_cli_version(capsys):
    from crossfill import __version__

    with pytest.raises(SystemExit) as exc:
        main(["--version"])
    assert exc.value.code == 0
    assert __version__ in capsys.readouterr().out
