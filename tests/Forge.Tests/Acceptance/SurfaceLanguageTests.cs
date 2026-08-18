using System.CommandLine;
using System.Globalization;
using Forge.Application;
using Forge.Cli;
using Forge.Localization;
using Forge.Tests.Support;

namespace Forge.AcceptanceTests;

public sealed class SurfaceLanguageTests
{
    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ConfiguredLanguageOverridesTheOperatingSystemCulture()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            using TestEnvironment environment = new();
            await environment.Application.SetConfigurationAsync(
                Configuration.ConfigurationScope.User,
                null,
                "language.ui",
                "ru",
                TestContext.Current.CancellationToken);

            // The hosts resolve the language from startup before rendering any text.
            StartupStatus startup = await environment.Application.GetStartupStatusAsync(
                null,
                TestContext.Current.CancellationToken);
            StringWriter output = new(CultureInfo.InvariantCulture);
            await CliApplication
                .CreateRootCommand(
                    SurfaceText.For(new ResourceLocalizationCatalog(), startup.Language.Ui),
                    output,
                    environment.Application)
                .Parse(["status"])
                .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

            Assert.Equal("ru", startup.Language.Ui);
            Assert.Contains("Проект не инициализирован.", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public void UnknownLanguageTagFallsBackToEnglish()
    {
        SurfaceText text = SurfaceText.For(new ResourceLocalizationCatalog(), "??");

        Assert.Equal("en", text.Culture.TwoLetterISOLanguageName);
        Assert.Equal("Forge is ready.", text.Resolve(MessageKeys.StatusReady));
    }

    [Theory]
    [Trait("Category", "Acceptance")]
    [InlineData(DiagnosticCodes.None, ExitCodes.Ok)]
    [InlineData(DiagnosticCodes.ProjectAlreadyInitialized, ExitCodes.Ok)]
    [InlineData(DiagnosticCodes.ConfigurationKeyUnknown, ExitCodes.Usage)]
    [InlineData(DiagnosticCodes.ProjectRootNotAbsolute, ExitCodes.Usage)]
    [InlineData(DiagnosticCodes.ConfigurationScopeViolation, ExitCodes.Configuration)]
    [InlineData(DiagnosticCodes.ConfigurationInvalid, ExitCodes.Configuration)]
    [InlineData(DiagnosticCodes.ProjectNotInitialized, ExitCodes.Project)]
    [InlineData(DiagnosticCodes.ProjectDirectoryUnknown, ExitCodes.Project)]
    [InlineData(DiagnosticCodes.ProjectRootMissing, ExitCodes.Project)]
    [InlineData(DiagnosticCodes.PlatformNotSupported, ExitCodes.Platform)]
    [InlineData(DiagnosticCodes.UpdateCheckDeferred, ExitCodes.Update)]
    [InlineData(DiagnosticCodes.ProviderPreflightPending, ExitCodes.Provider)]
    [InlineData(DiagnosticCodes.PermissionDenied, ExitCodes.Authorization)]
    [InlineData(DiagnosticCodes.ConfirmationRequired, ExitCodes.Confirmation)]
    [InlineData(DiagnosticCodes.SuggestionStale, ExitCodes.Concurrency)]
    [InlineData(DiagnosticCodes.InternalError, ExitCodes.Internal)]
    public void DiagnosticCodesMapToTheContractExitCodes(string diagnosticCode, int expected) =>
        Assert.Equal(expected, ExitCodes.For(diagnosticCode));
}
