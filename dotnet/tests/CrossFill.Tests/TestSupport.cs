using System.Net;
using System.Net.Sockets;
using CrossFill.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

// Tests change environment variables (CROSSFILL_TARGET_URL, CROSSFILL_HEADLESS), so run them one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CrossFill.Tests;

/// <summary>Paths into the repository and shared sample data.</summary>
internal static class Repo
{
    /// <summary>The repository root (the folder that holds spec/).</summary>
    public static string Root { get; } = FindRoot();

    public static string Spec(params string[] parts) => Path.Combine([Root, "spec", .. parts]);

    public static string Examples(params string[] parts) => Path.Combine([Root, "examples", .. parts]);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "spec", "conformance")))
                return dir.FullName;
        throw new InvalidOperationException("Could not find the repository root (spec/conformance).");
    }
}

internal static class Samples
{
    /// <summary>Text fields only: what a mapping without a "source" section reads from Form A.</summary>
    public static OrderedDictionary<string, string> Text => new()
    {
        ["name"] = "Jane Doe",
        ["email"] = "jane.doe@example.com",
        ["phone"] = "1234567890",
        ["message"] = "Please share more details.",
    };

    /// <summary>Every Form A field: text, textarea, dropdown, radio group and checkbox.</summary>
    public static OrderedDictionary<string, string> All
    {
        get
        {
            var all = Text;
            all["topic"] = "support";
            all["contact_method"] = "phone";
            all["newsletter"] = "true";
            return all;
        }
    }
}

/// <summary>Sets (or clears, with null) an environment variable until disposed.</summary>
internal sealed class EnvScope : IDisposable
{
    private readonly string _name;
    private readonly string? _old;

    public EnvScope(string name, string? value)
    {
        _name = name;
        _old = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _old);

    /// <summary>No target override, so mapping files are read as written.</summary>
    public static EnvScope Clean() => new(CrossFillInfo.TargetUrlEnv, null);
}

/// <summary>A temporary folder, deleted when disposed.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("crossfill-test-").FullName;

    public string Write(string name, string content)
    {
        string file = System.IO.Path.Combine(Path, name);
        File.WriteAllText(file, content);
        return file;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// The real CrossFill web app on a free port, headless, pointed at its own local test
/// Form B, so nothing is ever sent to an external site.
/// </summary>
public sealed class WebAppFixture : IAsyncLifetime
{
    private WebApplication? _app;
    private EnvScope? _target, _headless;

    public string BaseUrl { get; private set; } = "";

    public string FormBUrl => $"{BaseUrl}/form-b";

    public WebState State => _app!.Services.GetRequiredService<WebState>();

    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromMinutes(2) };

    public async Task InitializeAsync()
    {
        int port = FreePort();
        BaseUrl = $"http://127.0.0.1:{port}";
        _target = new EnvScope(CrossFillInfo.TargetUrlEnv, FormBUrl);
        _headless = new EnvScope(CrossFillInfo.HeadlessEnv, "1");
        _app = CrossFillWebApp.Build(port);
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _target?.Dispose();
        _headless?.Dispose();
        Http.Dispose();
    }

    /// <summary>Re-apply the test environment (a unit test may have cleared it).</summary>
    public void UseTestEnvironment()
    {
        Environment.SetEnvironmentVariable(CrossFillInfo.TargetUrlEnv, FormBUrl);
        Environment.SetEnvironmentVariable(CrossFillInfo.HeadlessEnv, "1");
    }

    public List<OrderedDictionary<string, string>> Submissions()
    {
        lock (State.FormBSubmissions) return [.. State.FormBSubmissions];
    }

    public void ClearSubmissions()
    {
        lock (State.FormBSubmissions) State.FormBSubmissions.Clear();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

[CollectionDefinition("web")]
public sealed class WebCollection : ICollectionFixture<WebAppFixture>;
