# Contributing to CrossFill

Thanks for helping! Bug reports, mappings for new kinds of forms, docs fixes
and code are all welcome.

## Ground rules

* **CrossFill never submits a form.** Pull requests that add automatic
  submission, CAPTCHA solving, bot-detection evasion or bulk sending will not
  be accepted. See [Responsible use](README.md#responsible-use).
* **No real personal data** in issues, tests or examples. Use `example.com`
  addresses and obviously fake names.
* Tests must only talk to the local test Form B (`/form-b`), never to an
  external site.
* Be kind. This project follows the [Code of Conduct](CODE_OF_CONDUCT.md).

## Development setup

```bash
git clone https://github.com/singhharvendra/CrossFill.git
cd CrossFill
python -m venv .venv && source .venv/bin/activate   # Windows: .venv\Scripts\activate
pip install -e ".[dev]"
playwright install chromium
pre-commit install          # optional: lint and validate mappings on every commit
```

## Checks to run before opening a PR

```bash
ruff check .                      # lint
pytest -m "not browser"           # fast unit tests (~1 s)
pytest                            # everything, including headless browser tests
crossfill validate examples/mappings/*.json
```

CI runs the same commands on Linux, Windows and macOS with Python 3.9 to 3.13.

## Making a change

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Create a branch, keep the change focused, and add or update tests.
3. Add a line under **Unreleased** in [CHANGELOG.md](CHANGELOG.md).
4. Open a pull request and fill in the template.

### Adding matcher synonyms

Built-in synonyms live in `crossfill/matcher.py` (`SYNONYMS`). Put the most
specific words first, add a unit test in `tests/test_unit.py` showing the new
match, and check that it doesn't steal another field's match.

### Contributing an example mapping

Put it in `examples/mappings/`, start it with the `$schema` line, use a
placeholder URL unless the site publicly allows automation, and make sure
`crossfill validate` passes.

## Releasing (maintainers)

1. Bump `__version__` in `crossfill/__init__.py` and move the CHANGELOG entries
   under a new version heading.
2. Tag `vX.Y.Z` and push the tag. The `Release` workflow builds, publishes to
   PyPI through trusted publishing, and creates the GitHub release.
