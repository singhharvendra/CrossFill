"""CrossFill demo web app.

Run:  crossfill serve      (or: python app.py)   then open http://127.0.0.1:5000

Form A is the page you fill in here. When you click "Transfer", CrossFill asks
you to confirm, then a Playwright browser opens Form A, reads the data, opens
the target form (``target_url`` in the mapping) and fills the matching
fields. The target is never submitted automatically: you review it and press
Submit.

The app also serves its own test target at /form-b, with the same fields as
the sample mapping. Submissions to it are kept in memory only.

Security: the server binds to 127.0.0.1 and rejects requests whose Host header
is not a loopback name, which blocks DNS-rebinding attacks from web pages you
visit. It is a single-user local tool; do not expose it on a network.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import threading
import uuid
from pathlib import Path

from flask import Flask, abort, jsonify, render_template, request

from ..cli import RESULT_PREFIX
from ..mapping import DEFAULT_MAPPING, load_mapping

PROJECT_ROOT = Path(__file__).resolve().parents[2]
FIELDS = ("name", "email", "phone", "message", "topic", "contact_method", "newsletter")
LOOPBACK_HOSTS = {"127.0.0.1", "localhost", "[::1]"}

# Choices offered by the demo forms (Form A and Form B use the same values).
TOPICS = ("sales", "support", "other")
CONTACT_METHODS = ("email", "phone")
_EMAIL_RE = re.compile(r"^[^@\s]+@[^@\s]+\.[^@\s]+$")

app = Flask(__name__)
app.config["CROSSFILL_MAPPING"] = str(DEFAULT_MAPPING)

RECORDS: dict[str, dict] = {}         # record id -> Form A data (kept in memory only)
# Fictional demo values pre-filled into the editable Form A so it can be tested in one click.
SAMPLE_RECORD: dict[str, str] = {
    "name": "Ada Lovelace",
    "email": "ada@example.com",
    "phone": "123-456-7890",
    "message": "I would like to know more about your services. Please call me back this week.",
    "topic": "",            # left unchosen on purpose: you must pick a topic
    "contact_method": "email",
    "newsletter": "true",
}
FORM_B_SUBMISSIONS: list[dict] = []   # what the local test Form B received
_lock = threading.Lock()


def validate_contact(values: dict, labels: dict) -> list[str]:
    """Check one contact record. ``labels`` maps our field name -> the form's
    own field name. Every field is required except the newsletter checkbox.
    Returns a list of readable problems (empty when the record is valid)."""
    get = lambda key: str(values.get(labels[key], "")).strip()   # noqa: E731
    missing = [k.replace("_", " ").capitalize() for k in labels if not get(k)]
    errors = [f"Please fill in: {', '.join(missing)}."] if missing else []
    if get("email") and not _EMAIL_RE.match(get("email")):
        errors.append("Email address is not valid.")
    if get("topic") and get("topic").lower() not in TOPICS:
        errors.append("Please choose a topic from the list.")
    if get("contact_method") and get("contact_method").lower() not in CONTACT_METHODS:
        errors.append("Preferred contact must be Email or Phone.")
    return errors


# Form A field -> itself; Form B field -> its own input name.
FORM_A_REQUIRED = {k: k for k in FIELDS if k != "newsletter"}
FORM_B_REQUIRED = {"name": "ufullname", "email": "uemailid", "phone": "ucontactno",
                   "message": "umsg", "topic": "utopic", "contact_method": "ucontactpref"}


def current_mapping() -> dict:
    return load_mapping(app.config["CROSSFILL_MAPPING"])


def target_url() -> str:
    return current_mapping()["target_url"]


def _hostname(host: str) -> str:
    """'127.0.0.1:5000' -> '127.0.0.1', '[::1]:5000' -> '[::1]'."""
    if host.startswith("["):
        return host[: host.find("]") + 1]
    return host.rsplit(":", 1)[0]


@app.before_request
def only_loopback_hosts():
    if _hostname(request.host or "").lower() not in LOOPBACK_HOSTS:
        abort(403)


@app.after_request
def security_headers(resp):
    resp.headers.setdefault("X-Content-Type-Options", "nosniff")
    resp.headers.setdefault("X-Frame-Options", "DENY")
    resp.headers.setdefault("Referrer-Policy", "no-referrer")
    return resp


@app.get("/")
def form_a():
    """Form A: the source form you fill in."""
    record = RECORDS.get(request.args.get("record", ""), {})
    mapping = current_mapping()
    return render_template(
        "form_a.html",
        record=record or SAMPLE_RECORD,
        target_url=mapping["target_url"],
        fields=list(FIELDS),
        target_fields={k: v for k, v in mapping["fields"].items() if k in FIELDS},
        readonly=bool(record),
    )


@app.route("/form-b", methods=["GET", "POST"])
def form_b():
    """Form B: a local test target with the same fields as the sample mapping.
    Submissions stay in memory on this machine and are never forwarded."""
    submitted, errors = None, []
    if request.method == "POST":
        form = request.form.to_dict()
        errors = validate_contact(form, FORM_B_REQUIRED)
        if not errors:
            submitted = form
            with _lock:
                FORM_B_SUBMISSIONS.append(submitted)
    return render_template("form_b.html", submitted=submitted, errors=errors), (400 if errors else 200)


@app.post("/api/transfer")
def transfer():
    payload = request.get_json(silent=True) or {}
    data = {k: str(payload.get(k, "")).strip() for k in FIELDS}
    errors = validate_contact(data, FORM_A_REQUIRED)
    if errors:
        return jsonify(ok=False, error=" ".join(errors)), 400

    record_id = uuid.uuid4().hex
    with _lock:
        RECORDS[record_id] = data
    source_url = f"{request.host_url.rstrip('/')}/?record={record_id}"

    cmd = [sys.executable, "-m", "crossfill", "run", "--source-url", source_url,
           "--mapping", app.config["CROSSFILL_MAPPING"]]
    if os.environ.get("CROSSFILL_HEADLESS") != "1":
        cmd.append("--keep-open")   # leave the browser up so you can review Form B

    # The browser runs in its own process so it can stay open after we reply.
    env = {**os.environ, "PYTHONPATH": os.pathsep.join(
        filter(None, [str(PROJECT_ROOT), os.environ.get("PYTHONPATH")]))}
    proc = subprocess.Popen(cmd, cwd=PROJECT_ROOT, env=env, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, text=True, encoding="utf-8")
    log = []
    try:
        for line in proc.stdout:          # wait for the result line
            if line.startswith(RESULT_PREFIX):
                result = json.loads(line[len(RESULT_PREFIX):])
                break
            log.append(line.rstrip())
        else:
            proc.wait()
            result = {"ok": False,
                      "error": "CrossFill stopped without a result.\n" + "\n".join(log[-20:])}
    finally:
        with _lock:
            RECORDS.pop(record_id, None)

    # Drain remaining output in the background so the child never blocks.
    threading.Thread(target=lambda: [None for _ in proc.stdout], daemon=True).start()
    return jsonify(result), (200 if result.get("ok") else 500)


@app.get("/health")
def health():
    from .. import __version__
    return jsonify(ok=True, version=__version__)


@app.errorhandler(404)
def not_found(_):
    return jsonify(ok=False, error="Not found"), 404


@app.errorhandler(403)
def forbidden(_):
    return jsonify(ok=False, error="Forbidden: CrossFill only answers on localhost"), 403


def serve(port: int = 5000, mapping_path: str | None = None) -> None:
    """Start the demo app on 127.0.0.1 (never on a public interface)."""
    if mapping_path:
        app.config["CROSSFILL_MAPPING"] = str(Path(mapping_path).resolve())
    current_mapping()   # fail fast on a bad mapping
    print(f"CrossFill running at http://127.0.0.1:{port}  (Ctrl+C to stop)")
    app.run(host="127.0.0.1", port=port, debug=False, threaded=True)
