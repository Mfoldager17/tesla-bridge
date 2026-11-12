using System.Reflection;

namespace TeslaBridge.Versioning;

/// <summary>
/// Which version of the bridge this is. The number comes from the project file (bumped by release-please on
/// every release), the commit and channel from the image build (Dockerfile build args):
///   stable → "1.2.0"            a release (image tags latest, v1.2.0, v1.2, v1)
///   edge   → "1.2.0-edge (abc1234)"  a build of main after 1.2.0 (image tag edge)
///   dev    → "1.2.0-dev"        dotnet run on your own machine
/// </summary>
public static class BuildInfo
{
    private static readonly string Informational =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>"1.2.0"</summary>
    public static string Version { get; } = Informational.Split('+')[0];

    /// <summary>Short commit hash, when the build knew it.</summary>
    public static string? Commit { get; } =
        Informational.Split('+') is [_, var sha, ..] && sha.Length >= 7 ? sha[..7] : null;

    /// <summary>"stable", "edge" or "dev".</summary>
    public static string Channel { get; } = Environment.GetEnvironmentVariable("BRIDGE_CHANNEL") is { Length: > 0 } c ? c : "dev";

    public static string Display { get; } =
        Channel == "stable" ? Version : $"{Version}-{Channel}" + (Commit is null ? "" : $" ({Commit})");

    /// <summary>Compares "1.2.0" with "v1.10.0" and the like; unparsable counts as 0.0.0.</summary>
    public static System.Version Parse(string? v) =>
        System.Version.TryParse((v ?? "").TrimStart('v', 'V').Split('-', '+')[0], out var p) ? p : new System.Version(0, 0, 0);
}
