using System.CommandLine;
using System.Globalization;
using Forge.Bootstrap;
using Forge.Cli;
using Forge.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
            StringWriter output = new(CultureInfo.InvariantCulture);
            ResourceLocalizationCatalog catalog = new();

            int exitCode = await CliApplication
                .CreateRootCommand(catalog, output)
                .Parse(["status"])
                .InvokeAsync(
                    new InvocationConfiguration(),
                    TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Equal($"Forge is ready.{Environment.NewLine}", output.ToString());
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
        using IHost host = ForgeHost.CreateBuilder().Build();
        StringWriter output = new(CultureInfo.InvariantCulture);
        ILocalizationCatalog catalog =
            host.Services.GetRequiredService<ILocalizationCatalog>();
        RootCommand root = CliApplication.CreateRootCommand(
            catalog,
            output);

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
}
