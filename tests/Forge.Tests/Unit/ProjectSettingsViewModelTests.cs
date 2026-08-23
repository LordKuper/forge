using System.Text.Json;
using Forge.Application;
using Forge.Configuration;
using Forge.Desktop.Presentation;
using Forge.Localization;
using Forge.Tests.Support;

namespace Forge.UnitTests;

public sealed class ProjectSettingsViewModelTests
{
    private static ProjectSettingsViewModel Create(
        TestEnvironment environment, IFolderPickerPort? picker = null) =>
        new(
            environment.Application,
            environment.Resolve<ProjectCatalogStore>(),
            new MainPageViewModel(Text(), environment.Application),
            (_, _) => Task.FromResult<IForgeMutations>(environment.Application),
            picker ?? new FakeFolderPicker(),
            new SurfaceTextProvider(new ResourceLocalizationCatalog(), "en"));

    private static SurfaceText Text() =>
        new(new ResourceLocalizationCatalog(), System.Globalization.CultureInfo.GetCultureInfo("en"));

    [Fact]
    [Trait("Category", "Unit")]
    public async Task LoadAsyncReportsProjectScopedDefaults()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectSettingsViewModel viewModel = Create(environment);
        Guid projectId = Guid.NewGuid();

        ProjectSettingsSnapshot snapshot =
            await viewModel.LoadAsync(projectId, environment.ProjectRoot, "alias", cancellationToken);

        Assert.Equal(environment.ProjectRoot, snapshot.Root);
        Assert.Equal(projectId, snapshot.ProjectId);
        Assert.Equal("alias", snapshot.Alias);
        Assert.Equal("en", snapshot.UserFacingLanguage);
        Assert.Equal("en", snapshot.AgentFacingLanguage);
        Assert.Equal(TokenBudgetResolver.DefaultTokenBudget, snapshot.TokenBudget);
        Assert.Empty(snapshot.AllowedModels);
        Assert.Equal(ConfigurationProvenance.BuiltInDefault, snapshot.TokenBudgetProvenance);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ValidateRejectsANonPositiveTokenBudget()
    {
        ProjectSettingsEdit edit = new()
        {
            UserFacingLanguage = "en",
            AgentFacingLanguage = "en",
            TokenBudget = 0,
            AllowedModels = [],
        };

        IReadOnlyList<string> errors = ProjectSettingsViewModel.Validate(edit);

        Assert.Contains(MessageKeys.SettingsTokenBudgetInvalid, errors);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SaveAsyncWritesOnlyTheChangedProjectScopedKeys()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectSettingsViewModel viewModel = Create(environment);
        Guid projectId = Guid.NewGuid();
        ProjectSettingsSnapshot before =
            await viewModel.LoadAsync(projectId, environment.ProjectRoot, null, cancellationToken);
        ProjectSettingsEdit edit = ProjectSettingsEdit.From(before);
        edit.TokenBudget = 50_000;
        edit.AllowedModels = ["codex:gpt-5"];

        ProjectSettingsSaveResult result =
            await viewModel.SaveAsync(environment.ProjectRoot, edit, before, cancellationToken);

        Assert.True(result.Succeeded);
        ProjectSettingsSnapshot after =
            await viewModel.LoadAsync(projectId, environment.ProjectRoot, null, cancellationToken);
        Assert.Equal(50_000, after.TokenBudget);
        Assert.Equal(["codex:gpt-5"], after.AllowedModels);
        Assert.Equal(ConfigurationProvenance.Project, after.TokenBudgetProvenance);
        Assert.Equal("en", after.UserFacingLanguage);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SaveAsyncRejectsAnInvalidEditWithoutWritingAnything()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectSettingsViewModel viewModel = Create(environment);
        Guid projectId = Guid.NewGuid();
        ProjectSettingsSnapshot before =
            await viewModel.LoadAsync(projectId, environment.ProjectRoot, null, cancellationToken);
        ProjectSettingsEdit edit = ProjectSettingsEdit.From(before);
        edit.TokenBudget = -1;

        ProjectSettingsSaveResult result =
            await viewModel.SaveAsync(environment.ProjectRoot, edit, before, cancellationToken);

        Assert.False(result.Succeeded);
        ProjectSettingsSnapshot after =
            await viewModel.LoadAsync(projectId, environment.ProjectRoot, null, cancellationToken);
        Assert.Equal(before.TokenBudget, after.TokenBudget);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetAliasAsyncUpdatesTheCatalogEntry()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        ProjectSettingsViewModel viewModel = Create(environment);

        // PR #98 review finding 4: SetAliasAsync now returns the write's real, already-localized
        // outcome (not the raw diagnostic code, and not an unconditional "saved" regardless of the
        // actual result).
        string message = await viewModel.SetAliasAsync(added.Entry!.ProjectId, "New alias", cancellationToken);

        Assert.Equal(Text().Resolve(MessageKeys.ProjectAliasSet), message);
        ProjectCatalogListing listing = await catalog.ListAsync(cancellationToken);
        Assert.Equal("New alias", Assert.Single(listing.Entries).Alias);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetAliasAsyncReportsFailureInsteadOfClaimingSuccess()
    {
        // PR #98 review finding 4: the alias path previously reported SettingsSaved unconditionally,
        // so a failed write (here, an alias past ProjectCatalogStore's own length limit) read as
        // success. Regression test: an alias write that ProjectCatalogStore actually rejects must
        // never render the same text as a successful one.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        ProjectSettingsViewModel viewModel = Create(environment);

        string message = await viewModel.SetAliasAsync(
            added.Entry!.ProjectId, new string('a', 10_000), cancellationToken);

        Assert.NotEqual(Text().Resolve(MessageKeys.ProjectAliasSet), message);
        Assert.Contains(DiagnosticCodes.ProjectCatalogAliasTooLong, message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RelinkAsyncVerifiesTheNewRootBeforeAcceptingIt()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectCatalogStore catalog = environment.Resolve<ProjectCatalogStore>();
        ProjectCatalogResult added = await catalog.AddAsync(environment.ProjectRoot, cancellationToken);
        string otherRoot = Path.Combine(environment.Root, "other");
        Directory.CreateDirectory(otherRoot);
        await environment.InitializeAsync(otherRoot, true, cancellationToken);
        FakeFolderPicker picker = new(otherRoot);
        ProjectSettingsViewModel viewModel = Create(environment, picker);

        string message = await viewModel.RelinkAsync(added.Entry!.ProjectId, cancellationToken);

        // otherRoot is a different, independently initialized project, so its own manifest id
        // never matches the entry being relinked -- ProjectCatalogStore.RelinkAsync must reject
        // this rather than trust the picked path. PR #98 review finding 4: the failure reaches the
        // user as localized text with the machine diagnostic code appended, not the raw code alone.
        Assert.Equal(
            Text().Resolve(MessageKeys.ProjectRelinkFailed) +
                $" ({DiagnosticCodes.ProjectCatalogRelinkMismatch})",
            message);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GenerateDiagnosticBundleAsyncReturnsParseableRedactedJson()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        ProjectSettingsViewModel viewModel = Create(environment);

        string json = await viewModel.GenerateDiagnosticBundleAsync(environment.ProjectRoot, cancellationToken);

        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }
}
