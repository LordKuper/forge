namespace Forge.Updater;

public sealed record ReleaseApiRequest(string? EntityTag);

public sealed record ReleaseApiResponse(
    bool NotModified,
    string? EntityTag,
    IReadOnlyList<ReleaseMetadata> Releases);

public sealed record ReleaseLookupResult(
    ReleaseMetadata? Release,
    UpdateDiagnostic Diagnostic,
    bool FromCache)
{
    public bool IsUpdateAvailable => Release is not null;
}

public interface IReleaseApi
{
    ValueTask<ReleaseApiResponse> GetReleasesAsync(
        ReleaseApiRequest request,
        CancellationToken cancellationToken);
}

public sealed class ForgeReleaseClient(IReleaseApi api) : IForgeReleaseClient
{
    private readonly IReleaseApi api = api ?? throw new ArgumentNullException(nameof(api));
    private string? entityTag;
    private IReadOnlyList<ReleaseMetadata>? cachedReleases;

    public async ValueTask<ReleaseLookupResult> GetLatestStableAsync(
        SemanticVersion currentVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        ReleaseApiResponse response = await api.GetReleasesAsync(
            new ReleaseApiRequest(entityTag),
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ReleaseMetadata>? releases = response.NotModified ? cachedReleases : response.Releases;
        if (releases is null)
        {
            return new(null, new(UpdateDiagnosticCode.ReleaseUnavailable, "The release endpoint returned 304 without a cached response."), false);
        }

        if (!response.NotModified)
        {
            entityTag = response.EntityTag;
            cachedReleases = response.Releases;
        }

        ReleaseMetadata? latest = releases
            .Where(release => !release.IsDraft && !release.IsPrerelease && release.Version.IsStable)
            .OrderByDescending(release => release.Version)
            .FirstOrDefault();
        if (latest is null || latest.Version.CompareTo(currentVersion) <= 0)
        {
            return new(null, new(UpdateDiagnosticCode.NoUpdateAvailable, "No newer published stable release is available."), response.NotModified);
        }

        return new(latest, UpdateDiagnostic.None, response.NotModified);
    }
}
