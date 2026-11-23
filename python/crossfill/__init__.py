"""CrossFill - copy data from one web application into another's form,
with automatic field matching and a human always pressing Submit.

Quick use from Python::

    from crossfill import load_mapping, run_transfer

    result = run_transfer(mapping=load_mapping("my-site.json"),
                          data={"name": "Ada", "email": "ada@example.com"},
                          headless=True)
    print(result.summary())
"""

from .mapping import MappingError, load_mapping, validate_mapping
from .sources import SourceError, load_record, load_records

__version__ = "0.2.0"

__all__ = [
    "FillError", "MappingError", "SourceError", "TransferResult",
    "load_mapping", "load_record", "load_records", "run_transfer", "validate_mapping",
    "__version__",
]


def __getattr__(name):
    # The engine imports Playwright; load it only when it's actually used so
    # `crossfill validate` and the mapping/source helpers stay lightweight.
    if name in {"run_transfer", "TransferResult", "FillError"}:
        from . import engine
        return getattr(engine, name)
    raise AttributeError(f"module 'crossfill' has no attribute {name!r}")
