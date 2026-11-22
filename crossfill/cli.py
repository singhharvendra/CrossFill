"""Command line interface.

    crossfill run --source-url URL [--mapping FILE] [--dry-run] ...
    crossfill run --data record.json [--record N] [--mapping FILE] ...
    crossfill validate FILE [FILE ...]
    crossfill serve [--port 5000]

``crossfill --source-url URL`` (no subcommand) is kept as an alias for
``crossfill run`` so existing scripts keep working.

The target form is filled but never submitted.

``run`` prints one line starting with ``CROSSFILL_RESULT `` followed by JSON as
soon as the form has been filled, so the web app (or any other caller) can
read it while the browser stays open for review.

Exit codes: 0 success, 1 transfer incomplete or failed, 2 bad arguments or
invalid mapping/data file.
"""

from __future__ import annotations

import argparse
import logging
import os
import sys

from . import __version__
from .mapping import DEFAULT_MAPPING, MappingError, load_mapping
from .sources import SourceError, load_record

RESULT_PREFIX = "CROSSFILL_RESULT "
EXIT_OK, EXIT_INCOMPLETE, EXIT_USAGE = 0, 1, 2
_COMMANDS = {"run", "validate", "serve"}


def _cmd_run(args) -> int:
    try:
        mapping = load_mapping(args.mapping, target_url=args.target_url)
        data = load_record(args.data, args.record) if args.data else None
    except (MappingError, SourceError) as exc:
        print(f"crossfill: {exc}", file=sys.stderr)
        return EXIT_USAGE
    if data is None and not (args.source_url or (mapping.get("source") or {}).get("url")):
        print("crossfill: give --source-url or --data (or set source.url in the mapping)",
              file=sys.stderr)
        return EXIT_USAGE

    from .engine import run_transfer  # imported late so `validate` works without Playwright

    headless = args.headless or os.environ.get("CROSSFILL_HEADLESS") == "1"

    def emit(result):
        print(RESULT_PREFIX + result.to_json(), flush=True)
        print(result.summary(), file=sys.stderr, flush=True)
        if args.keep_open and not headless and not result.dry_run:
            print("Review the form in the browser and submit it yourself. "
                  "Close the window when done.", file=sys.stderr, flush=True)

    result = run_transfer(
        args.source_url,
        mapping,
        data=data,
        headless=headless,
        keep_open=args.keep_open,
        dry_run=args.dry_run,
        on_filled=emit,
    )
    return EXIT_OK if result.ok else EXIT_INCOMPLETE


def _cmd_validate(args) -> int:
    status = EXIT_OK
    for path in args.files:
        try:
            m = load_mapping(path)
        except MappingError as exc:
            print(f"FAIL  {exc}")
            status = EXIT_USAGE
        else:
            print(f"OK    {path}  ({len(m['fields'])} explicit field(s), target {m['target_url']})")
    return status


def _cmd_serve(args) -> int:
    try:
        from .web.app import serve
    except ImportError as exc:  # Flask is an optional extra
        print(f"crossfill: the web app needs Flask: pip install 'crossfill[web]' ({exc})",
              file=sys.stderr)
        return EXIT_USAGE
    serve(port=args.port, mapping_path=args.mapping)
    return EXIT_OK


def build_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(
        prog="crossfill",
        description="Copy data from one web application into another's form. "
                    "CrossFill fills the form; you review it and submit it yourself.",
    )
    ap.add_argument("--version", action="version", version=f"%(prog)s {__version__}")
    ap.add_argument("-v", "--verbose", action="store_true", help="Log each step to stderr")
    sub = ap.add_subparsers(dest="command", metavar="COMMAND")

    run = sub.add_parser("run", help="Read source data and fill the target form")
    src = run.add_mutually_exclusive_group()
    src.add_argument("--source-url", help="URL of the source page (Form A)")
    src.add_argument("--data", metavar="FILE", help="Read the record from a .json or .csv file")
    run.add_argument("--record", type=int, default=0, metavar="N",
                     help="Which record in --data to use (0-based, default 0)")
    run.add_argument("--mapping", default=str(DEFAULT_MAPPING), help="Mapping JSON for the target form")
    run.add_argument("--target-url", help="Override the target URL in the mapping file")
    run.add_argument("--dry-run", action="store_true",
                     help="Match fields and report the plan without typing anything")
    run.add_argument("--headless", action="store_true", help="Run without a visible browser window")
    run.add_argument("--keep-open", action="store_true", help="Keep the browser open until you close it")
    run.set_defaults(func=_cmd_run)

    val = sub.add_parser("validate", help="Check one or more mapping files")
    val.add_argument("files", nargs="+", metavar="FILE")
    val.set_defaults(func=_cmd_validate)

    srv = sub.add_parser("serve", help="Start the local demo web app (needs crossfill[web])")
    srv.add_argument("--port", type=int, default=int(os.environ.get("PORT", "5000")))
    srv.add_argument("--mapping", default=None, help="Mapping JSON for the target form")
    srv.set_defaults(func=_cmd_serve)
    return ap


def main(argv=None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    # Backwards compatibility: `crossfill --source-url ...` means `crossfill run ...`.
    first_cmd = next((a for a in argv if not a.startswith("-")), None)
    if argv and first_cmd not in _COMMANDS and not {"-h", "--help", "--version"} & set(argv):
        verbose = [a for a in argv if a in ("-v", "--verbose")]
        argv = verbose + ["run"] + [a for a in argv if a not in ("-v", "--verbose")]

    parser = build_parser()
    args = parser.parse_args(argv)
    logging.basicConfig(
        level=logging.INFO if args.verbose else logging.WARNING,
        format="%(levelname)s %(name)s: %(message)s",
        stream=sys.stderr,
    )
    if not getattr(args, "func", None):
        parser.print_help()
        return EXIT_USAGE
    return args.func(args)
