# CrossFill for .NET

**Copy data from one web application into another's form, with automatic field matching. A human always presses Submit.**

CrossFill reads a record (from a web page, JSON or CSV), opens the target
form, finds the matching fields, fills them, reads every value back to verify
it, and stops. You review the form and press Submit yourself.

This is the .NET implementation, for .NET 11 and .NET 10 (LTS). It behaves
the same as the [Python implementation](https://github.com/singhharvendra/CrossFill/tree/main/python):
same mapping files, same command-line options, same results.

| Package | What it is |
|---------|------------|
| `CrossFill.Cli` | The `crossfill` command-line tool, including the local demo web app |
| `CrossFill.Core` | The library: `MappingLoader`, `Sources`, `Matcher`, `Engine` |

## Install

```bash
dotnet tool install --global CrossFill.Cli
crossfill playwright install chromium     # optional: falls back to installed Chrome or Edge
crossfill serve                           # demo: open http://127.0.0.1:5000
```

## Use

```bash
crossfill run --data contacts.csv --record 1 --mapping my-site.json --keep-open
crossfill run --source-url "https://crm.example/record/42" --mapping my-site.json --dry-run
crossfill validate my-site.json
```

```csharp
using CrossFill;

var mapping = MappingLoader.Load("my-site.json");
foreach (var record in Sources.LoadRecords("contacts.csv"))
{
    var result = await Engine.RunTransferAsync(mapping: mapping, data: record, keepOpen: true);
    Console.WriteLine(result.Summary());
}
```

## Develop

From this folder:

```bash
dotnet build                                        # warnings are errors
dotnet run --project src/CrossFill.Cli -- playwright install chromium
dotnet test --filter "Category!=browser"            # fast
dotnet test                                         # everything, on .NET 11 and .NET 10
```

`dotnet test` runs each target framework that has a runtime installed. The
shared `spec/` files (mapping schema, sample mapping, conformance cases) are
linked into the build directly.

Full documentation: <https://github.com/singhharvendra/CrossFill>

License: [MIT-0](https://github.com/singhharvendra/CrossFill/blob/main/LICENSE). No permission or credit needed.
