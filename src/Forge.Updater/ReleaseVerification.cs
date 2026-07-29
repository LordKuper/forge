using System.Security.Cryptography;
using System.Text;

namespace Forge.Updater;

public sealed record VerificationResult(bool Succeeded, VerifiedRelease? Release, UpdateDiagnostic Diagnostic)
{
    public static VerificationResult Failure(string detail) =>
        new(false, null, new(UpdateDiagnosticCode.VerificationFailed, detail));
}

public sealed record ReleaseTrustPolicy(
    string Repository,
    string Workflow,
    string AssetNameTemplate,
    string ChecksumAssetName,
    string ProvenanceAssetName);

public interface IReleaseAssetDownloader
{
    ValueTask<Stream> DownloadAsync(ReleaseAsset asset, CancellationToken cancellationToken);
}

public interface IProvenanceVerifier
{
    ValueTask<bool> VerifyAsync(
        Stream bundle,
        ReleaseTrustPolicy policy,
        ReleaseAsset asset,
        string sha256,
        CancellationToken cancellationToken);
}

public sealed class ReleaseAssetVerifier(
    ReleaseTrustPolicy policy,
    IReleaseAssetDownloader downloader,
    IProvenanceVerifier provenanceVerifier) : IReleaseVerifier
{
    private readonly ReleaseTrustPolicy policy = policy ?? throw new ArgumentNullException(nameof(policy));
    private readonly IReleaseAssetDownloader downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
    private readonly IProvenanceVerifier provenanceVerifier = provenanceVerifier ?? throw new ArgumentNullException(nameof(provenanceVerifier));

    public async ValueTask<VerificationResult> VerifyAsync(
        ReleaseMetadata release,
        UpdateTarget target,
        CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(release);
            ArgumentNullException.ThrowIfNull(target);
            string expectedName = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                policy.AssetNameTemplate,
                target.OperatingSystem,
                target.Architecture,
                target.Packaging);
            ReleaseAsset? asset = release.Assets.SingleOrDefault(candidate =>
                string.Equals(candidate.Name, expectedName, StringComparison.Ordinal));
            ReleaseAsset? checksumAsset = release.Assets.SingleOrDefault(candidate =>
                string.Equals(candidate.Name, policy.ChecksumAssetName, StringComparison.Ordinal));
            ReleaseAsset? provenanceAsset = release.Assets.SingleOrDefault(candidate =>
                string.Equals(candidate.Name, policy.ProvenanceAssetName, StringComparison.Ordinal));
            if (asset is null || checksumAsset is null || provenanceAsset is null)
            {
                return VerificationResult.Failure("The release is missing a required asset, checksum manifest, or provenance bundle.");
            }

            ChecksumEntry? expected = await FindExpectedChecksumAsync(checksumAsset, asset, cancellationToken).ConfigureAwait(false);
            if (expected is null || expected.Size != asset.Size)
            {
                return VerificationResult.Failure("The checksum manifest does not contain an exact asset name, size, and SHA-256 hash.");
            }

            string actualHash;
            long actualSize;
            await using (Stream contents = await downloader.DownloadAsync(asset, cancellationToken).ConfigureAwait(false))
            using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[81920];
                int read;
                actualSize = 0;
                while ((read = await contents.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    hash.AppendData(buffer, 0, read);
                    actualSize += read;
                }

                actualHash = Convert.ToHexString(hash.GetHashAndReset());
            }

            if (actualSize != expected.Size || !string.Equals(actualHash, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return VerificationResult.Failure("The downloaded asset size or SHA-256 hash does not match the signed manifest.");
            }

            await using Stream provenance = await downloader.DownloadAsync(provenanceAsset, cancellationToken).ConfigureAwait(false);
            if (!await provenanceVerifier.VerifyAsync(provenance, policy, asset, actualHash, cancellationToken).ConfigureAwait(false))
            {
                return VerificationResult.Failure("The provenance bundle does not satisfy the built-in repository and workflow policy.");
            }

            return new(
                true,
                new VerifiedRelease(release.Version, release.ReleaseUri, asset, actualHash, provenanceAsset.Name),
                UpdateDiagnostic.None);
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidDataException)
        {
            return VerificationResult.Failure("Release asset verification could not complete.");
        }
    }

    private async ValueTask<ChecksumEntry?> FindExpectedChecksumAsync(
        ReleaseAsset checksumAsset,
        ReleaseAsset expectedAsset,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await downloader.DownloadAsync(checksumAsset, cancellationToken).ConfigureAwait(false);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length == 3 &&
                fields[0].Length == 64 &&
                fields[0].All(Uri.IsHexDigit) &&
                long.TryParse(
                    fields[1],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out long size) &&
                size >= 0 &&
                string.Equals(fields[2].TrimStart('*'), expectedAsset.Name, StringComparison.Ordinal))
            {
                return new ChecksumEntry(fields[0].ToUpperInvariant(), size);
            }
        }

        return null;
    }

    private sealed record ChecksumEntry(string Sha256, long Size);
}
