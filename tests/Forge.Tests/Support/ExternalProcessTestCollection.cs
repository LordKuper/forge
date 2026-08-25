namespace Forge.Tests.Support;

/// <summary>External-process tests already share one collection to serialize their process and file
/// operations. Keep that collection isolated from every other collection as well: hosted Windows
/// runners can otherwise starve a newly spawned process long enough to turn readiness checks into
/// unrelated timing failures.</summary>
[CollectionDefinition("External process tests", DisableParallelization = true)]
public sealed class ExternalProcessTestGroup;
