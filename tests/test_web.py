"""Web app tests that need no browser (Flask test client / plain HTTP)."""

from __future__ import annotations

import requests


def test_rejects_missing_fields(crossfill_app):
    base, _ = crossfill_app
    r = requests.post(f"{base}/api/transfer", json={"name": "", "email": ""}, timeout=30)
    assert r.status_code == 400


def test_transfer_requires_every_field(crossfill_app):
    """Form A data must be complete (newsletter is optional) and the topic chosen."""
    from conftest import SAMPLE

    base, _ = crossfill_app
    r = requests.post(f"{base}/api/transfer", json={**SAMPLE, "topic": "", "phone": ""},
                      timeout=30)
    assert r.status_code == 400
    assert r.json()["error"] == "Please fill in: Phone, Topic."
    r = requests.post(f"{base}/api/transfer", json={**SAMPLE, "topic": "billing"}, timeout=30)
    assert r.status_code == 400 and "choose a topic" in r.json()["error"]


def test_local_form_b_serves_same_fields(crossfill_app):
    """The built-in test Form B keeps the field ids the mapping expects and
    records a manual submission in memory."""
    base, module = crossfill_app
    html = requests.get(f"{base}/form-b", timeout=30).text
    for field_id in ("ufullname", "uemailid", "ucontactno", "umsg", "utopic",
                     "ucontactpref_email", "ucontactpref_phone", "unewsletter"):
        assert f'id="{field_id}"' in html

    module.FORM_B_SUBMISSIONS.clear()
    full = {"ufullname": "Test", "uemailid": "t@example.com", "ucontactno": "5550100",
            "umsg": "Hello", "utopic": "support", "ucontactpref": "phone"}
    r = requests.post(f"{base}/form-b", data=full, timeout=30)
    assert r.status_code == 200
    assert "Received (test only)" in r.text
    assert module.FORM_B_SUBMISSIONS == [full]
    module.FORM_B_SUBMISSIONS.clear()


def test_form_b_rejects_incomplete_submission(crossfill_app):
    """Form B checks every field on the server too, and keeps nothing invalid."""
    base, module = crossfill_app
    module.FORM_B_SUBMISSIONS.clear()
    r = requests.post(f"{base}/form-b", data={"ufullname": "Test", "uemailid": "not-an-email",
                                              "utopic": ""}, timeout=30)
    assert r.status_code == 400
    assert "Not submitted" in r.text
    assert "Phone, Message, Topic, Contact method" in r.text
    assert "Email address is not valid" in r.text
    assert module.FORM_B_SUBMISSIONS == []


def test_rejects_foreign_host_header(crossfill_app):
    """DNS-rebinding guard: only loopback Host headers are served."""
    _, module = crossfill_app
    client = module.app.test_client()
    assert client.get("/health", headers={"Host": "evil.example"}).status_code == 403
    assert client.get("/health", headers={"Host": "localhost:5000"}).status_code == 200
    assert client.get("/health", headers={"Host": "[::1]:5000"}).status_code == 200


def test_security_headers(crossfill_app):
    _, module = crossfill_app
    resp = module.app.test_client().get("/health", headers={"Host": "127.0.0.1"})
    assert resp.headers["X-Frame-Options"] == "DENY"
    assert resp.headers["X-Content-Type-Options"] == "nosniff"


def test_form_a_shows_mapped_selectors(crossfill_app):
    base, _ = crossfill_app
    html = requests.get(base + "/", timeout=30).text
    assert "#ufullname" in html and "#umsg" in html


def test_form_a_has_every_field_type_prefilled(crossfill_app):
    """The editable Form A offers text, textarea, dropdown, radio and checkbox,
    each pre-filled with the sample record."""
    base, _ = crossfill_app
    html = requests.get(base + "/", timeout=30).text
    assert 'value="Ada Lovelace"' in html
    assert '<select id="topic" name="topic" required' in html
    assert " selected" not in html          # you must choose a topic yourself
    assert 'type="radio" name="contact_method"' in html
    assert 'type="checkbox" id="newsletter"' in html
