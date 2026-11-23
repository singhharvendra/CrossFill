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

CrossFill has two implementations, in `python/` and `dotnet/`, that must behave
the same way. Files both of them use (the mapping schema, the sample mapping
and the conformance cases) live in `spec/`. See
[docs/architecture.md](docs/architecture.md#keeping-the-two-implementations-identical).

### Python

```bash
git clone https://github.com/singhharvendra/CrossFill.git
cd CrossFill/python
python -m venv .venv && source .venv/bin/activate   # Windows: .venv\Scripts\activate
pip install -e ".[dev]"
playwright install chromium
pre-commit install          # optional: lint, validate mappings and sync spec/ on every commit
```

### .NET

Install the .NET 11 SDK.

```bash
cd CrossFill/dotnet
dotnet build
dotnet run --project src/CrossFill.Cli -- playwright install chromium
```

## Checks to run before opening a PR

```bash
# Python (in python/)
ruff check .                      # lint
pytest -m "not browser"           # fast unit and conformance tests (~2 s)
pytest                            # everything, including headless browser tests
crossfill validate ../examples/mappings/*.json

# .NET (in dotnet/)
dotnet build                               # warnings are errors
dotnet test --filter "Category!=browser"   # fast unit and conformance tests
dotnet test                                # everything, including headless browser tests
```

CI runs the same commands on Linux, Windows and macOS, with Python 3.9 to 3.13
and with .NET 11.

## Making a change

1. Open an issue first for anything larger than a small fix, so we can agree on the approach.
2. Create a branch, keep the change focused, and add or update tests.
3. **Change both implementations.** A behaviour change goes into `python/` and
   `dotnet/` together, with a case in `spec/conformance/cases.json` that both
   test suites run. If you only know one language, open the PR anyway and say
   so; a maintainer or another contributor can port it.
4. If you change anything in `spec/`, run `python python/sync_spec.py` so the
   Python package's copies match.
5. Add a line under **Unreleased** in [CHANGELOG.md](CHANGELOG.md).
6. Open a pull request and fill in the template.

### Adding matcher synonyms

Built-in synonyms live in `python/crossfill/matcher.py` (`SYNONYMS`) and
`dotnet/src/CrossFill/Matcher.cs` (`BuiltInSynonyms`); keep the two lists
identical. Put the most specific words first, add a `score` case to
`spec/conformance/cases.json` showing the new match, and check that it doesn't
steal another field's match.

### Contributing an example mapping

Put it in `examples/mappings/`, start it with the `$schema` line, use a
placeholder URL unless the site publicly allows automation, and make sure
`crossfill validate` passes.

## Releasing (maintainers)

Both implementations share one version number, but are released separately.

1. Bump the version in `python/crossfill/__init__.py` and
   `dotnet/Directory.Build.props` (a .NET test checks they match), and move the
   CHANGELOG entries under a new version heading.
2. Python: tag `py-vX.Y.Z` and push it. The `Release (Python)` workflow builds,
   publishes to PyPI through trusted publishing, and creates the GitHub release.
3. .NET: tag `dotnet-vX.Y.Z` and push it. The `Release (.NET)` workflow packs
   `CrossFill` and `CrossFill.Cli`, pushes them to NuGet (secret
   `NUGET_API_KEY`), and creates the GitHub release.
