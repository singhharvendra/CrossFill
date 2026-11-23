"""Shared fixtures. The browser tests run against CrossFill's own local test
Form B (/form-b) on a random free port, so nothing is ever sent to an
external site."""

from __future__ import annotations

import os
import socket
import sys
import threading
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

# Text fields only: what a mapping without a "source" section reads from Form A.
TEXT_SAMPLE = {
    "name": "Jane Doe",
    "email": "jane.doe@example.com",
    "phone": "1234567890",
    "message": "Please share more details.",
}
# Every Form A field: text, textarea, dropdown, radio group and checkbox.
SAMPLE = {
    **TEXT_SAMPLE,
    "topic": "support",
    "contact_method": "phone",
    "newsletter": "true",
}


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class ServerThread(threading.Thread):
    def __init__(self, flask_app, port):
        from werkzeug.serving import make_server

        super().__init__(daemon=True)
        self.srv = make_server("127.0.0.1", port, flask_app, threaded=True)

    def run(self):
        self.srv.serve_forever()

    def stop(self):
        self.srv.shutdown()


@pytest.fixture(scope="session")
def crossfill_app():
    """The real CrossFill web app on a free port, headless, pointed at its
    own local test Form B. Yields (base_url, module)."""
    port = free_port()
    base = f"http://127.0.0.1:{port}"
    os.environ["CROSSFILL_TARGET_URL"] = f"{base}/form-b"
    os.environ["CROSSFILL_HEADLESS"] = "1"
    from crossfill.web import app as module

    t = ServerThread(module.app, port)
    t.start()
    yield base, module
    t.stop()
    os.environ.pop("CROSSFILL_TARGET_URL", None)


@pytest.fixture
def form_b(crossfill_app):
    """(target_url, submissions list) for the local test Form B, emptied per test."""
    base, module = crossfill_app
    module.FORM_B_SUBMISSIONS.clear()
    return f"{base}/form-b", module.FORM_B_SUBMISSIONS


@pytest.fixture
def source_url(crossfill_app):
    """URL of a read-only Form A page pre-filled with SAMPLE."""
    base, module = crossfill_app
    module.RECORDS["test"] = dict(SAMPLE)
    yield f"{base}/?record=test"
    module.RECORDS.pop("test", None)


@pytest.fixture
def clean_env(monkeypatch):
    """No target override, so mapping files are read as written."""
    monkeypatch.delenv("CROSSFILL_TARGET_URL", raising=False)
