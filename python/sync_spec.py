"""Copy the shared spec files into the Python package.

The canonical copies live in ``spec/`` at the repository root and are shared
with the .NET implementation. Python packaging cannot include files from
outside this folder, so the package keeps its own copies. Run this after
changing anything in ``spec/``; ``tests/test_conformance.py`` fails if the
copies drift.

    python sync_spec.py
"""

from __future__ import annotations

import shutil
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO_ROOT = HERE.parent

# (path inside python/, path inside the repository)
BUNDLED = [
    (Path("crossfill/mappings/form_b.json"), Path("spec/mappings/form_b.json")),
    (Path("crossfill/schema/mapping.schema.json"), Path("spec/schema/mapping.schema.json")),
    (Path("LICENSE"), Path("LICENSE")),
]


def main() -> None:
    for bundled, shared in BUNDLED:
        shutil.copyfile(REPO_ROOT / shared, HERE / bundled)
        print(f"copied {shared} -> python/{bundled.as_posix()}")


if __name__ == "__main__":
    main()
