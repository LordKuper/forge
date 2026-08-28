using Forge.Application;

namespace Forge.Providers;

/// <summary>
/// The release-availability check's cache (ADR 0008). Everything about how it is stored — the
/// per-instance directory, the snake_case JSON, the atomic write, the degrade-to-"no cache"
/// behaviour — comes from <see cref="FileProviderJsonCache{TEntry}"/>; only the payload record and
/// the <c>release-cache-</c> file-name prefix are this type's own.
/// </summary>
public sealed class FileProviderReleaseCache(IEnvironmentPaths paths)
    : FileProviderJsonCache<ProviderReleaseCacheEntry>(paths, "release-cache-"), IProviderReleaseCache;
