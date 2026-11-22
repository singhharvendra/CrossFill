"""``python -m crossfill`` - see :mod:`crossfill.cli`."""

import sys

from .cli import RESULT_PREFIX, main

__all__ = ["RESULT_PREFIX", "main"]

if __name__ == "__main__":
    sys.exit(main())
