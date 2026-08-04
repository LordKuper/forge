using System.CommandLine;
using System.Globalization;
using Forge.Cli;
using Forge.Localization;
using Forge.Tests.Support;
using Forge.Updater;
using Forge.Updater.Windows;

namespace Forge.AcceptanceTests;

public sealed class CliTests
{
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
                .CreateRootCommand(catalog, output, environment.Application)
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
            catalog,
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
            catalog,
            output,
            environment.Application,
            _ => ValueTask.FromResult(new WindowsInstallationResult(true, "C:\\Forge", UpdateDiagnostic.None)));

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
            catalog,
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
                catalog,
                output,
                environment.Application,
                _ => ValueTask.FromResult(WindowsInstallationResult.Failure(new(
                    UpdateDiagnosticCode.ReleaseUnavailable,
                    "The release endpoint could not be reached."))));

            int exitCode = await root
                .Parse(["install"])
                .InvokeAsync(new InvocationConfiguration(), TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.Equal($"{catalog.Resolve(MessageKeys.InstallFailed)}{Environment.NewLine}", output.ToString());
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }
}
