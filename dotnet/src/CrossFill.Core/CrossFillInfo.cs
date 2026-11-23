using System.Reflection;

namespace CrossFill;

/// <summary>Version and well-known names shared by the library, the CLI and the web app.</summary>
public static class CrossFillInfo
{
    /// <summary>The CrossFill version, for example <c>0.2.0</c>.</summary>
    public static string Version { get; } =
        typeof(CrossFillInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>Prefix of the one machine-readable line <c>crossfill run</c> prints.</summary>
    public const string ResultPrefix = "CROSSFILL_RESULT ";

    /// <summary>Overrides the mapping's <c>target_url</c>.</summary>
    public const string TargetUrlEnv = "CROSSFILL_TARGET_URL";

    /// <summary>Set to <c>1</c> to run without a visible browser window.</summary>
    public const string HeadlessEnv = "CROSSFILL_HEADLESS";
}

/// <summary>Raised when a mapping file is missing, unreadable or invalid.</summary>
public sealed class MappingException(string message) : Exception(message);

/// <summary>Raised when source data cannot be read.</summary>
public sealed class SourceException(string message) : Exception(message);

/// <summary>A value could not be put into a field (e.g. no such option in a select).</summary>
public sealed class FillException(string message) : Exception(message);
