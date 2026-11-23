using System.CommandLine;
using System.CommandLine.Help;
using CrossFill.Web;
using Microsoft.Extensions.Logging;

namespace CrossFill.Cli;

/// <summary>
/// Command line interface.
/// </summary>
/// <remarks>
/// <code>
/// crossfill run --source-url URL [--mapping FILE] [--dry-run] ...
/// crossfill run --data record.json [--record N] [--mapping FILE] ...
/// crossfill validate FILE [FILE ...]
/// crossfill serve [--port 5000]
/// crossfill playwright install chromium
/// </code>
/// <c>crossfill --source-url URL</c> (no subcommand) is kept as an alias for
/// <c>crossfill run</c> so existing scripts keep working.
///
/// The target form is filled but never submitted.
///
/// <c>run</c> prints one line starting with <c>CROSSFILL_RESULT </c> followed by JSON as
/// soon as the form has been filled, so a caller can read it while the browser stays open
/// for review.
///
/// Exit codes: 0 success, 1 transfer incomplete or failed, 2 bad arguments or invalid
/// mapping/data file.
/// </remarks>
public static class Cli
{
    /// <summary>All fields filled and verified.</summary>
    public const int ExitOk = 0;

    /// <summary>The transfer is incomplete or failed.</summary>
    public const int ExitIncomplete = 1;

    /// <summary>Bad arguments, or an invalid mapping or data file.</summary>
    public const int ExitUsage = 2;

    private static readonly HashSet<string> Commands = ["run", "validate", "serve", "playwright"];

    /// <summary>Run the CLI with <paramref name="args"/>; returns the exit code.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;
        var argv = args.ToList();

        // `crossfill playwright ...` passes straight through to Playwright's own tool
        // (the .NET equivalent of Python's `playwright install chromium`).
        if (argv.Count > 0 && argv[0] == "playwright")
            return Microsoft.Playwright.Program.Main(argv.Skip(1).ToArray());

        // Backwards compatibility: `crossfill --source-url ...` means `crossfill run ...`.
        string? firstCommand = argv.FirstOrDefault(a => !a.StartsWith('-'));
        if (argv.Count > 0 && !Commands.Contains(firstCommand ?? "")
            && !argv.Any(a => a is "-h" or "--help" or "-?" or "--version"))
        {
            var verbose = argv.Where(a => a is "-v" or "--verbose").ToList();
            argv = [.. verbose, "run", .. argv.Where(a => a is not ("-v" or "--verbose"))];
        }

        var verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Log each step to stderr",
            Recursive = true,
        };
        var versionOption = new Option<bool>("--version") { Description = "Show program's version number and exit" };

        var root = new RootCommand(
            "Copy data from one web application into another's form. " +
            "CrossFill fills the form; you review it and submit it yourself.");
        foreach (var builtIn in root.Options.OfType<VersionOption>().ToList())
            root.Options.Remove(builtIn);
        root.Options.Add(versionOption);
        root.Options.Add(verboseOption);
        root.Subcommands.Add(BuildRun(stdout, stderr, verboseOption));
        root.Subcommands.Add(BuildValidate(stdout));
        root.Subcommands.Add(BuildServe(stdout, stderr));
        root.Subcommands.Add(new Command("playwright", "Run Playwright's own tool, e.g. `playwright install chromium`"));

        root.SetAction(parse =>
        {
            if (parse.GetValue(versionOption))
            {
                stdout.WriteLine($"crossfill {CrossFillInfo.Version}");
                return ExitOk;
            }
            PrintHelp(root, stdout);
            return ExitUsage;
        });

        var result = root.Parse(argv);
        if (result.Errors.Count > 0)
        {
            foreach (var error in result.Errors)
                stderr.WriteLine($"crossfill: error: {error.Message}");
            stderr.WriteLine("Try 'crossfill --help' for more information.");
            return ExitUsage;
        }
        return await result.InvokeAsync(new InvocationConfiguration { Output = stdout, Error = stderr });
    }

    private static void PrintHelp(Command command, TextWriter output) =>
        command.Parse("--help").Invoke(new InvocationConfiguration { Output = output });

    private static Command BuildRun(TextWriter stdout, TextWriter stderr, Option<bool> verboseOption)
    {
        var sourceUrl = new Option<string?>("--source-url") { Description = "URL of the source page (Form A)" };
        var data = new Option<string?>("--data")
        {
            Description = "Read the record from a .json or .csv file",
            HelpName = "FILE",
        };
        var record = new Option<int>("--record")
        {
            Description = "Which record in --data to use (0-based, default 0)",
            HelpName = "N",
            DefaultValueFactory = _ => 0,
        };
        var mapping = new Option<string>("--mapping")
        {
            Description = "Mapping JSON for the target form",
            DefaultValueFactory = _ => MappingLoader.DefaultMappingPath,
        };
        var targetUrl = new Option<string?>("--target-url") { Description = "Override the target URL in the mapping file" };
        var dryRun = new Option<bool>("--dry-run") { Description = "Match fields and report the plan without typing anything" };
        var headless = new Option<bool>("--headless") { Description = "Run without a visible browser window" };
        var keepOpen = new Option<bool>("--keep-open") { Description = "Keep the browser open until you close it" };

        var run = new Command("run", "Read source data and fill the target form")
        {
            sourceUrl, data, record, mapping, targetUrl, dryRun, headless, keepOpen,
        };
        run.Validators.Add(r =>
        {
            if (r.GetResult(sourceUrl) is { Implicit: false } && r.GetResult(data) is { Implicit: false })
                r.AddError("argument --data: not allowed with argument --source-url");
        });

        run.SetAction(async parse =>
        {
            Mapping map;
            OrderedDictionary<string, string>? record_ = null;
            try
            {
                map = MappingLoader.Load(parse.GetValue(mapping), parse.GetValue(targetUrl));
                if (parse.GetValue(data) is { } dataFile)
                    record_ = Sources.LoadRecord(dataFile, parse.GetValue(record));
            }
            catch (Exception e) when (e is MappingException or SourceException)
            {
                stderr.WriteLine($"crossfill: {e.Message}");
                return ExitUsage;
            }
            string? source = parse.GetValue(sourceUrl);
            if (record_ is null && string.IsNullOrEmpty(source) && string.IsNullOrEmpty(map.Source?.Url))
            {
                stderr.WriteLine("crossfill: give --source-url or --data (or set source.url in the mapping)");
                return ExitUsage;
            }

            bool isHeadless = parse.GetValue(headless) || Environment.GetEnvironmentVariable(CrossFillInfo.HeadlessEnv) == "1";
            bool keep = parse.GetValue(keepOpen);
            bool dry = parse.GetValue(dryRun);

            void Emit(TransferResult result)
            {
                stdout.WriteLine(CrossFillInfo.ResultPrefix + result.ToJson());
                stdout.Flush();
                stderr.WriteLine(result.Summary());
                if (keep && !isHeadless && !result.DryRun)
                    stderr.WriteLine("Review the form in the browser and submit it yourself. Close the window when done.");
                stderr.Flush();
            }

            var logger = new StderrLogger("crossfill", stderr, parse.GetValue(verboseOption) ? LogLevel.Information : LogLevel.Warning);
            try
            {
                var result = await Engine.RunTransferAsync(
                    source, map, data: record_, headless: isHeadless, keepOpen: keep, dryRun: dry,
                    onFilled: Emit, logger: logger);
                return result.Ok ? ExitOk : ExitIncomplete;
            }
            catch (Exception e)
            {
                stderr.WriteLine($"crossfill: {e.Message}");
                return ExitIncomplete;
            }
        });
        return run;
    }

    private static Command BuildValidate(TextWriter stdout)
    {
        var files = new Argument<string[]>("FILE") { Arity = ArgumentArity.OneOrMore };
        var validate = new Command("validate", "Check one or more mapping files") { files };
        validate.SetAction(parse =>
        {
            int status = ExitOk;
            foreach (string path in parse.GetValue(files) ?? [])
            {
                try
                {
                    var m = MappingLoader.Load(path);
                    stdout.WriteLine($"OK    {path}  ({m.Fields.Count} explicit field(s), target {m.TargetUrl})");
                }
                catch (MappingException e)
                {
                    stdout.WriteLine($"FAIL  {e.Message}");
                    status = ExitUsage;
                }
            }
            return status;
        });
        return validate;
    }

    private static Command BuildServe(TextWriter stdout, TextWriter stderr)
    {
        var port = new Option<int>("--port")
        {
            Description = "Port on 127.0.0.1 (default: $PORT or 5000)",
            DefaultValueFactory = _ => int.TryParse(Environment.GetEnvironmentVariable("PORT"), out int p) ? p : 5000,
        };
        var mapping = new Option<string?>("--mapping") { Description = "Mapping JSON for the target form" };
        var serve = new Command("serve", "Start the local demo web app") { port, mapping };
        serve.SetAction(async (parse, cancel) =>
        {
            try
            {
                await CrossFillWebApp.ServeAsync(parse.GetValue(port), parse.GetValue(mapping), stdout);
            }
            catch (MappingException e)
            {
                stderr.WriteLine($"crossfill: {e.Message}");
                return ExitUsage;
            }
            return ExitOk;
        });
        return serve;
    }
}

/// <summary>Writes <c>LEVEL name: message</c> lines, like Python's logging setup in the Python CLI.</summary>
internal sealed class StderrLogger(string name, TextWriter output, LogLevel minimum) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= minimum;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        string level = logLevel switch
        {
            LogLevel.Trace or LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARNING",
            LogLevel.Error => "ERROR",
            _ => "CRITICAL",
        };
        lock (output)
        {
            output.WriteLine($"{level} {name}: {formatter(state, exception)}");
            output.Flush();
        }
    }
}
