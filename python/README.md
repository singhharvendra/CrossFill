# CrossFill for Python

**Copy data from one web application into another's form, with automatic field matching. A human always presses Submit.**

CrossFill reads a record (from a web page, JSON or CSV), opens the target
form, finds the matching fields, fills them, reads every value back to verify
it, and stops. You review the form and press Submit yourself.

This is the Python implementation. A [.NET implementation](https://github.com/singhharvendra/CrossFill/tree/main/dotnet)
with the same behaviour lives in the same repository.

## Install

Requires Python 3.9+.

```bash
pip install "crossfill[web]"       # or: pip install crossfill  (CLI and library only)
playwright install chromium        # optional: falls back to installed Chrome or Edge
crossfill serve                    # demo: open http://127.0.0.1:5000
```

## Use

```bash
crossfill run --data contacts.csv --record 1 --mapping my-site.json --keep-open
crossfill run --source-url "https://crm.example/record/42" --mapping my-site.json --dry-run
crossfill validate my-site.json
```

```python
from crossfill import load_mapping, load_records, run_transfer

mapping = load_mapping("my-site.json")
for record in load_records("contacts.csv"):
    result = run_transfer(mapping=mapping, data=record, keep_open=True)
    print(result.summary())
```

## Develop

From this folder:

```bash
pip install -e ".[dev]" && playwright install chromium
ruff check .
pytest -m "not browser"    # fast
pytest                     # everything, including the shared conformance cases
```

The mapping schema and the sample mapping are copies of the files in the
repository's `spec/` folder. After changing `spec/`, run `python sync_spec.py`.

Full documentation: <https://github.com/singhharvendra/CrossFill>

License: [MIT-0](https://github.com/singhharvendra/CrossFill/blob/main/LICENSE). No permission or credit needed.
