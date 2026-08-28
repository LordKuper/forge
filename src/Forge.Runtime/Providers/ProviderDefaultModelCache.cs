using Forge.Application;

namespace Forge.Providers;

/// <summary>
/// The default-model probe's cache (ADR 0063). A distinct payload record and a distinct
/// <c>default-model-</c> file-name prefix over the shared file mechanics in
/// <see cref="FileProviderJsonCache{TEntry}"/> — the two caches' values carry different meanings, and
/// only how they are stored is common.
/// </summary>
public sealed class FileProviderDefaultModelCache(IEnvironmentPaths paths)
    : FileProviderJsonCache<ProviderDefaultModelCacheEntry>(paths, "default-model-"), IProviderDefaultModelCache;
