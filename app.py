"""Convenience launcher: ``python app.py`` == ``crossfill serve``.

The web app lives in :mod:`crossfill.web.app`.
"""

import os

from crossfill.web.app import FORM_B_SUBMISSIONS, RECORDS, app, serve  # noqa: F401

if __name__ == "__main__":
    serve(port=int(os.environ.get("PORT", "5000")))
