using Forge.Application;

namespace Forge.Providers;

/// <summary>
/// The model-enumeration probe's cache (ADR 0066). A distinct payload record and a distinct
/// <c>model-catalog-</c> file-name prefix over the shared file mechanics in
/// <see cref="FileProviderJsonCache{TEntry}"/>.
/// </summary>
public sealed class FileProviderModelCatalogCache(IEnvironmentPaths paths)
    : FileProviderJsonCache<ProviderModelCatalogCacheEntry>(paths, "model-catalog-"), IProviderModelCatalogCache;
