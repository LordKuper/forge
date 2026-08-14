using System.Globalization;
using System.Text.Json;
using Forge.Application;
using Forge.Tests.Support;
using Json.Schema;

namespace Forge.UnitTests;

public sealed class StartupStatusJsonTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheSerializedStartupStatusSatisfiesTheVersionedStartupCheckContract()
    {
        using TestEnvironment environment = new();

        StartupStatus status = await environment.Application.GetStartupStatusAsync(
            null, TestContext.Current.CancellationToken);

        string json = StatusJson.Serialize(status);
        using JsonDocument instance = JsonDocument.Parse(json);

        EvaluationResults result = ContractSchemas.Load("startup-check").Evaluate(
            instance.RootElement,
            new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
        Assert.True(result.IsValid, json);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task TheSerializedStartupStatusStaysCultureInvariant()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
            using TestEnvironment environment = new();

            StartupStatus status = await environment.Application.GetStartupStatusAsync(
                null, TestContext.Current.CancellationToken);
            using JsonDocument json = JsonDocument.Parse(StatusJson.Serialize(status));

            Assert.Equal("blocked", json.RootElement.GetProperty("state").GetString());
            Assert.Equal(
                "user_configuration",
                json.RootElement.GetProperty("checks")[0].GetProperty("id").GetString());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

}
