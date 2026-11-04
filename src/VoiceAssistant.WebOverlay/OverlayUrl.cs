using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace VoiceAssistant.WebOverlay;

/// <summary>Where the hosted page address was configured.</summary>
internal enum OverlayUrlSource { None, Argument, Environment, SettingsFile, BuildDefault }

/// <summary>The resolved page address, or why there is none. <see cref="Error"/> is set only for a value that is set but unusable.</summary>
internal sealed record OverlayUrlResult(Uri? Url, OverlayUrlSource Source, string? Error = null);

/// <summary>
/// Finds the live.html address to host. The first setting present wins: <c>--url</c>, then the <c>LIVECOACH_URL</c>
/// environment variable, then <c>LiveCoach.settings.json</c> next to the executable, then the build-time default
/// (<c>LiveCoachDefaultUrl</c>). A setting that is present but invalid is reported, never skipped, so a typo cannot
/// silently open a different page.
/// </summary>
internal static class OverlayUrl
{
    public const string Argument = "--url";
    public const string EnvironmentVariable = "LIVECOACH_URL";
    public const string SettingsFileName = "LiveCoach.settings.json";
    public const string BuildDefaultKey = "LiveCoachDefaultUrl";

    public static OverlayUrlResult Resolve(IReadOnlyList<string> args, Func<string, string?> environment, Func<string?> readSettingsFile,
        string? buildDefault)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] != Argument) continue;
            return i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? Check(args[i + 1], OverlayUrlSource.Argument)
                : new(null, OverlayUrlSource.Argument, "--url needs an address.");
        }
        if (environment(EnvironmentVariable) is { } fromEnvironment && !string.IsNullOrWhiteSpace(fromEnvironment))
            return Check(fromEnvironment, OverlayUrlSource.Environment);
        string? settings;
        try { settings = readSettingsFile(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(null, OverlayUrlSource.SettingsFile, $"{SettingsFileName} could not be read.");
        }
        if (settings is not null) return FromSettings(settings);
        return string.IsNullOrWhiteSpace(buildDefault) ? new(null, OverlayUrlSource.None) : Check(buildDefault, OverlayUrlSource.BuildDefault);
    }

    /// <summary>Resolves from this process: its arguments, environment, the settings file beside the executable and the build default.</summary>
    public static OverlayUrlResult ResolveForProcess() => Resolve(
        Environment.GetCommandLineArgs(),
        Environment.GetEnvironmentVariable,
        () =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        },
        BuildDefault(typeof(OverlayUrl).Assembly));

    /// <summary>The <c>LiveCoachDefaultUrl</c> assembly metadata that an optional MSBuild property embeds at build time.</summary>
    public static string? BuildDefault(Assembly assembly) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == BuildDefaultKey)?.Value;

    /// <summary>Only HTTPS pages, or plain HTTP on loopback for local development, may be hosted.</summary>
    public static bool TryValidate(string? value, [NotNullWhen(true)] out Uri? url)
    {
        url = Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && string.IsNullOrEmpty(uri.UserInfo) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) ? uri : null;
        return url is not null;
    }

    /// <summary>The text the window shows when there is no usable address: what went wrong, and the three ways to set it.</summary>
    public static string SetupMessage(OverlayUrlResult result) =>
        (result.Error is null
            ? "Live Coach 페이지 주소가 설정되지 않았습니다."
            : $"Live Coach 페이지 주소를 쓸 수 없습니다.\n{result.Error}") +
        "\n\n다음 중 하나로 live.html 주소(HTTPS)를 지정한 뒤 다시 실행하세요.\n" +
        $"• LiveCoach.exe {Argument} https://<앱 주소>/live.html\n" +
        $"• 환경 변수 {EnvironmentVariable}\n" +
        $"• LiveCoach.exe 옆의 {SettingsFileName}: {{\"url\": \"https://<앱 주소>/live.html\"}}";

    private static OverlayUrlResult FromSettings(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, "url", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                        return Check(property.Value.GetString(), OverlayUrlSource.SettingsFile);
                }
            }
            return new(null, OverlayUrlSource.SettingsFile, $"{SettingsFileName} needs a \"url\" text value.");
        }
        catch (JsonException)
        {
            return new(null, OverlayUrlSource.SettingsFile, $"{SettingsFileName} is not valid JSON.");
        }
    }

    private static OverlayUrlResult Check(string? value, OverlayUrlSource source) =>
        TryValidate(value, out var url)
            ? new(url, source)
            : new(null, source, $"{Describe(source)} must be an HTTPS address, or HTTP on localhost for development.");

    private static string Describe(OverlayUrlSource source) => source switch
    {
        OverlayUrlSource.Argument => Argument,
        OverlayUrlSource.Environment => EnvironmentVariable,
        OverlayUrlSource.SettingsFile => SettingsFileName,
        _ => "The built-in address",
    };
}
