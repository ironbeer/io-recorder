using System.Reflection;

namespace IoRecorder.Core;

// The version shown on screen, in the CLI and in environment.json: "0.2.1 (8d005b1)", the version from Directory.Build.props
// and, when the build knows it, the commit it was built from (so a local build can be told apart from a release).
public static class ToolVersion
{
    public static string Text { get; } = Describe(typeof(ToolVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
    public static string Describe(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return "?";
        int plus = informational.IndexOf('+');
        if (plus < 0) return informational;
        string commit = informational[(plus + 1)..];
        return $"{informational[..plus]} ({commit[..Math.Min(7, commit.Length)]})";
    }
}
