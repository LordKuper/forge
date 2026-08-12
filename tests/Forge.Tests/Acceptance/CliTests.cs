using System.CommandLine;
using System.Globalization;
using Forge.Cli;
using Forge.Localization;
using Forge.Providers;
using Forge.Tests.Support;
using Forge.Updater;

namespace Forge.AcceptanceTests;

public sealed class CliTests
{
    [Fact]
    [Trait("Category", "Acceptance")]
    public void RootCommandOmitsInstallAndUpdateWhenNoPlatformIsComposed()
    {
        using TestEnvironment environment = new();
        ResourceLocalizationCatalog catalog = new();

        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            new StringWriter(CultureInfo.InvariantCulture),
            environment.Application);

        Assert.DoesNotContain(root.Subcommands, command => command.Name is "install" or "update");
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ModelsCommandReportsReadyProviders()
    {
        using TestEnvironment environment = new(
            providers: new FakeProviderToolchainManager(FakeProviderToolchainManager.Ready));
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(Text(catalog), output, environment.Application);

        int exitCode = await root
            .Parse(["models"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("codex ready 0.146.0", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("claude_code ready 2.1.221", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ModelsCommandDefaultsToReadOnlyDiscovery()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application,
            diagnostics);

        int exitCode = await root
            .Parse(["models", "--json"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Provider, exitCode);
        Assert.Contains("\"missing\"", output.ToString(), StringComparison.Ordinal);
        Assert.Equal($"provider_preflight_pending{Environment.NewLine}", diagnostics.ToString());
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task ModelsCommandRefreshReportsUpdateFailedWhenRepairIsNeeded()
    {
        ProviderToolchainStatus failed = new([
            new(ProviderKind.Codex, ProviderState.Failed, null, ProviderDiagnosticCodes.UpdateFailed),
            ProviderStatus.Ready(ProviderKind.ClaudeCode, "2.1.221"),
        ]);
        using TestEnvironment environment = new(providers: new FakeProviderToolchainManager(failed));
        StringWriter output = new(CultureInfo.InvariantCulture);
        StringWriter diagnostics = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application,
            diagnostics);

        int exitCode = await root
            .Parse(["models", "--refresh", "--json"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Provider, exitCode);
        Assert.Equal($"provider_update_failed{Environment.NewLine}", diagnostics.ToString());
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task StatusCommandUsesSharedLocalizationCatalog()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            using TestEnvironment environment = new();
            StringWriter output = new(CultureInfo.InvariantCulture);
            ResourceLocalizationCatalog catalog = new();

            int exitCode = await CliApplication
                .CreateRootCommand(Text(catalog), output, environment.Application)
                .Parse(["status"])
                .InvokeAsync(
                    new InvocationConfiguration(),
                    TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Contains(
                "Запуск заблокирован; работа со спринтами недоступна.",
                output.ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                "Проект не инициализирован.",
                output.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task HelpOptionIsHandledByCliParser()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);
        ILocalizationCatalog catalog = environment.Resolve<ILocalizationCatalog>();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application);

        int exitCode = await root
            .Parse(["--help"])
            .InvokeAsync(
                new InvocationConfiguration { Output = output },
                TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains(
            catalog.Resolve(MessageKeys.AppDescription),
            output.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task InstallCommandUsesTheInstalledReleaseFlow()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application,
            install: _ => ValueTask.FromResult(
                new InstallationResult(true, "C:\\Forge", UpdateDiagnostic.None)));

        int exitCode = await root
            .Parse(["install"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal($"{catalog.Resolve(MessageKeys.InstallCompleted)}{Environment.NewLine}", output.ToString());
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task UpdateCommandUsesTheSharedUpdateFlow()
    {
        using TestEnvironment environment = new();
        StringWriter output = new(CultureInfo.InvariantCulture);
        ResourceLocalizationCatalog catalog = new();
        RootCommand root = CliApplication.CreateRootCommand(
            Text(catalog),
            output,
            environment.Application,
            update: _ => ValueTask.FromResult(new UpdateResult(
                UpdateLifecycleState.RestartRequested,
                UpdateDiagnostic.None)));

        int exitCode = await root
            .Parse(["update"])
            .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal($"{catalog.Resolve(MessageKeys.UpdateCompleted)}{Environment.NewLine}", output.ToString());
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task InstallCommandLocalizesFailureOutput()
    {
        CultureInfo original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru");
            using TestEnvironment environment = new();
            StringWriter output = new(CultureInfo.InvariantCulture);
            ResourceLocalizationCatalog catalog = new();
            RootCommand root = CliApplication.CreateRootCommand(
                Text(catalog),
                output,
                environment.Application,
                install: _ => ValueTask.FromResult(InstallationResult.Failure(new(
                    UpdateDiagnosticCode.ReleaseUnavailable,
                    "The release endpoint could not be reached."))));

            int exitCode = await root
                .Parse(["install"])
                .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

            Assert.Equal(ExitCodes.Update, exitCode);
            Assert.Equal($"{catalog.Resolve(MessageKeys.InstallFailed)}{Environment.NewLine}", output.ToString());
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    private static SurfaceText Text(ILocalizationCatalog catalog) =>
        new(catalog, CultureInfo.CurrentUICulture);
}
