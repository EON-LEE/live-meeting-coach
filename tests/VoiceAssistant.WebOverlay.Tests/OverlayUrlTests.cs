using System.Reflection;

[assembly: AssemblyMetadata(VoiceAssistant.WebOverlay.OverlayUrl.BuildDefaultKey, "https://built.example.com/live.html")]

namespace VoiceAssistant.WebOverlay.Tests;

public sealed class OverlayUrlTests
{
    private const string Argument = "https://argument.example.com/live.html";
    private const string FromEnvironment = "https://environment.example.com/live.html";
    private const string Settings = """{"url":"https://settings.example.com/live.html"}""";
    private const string Built = "https://built.example.com/live.html";

    private static OverlayUrlResult Resolve(string[]? args = null, string? environment = null, string? settings = null,
        string? built = null) =>
        OverlayUrl.Resolve(["LiveCoach.exe", .. args ?? []], name => name == OverlayUrl.EnvironmentVariable ? environment : null,
            () => settings, built);

    [Fact]
    public void TheArgumentWinsOverEveryOtherSetting()
    {
        var result = Resolve(["--fake-mic", "--url", Argument], FromEnvironment, Settings, Built);
        Assert.Equal(new OverlayUrlResult(new Uri(Argument), OverlayUrlSource.Argument), result);
    }

    [Fact]
    public void TheEnvironmentVariableWinsOverTheSettingsFileAndTheBuildDefault()
    {
        var result = Resolve(environment: FromEnvironment, settings: Settings, built: Built);
        Assert.Equal(new OverlayUrlResult(new Uri(FromEnvironment), OverlayUrlSource.Environment), result);
    }

    [Fact]
    public void TheSettingsFileWinsOverTheBuildDefault()
    {
        var result = Resolve(environment: " ", settings: Settings, built: Built);
        Assert.Equal(new OverlayUrlResult(new Uri("https://settings.example.com/live.html"), OverlayUrlSource.SettingsFile), result);
        Assert.Equal(OverlayUrlSource.SettingsFile,
            Resolve(settings: """{ "URL": "https://settings.example.com/live.html", /* note */ }""").Source);
    }

    [Fact]
    public void TheBuildDefaultIsUsedLast()
    {
        Assert.Equal(new OverlayUrlResult(new Uri(Built), OverlayUrlSource.BuildDefault), Resolve(built: Built));
    }

    [Fact]
    public void WithNothingSetThereIsNoPageAndNoError()
    {
        var result = Resolve(built: "");
        Assert.Equal(new OverlayUrlResult(null, OverlayUrlSource.None), result);
        var message = OverlayUrl.SetupMessage(result);
        Assert.Contains("--url https://", message);
        Assert.Contains(OverlayUrl.EnvironmentVariable, message);
        Assert.Contains(OverlayUrl.SettingsFileName, message);
    }

    [Theory]
    [InlineData("http://example.com/live.html")]
    [InlineData("https://user:secret@example.com/live.html")]
    [InlineData("file:///C:/live.html")]
    [InlineData("javascript:alert(1)")]
    [InlineData("live.html")]
    [InlineData("https://<your-app>.<region>.azurecontainerapps.io/live.html")]
    public void AnInvalidSettingIsReportedInsteadOfFallingThrough(string value)
    {
        foreach (var result in new[]
        {
            Resolve(["--url", value], FromEnvironment, Settings, Built),
            Resolve(environment: value, settings: Settings, built: Built),
            Resolve(settings: $$"""{"url":"{{value}}"}""", built: Built),
            Resolve(built: value),
        })
        {
            Assert.Null(result.Url);
            Assert.Contains("HTTPS", result.Error);
            Assert.StartsWith("Live Coach 페이지 주소를 쓸 수 없습니다.", OverlayUrl.SetupMessage(result));
        }
    }

    [Theory]
    [InlineData("https://example.com/live.html")]
    [InlineData("http://localhost:5173/live.html")]
    [InlineData("http://127.0.0.1:5173/live.html")]
    [InlineData("  https://example.com/live.html  ")]
    public void HttpsAndLoopbackHttpAreAccepted(string value) => Assert.True(OverlayUrl.TryValidate(value, out _));

    [Theory]
    [InlineData("--url")]
    [InlineData("--url --fake-mic")]
    public void AnArgumentWithoutAnAddressIsAnError(string args)
    {
        var result = Resolve(args.Split(' '), FromEnvironment, Settings, Built);
        Assert.Equal(OverlayUrlSource.Argument, result.Source);
        Assert.Null(result.Url);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""["https://example.com/live.html"]""")]
    [InlineData("""{"address":"https://example.com/live.html"}""")]
    [InlineData("""{"url":42}""")]
    public void AnUnusableSettingsFileIsAnError(string json)
    {
        var result = Resolve(settings: json, built: Built);
        Assert.Equal(OverlayUrlSource.SettingsFile, result.Source);
        Assert.Null(result.Url);
        Assert.Contains(OverlayUrl.SettingsFileName, result.Error);
    }

    [Fact]
    public void AnUnreadableSettingsFileIsAnError()
    {
        var result = OverlayUrl.Resolve(["LiveCoach.exe"], _ => null, () => throw new IOException("locked"), Built);
        Assert.Equal(OverlayUrlSource.SettingsFile, result.Source);
        Assert.Null(result.Url);
    }

    [Fact]
    public void TheBuildDefaultComesFromAssemblyMetadata()
    {
        Assert.Equal(Built, OverlayUrl.BuildDefault(typeof(OverlayUrlTests).Assembly));
        Assert.Null(OverlayUrl.BuildDefault(typeof(object).Assembly));
    }
}
