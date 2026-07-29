using System.Security.Cryptography;
using System.Text;
using Forge.Updater;

namespace Forge.UnitTests;

public sealed class UpdaterTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void TargetNormalizationAndResolutionAreDeterministic()
    {
        UpdateTarget target = new(" Windows ", " X64 ", " Portable_Bundle ");
        TestStrategy strategy = new(true);
        PlatformUpdateStrategyResolver resolver = new([strategy]);

        StrategyResolution result = resolver.Resolve(target);

        Assert.Equal("windows", target.OperatingSystem);
        Assert.Equal("x64", target.Architecture);
        Assert.Equal("portable_bundle", target.Packaging);
        Assert.Same(strategy, result.Strategy);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ResolverRejectsZeroAndMultipleStrategies()
    {
        UpdateTarget target = new("linux", "x64", "portable_bundle");

        Assert.Equal(
            UpdateDiagnosticCode.PlatformNotSupported,
            new PlatformUpdateStrategyResolver([]).Resolve(target).Diagnostic.Code);
        Assert.Equal(
            UpdateDiagnosticCode.InvalidComposition,
            new PlatformUpdateStrategyResolver([new TestStrategy(true), new TestStrategy(true)])
                .Resolve(target).Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UnsupportedPlatformDoesNotQueryReleaseOrMutate()
    {
        CountingReleaseClient releases = new();
        ForgeSelfUpdater updater = CreateUpdater(
            new FixedTargetDetector(new UpdateTarget("linux", "x64", "portable_bundle")),
            [],
            releases,
            new PassingVerifier(),
            new RestartTokenService(),
            new RejectingRestartCoordinator());

        UpdateResult result = await updater.UpdateAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(UpdateDiagnosticCode.PlatformNotSupported, result.Diagnostic.Code);
        Assert.Equal(0, releases.Calls);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReleaseClientSelectsOnlyNewerPublishedStableReleaseAndUsesEtag()
    {
        TestReleaseApi api = new(
            new ReleaseApiResponse(
                false,
                "\"releases-v1\"",
                [
                    Release("1.0.0"),
                    Release("1.2.0", prerelease: true),
                    Release("1.1.0"),
                    Release("1.3.0", draft: true),
                ]),
            new ReleaseApiResponse(true, "\"releases-v1\"", []),
            new ReleaseApiResponse(true, "\"releases-v1\"", []));
        ForgeReleaseClient client = new(api);

        ReleaseLookupResult first = await client.GetLatestStableAsync(
            SemanticVersion.Parse("1.0.0"),
            TestContext.Current.CancellationToken);
        ReleaseLookupResult second = await client.GetLatestStableAsync(
            SemanticVersion.Parse("1.0.0"),
            TestContext.Current.CancellationToken);
        ReleaseLookupResult equal = await client.GetLatestStableAsync(
            SemanticVersion.Parse("1.1.0"),
            TestContext.Current.CancellationToken);

        Assert.Equal("1.1.0", first.Release!.Version.ToString());
        Assert.True(second.FromCache);
        Assert.Equal("\"releases-v1\"", api.Requests[1].EntityTag);
        Assert.Equal(UpdateDiagnosticCode.NoUpdateAvailable, equal.Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ReleaseClientRejectsUncachedNotModifiedResponse()
    {
        ForgeReleaseClient client = new(new TestReleaseApi(new ReleaseApiResponse(true, "etag", [])));

        ReleaseLookupResult result = await client.GetLatestStableAsync(
            SemanticVersion.Parse("1.0.0"),
            TestContext.Current.CancellationToken);

        Assert.Equal(UpdateDiagnosticCode.ReleaseUnavailable, result.Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task VerifierRejectsIncorrectHashAndSize()
    {
        ReleaseAsset asset = new("forge-windows-x64-portable_bundle.zip", 4, new Uri("https://example.test/asset"));
        ReleaseAsset checksum = new("checksums.txt", 1, new Uri("https://example.test/checksums"));
        ReleaseAsset provenance = new("provenance.intoto.jsonl", 1, new Uri("https://example.test/provenance"));
        Dictionary<string, byte[]> content = new(StringComparer.Ordinal)
        {
            [asset.Name] = Encoding.UTF8.GetBytes("bad"),
            [checksum.Name] = Encoding.UTF8.GetBytes($"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("good")))}  4  {asset.Name}\n"),
            [provenance.Name] = Encoding.UTF8.GetBytes("bundle"),
        };
        ReleaseAssetVerifier verifier = new(
            new("github.com/LordKuper/forge", "release.yml", "forge-{0}-{1}-{2}.zip", checksum.Name, provenance.Name),
            new MemoryDownloader(content),
            new PassingProvenanceVerifier());

        VerificationResult result = await verifier.VerifyAsync(
            new ReleaseMetadata(SemanticVersion.Parse("1.1.0"), new Uri("https://example.test/release"), false, false, DateTimeOffset.UtcNow, [asset, checksum, provenance]),
            new UpdateTarget("windows", "x64", "portable_bundle"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateDiagnosticCode.VerificationFailed, result.Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RestartTokensAreOneUseAndPreserveLaunchContext()
    {
        RestartTokenService tokens = new();
        UpdateRequest request = new(
            SemanticVersion.Parse("1.0.0"),
            "C:\\Forge\\forge.exe",
            ["status", "--json"],
            "C:\\work");

        RestartContext context = tokens.Create(request);

        Assert.Equal(request.Arguments, context.Arguments);
        Assert.Equal(request.WorkingDirectory, context.WorkingDirectory);
        Assert.True(tokens.Consume(context.Token));
        Assert.False(tokens.Consume(context.Token));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void StartupHandshakeRejectsMissingAndReplayedToken()
    {
        RestartTokenService tokens = new();
        StartupHandshake handshake = new(tokens);
        RestartContext context = tokens.Create(CreateRequest());

        Assert.Equal(UpdateDiagnosticCode.None, handshake.Confirm(context.Token).Code);
        Assert.Equal(UpdateDiagnosticCode.HandshakeFailed, handshake.Confirm(context.Token).Code);
        Assert.Equal(UpdateDiagnosticCode.HandshakeFailed, handshake.Confirm("missing").Code);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RestartFailureRollsBackActivatedRelease()
    {
        TestStrategy strategy = new(true) { ActivateResult = new(true, new("activation", "1.0.0", "1.1.0"), UpdateDiagnostic.None) };
        ForgeSelfUpdater updater = CreateUpdater(
            new FixedTargetDetector(new UpdateTarget("windows", "x64", "portable_bundle")),
            [strategy],
            new StaticReleaseClient(Release("1.1.0")),
            new PassingVerifier(),
            new RestartTokenService(),
            new FailingRestartCoordinator());

        UpdateResult result = await updater.UpdateAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(UpdateLifecycleState.RolledBack, result.State);
        Assert.True(result.RollbackAttempted);
        Assert.Equal(1, strategy.RollbackCalls);
    }

    private static ForgeSelfUpdater CreateUpdater(
        IUpdateTargetDetector detector,
        IEnumerable<IPlatformUpdateStrategy> strategies,
        IForgeReleaseClient releases,
        IReleaseVerifier verifier,
        IRestartTokenService tokens,
        IRestartCoordinator restart) =>
        new(detector, new PlatformUpdateStrategyResolver(strategies), releases, verifier, tokens, restart);

    private static UpdateRequest CreateRequest() =>
        new(SemanticVersion.Parse("1.0.0"), "C:\\Forge\\forge.exe", ["status"], "C:\\work");

    private static ReleaseMetadata Release(string version, bool draft = false, bool prerelease = false) =>
        new(
            SemanticVersion.Parse(version),
            new Uri($"https://example.test/{version}"),
            draft,
            prerelease,
            DateTimeOffset.UtcNow,
            []);

    private sealed class FixedTargetDetector(UpdateTarget target) : IUpdateTargetDetector
    {
        public UpdateTarget Detect() => target;
    }

    private sealed class CountingReleaseClient : IForgeReleaseClient
    {
        public int Calls { get; private set; }

        public ValueTask<ReleaseLookupResult> GetLatestStableAsync(SemanticVersion currentVersion, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new ReleaseLookupResult(null, new(UpdateDiagnosticCode.NoUpdateAvailable, "unused"), false));
        }
    }

    private sealed class StaticReleaseClient(ReleaseMetadata release) : IForgeReleaseClient
    {
        public ValueTask<ReleaseLookupResult> GetLatestStableAsync(SemanticVersion currentVersion, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new ReleaseLookupResult(release, UpdateDiagnostic.None, false));
    }

    private sealed class TestReleaseApi(params ReleaseApiResponse[] responses) : IReleaseApi
    {
        private readonly Queue<ReleaseApiResponse> responses = new(responses);

        public List<ReleaseApiRequest> Requests { get; } = [];

        public ValueTask<ReleaseApiResponse> GetReleasesAsync(ReleaseApiRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ValueTask.FromResult(responses.Dequeue());
        }
    }

    private sealed class PassingVerifier : IReleaseVerifier
    {
        public ValueTask<VerificationResult> VerifyAsync(ReleaseMetadata release, UpdateTarget target, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new VerificationResult(
                true,
                new VerifiedRelease(release.Version, release.ReleaseUri, new("asset.zip", 0, new Uri("https://example.test/asset")), "00", "provenance"),
                UpdateDiagnostic.None));
    }

    private sealed class TestStrategy(bool supports) : IPlatformUpdateStrategy
    {
        public ActivationResult ActivateResult { get; set; } = new(true, null, UpdateDiagnostic.None);

        public int RollbackCalls { get; private set; }

        public bool Supports(UpdateTarget target) => supports;

        public ValueTask<StageResult> StageAsync(VerifiedRelease release, UpdateTarget target, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new StageResult(true, new("stage", release), UpdateDiagnostic.None));

        public ValueTask<ActivationResult> ActivateAsync(StagedRelease staged, RestartContext restart, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ActivateResult);

        public ValueTask<RollbackResult> RollbackAsync(ActivationReceipt receipt, CancellationToken cancellationToken)
        {
            RollbackCalls++;
            return ValueTask.FromResult(new RollbackResult(true, UpdateDiagnostic.None));
        }
    }

    private sealed class MemoryDownloader(Dictionary<string, byte[]> content) : IReleaseAssetDownloader
    {
        public ValueTask<Stream> DownloadAsync(ReleaseAsset asset, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new MemoryStream(content[asset.Name], writable: false));
    }

    private sealed class PassingProvenanceVerifier : IProvenanceVerifier
    {
        public ValueTask<bool> VerifyAsync(Stream bundle, ReleaseTrustPolicy policy, ReleaseAsset asset, string sha256, CancellationToken cancellationToken) =>
            ValueTask.FromResult(true);
    }

    private sealed class FailingRestartCoordinator : IRestartCoordinator
    {
        public ValueTask<UpdateDiagnostic> RestartAsync(RestartContext restart, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new UpdateDiagnostic(UpdateDiagnosticCode.RestartFailed, "restart failed"));
    }
}
