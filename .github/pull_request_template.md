## What does this change?

<!-- One or two sentences. Link the issue it closes: "Closes #123". -->

## Checklist

- [ ] Tests added or updated in **both** implementations (`pytest` and `dotnet test`; browser tests use only the local test Form B)
- [ ] Behaviour changes have a case in `spec/conformance/cases.json`
- [ ] `ruff check .` (in `python/`) and `dotnet build` (warnings are errors) pass
- [ ] Docs / CHANGELOG updated if behaviour changed
- [ ] CrossFill still never submits a form on its own
