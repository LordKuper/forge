using System.Collections.Concurrent;
using System.Text.Json;
using Forge.Configuration;

namespace Forge.Application;

/// <summary>
/// Plan section 6.1's user-scoped project catalog row. Keyed by the project's own durable
/// <see cref="ProjectId"/> -- never a second, catalog-local identity -- so an entry only exists for
/// an already-initialized project (<see cref="ProjectCatalogStore.AddAsync"/>). <see cref="Alias"/>
/// is local display text only; it never modifies the project's own manifest.
/// <see cref="TimelineReadWatermarks"/> is Slice 6's per-sprint "last read timeline position" (plan
/// section 4.3's unread tracking) and <see cref="SprintDrafts"/> its minimal unsent-draft
/// preservation (the sprint workspace's rewind-reason input, the one substantial new free-text field
/// this slice adds) -- both keyed by the sprint id's <c>"D"</c> string form, matching how every other
/// catalog id here is already stored on the wire. Every other typed input this slice renders
/// (gate/confirm/test-work justification) is a one-shot decision entered and submitted in a single
/// dialog turn, not a draft worth surviving a restart. <see cref="MessageDrafts"/> (ADR 0054,
/// post-release timeline gap closure) is a PARALLEL field for the message composer's own draft, not
/// a reuse of <see cref="SprintDrafts"/> -- that field is specific to the rewind-reason input (one
/// draft slot per sprint already), and a sprint can have an in-progress rewind reason and an
/// in-progress message at the same time. <see cref="SprintListCollapsed"/> (plan 12.1 final-sweep
/// gap 1) is the sidebar's per-project active-sprint-list disclosure state -- unlike the
/// whole-sidebar rail (a Desktop-instance-level preference in user configuration), this is scoped to
/// one project's catalog row, so it lives here rather than as a configuration key; defaults to
/// <see langword="false"/> (expanded) so an entry added before this field existed renders exactly as
/// it always has. <see cref="SprintScrollPositions"/> (gap 2) is the sprint workspace's last scroll
/// offset per sprint, keyed the same "D" way as every other per-sprint dictionary here -- restores
/// that in-session-only behavior across an app restart too.
/// </summary>
public sealed record ProjectCatalogEntry(
    Guid ProjectId,
    string Root,
    string? Alias,
    DateTimeOffset LastOpenedAt,
    Guid? LastSelectedSprintId,
    string? LastRoute,
    IReadOnlyDictionary<string, long>? TimelineReadWatermarks = null,
    IReadOnlyDictionary<string, string>? SprintDrafts = null,
    IReadOnlyDictionary<string, string>? MessageDrafts = null,
    bool SprintListCollapsed = false,
    IReadOnlyDictionary<string, double>? SprintScrollPositions = null);

public sealed record ProjectCatalogResult(bool Succeeded, ProjectCatalogEntry? Entry, string DiagnosticCode)
{
    public static ProjectCatalogResult Fail(string diagnosticCode) => new(false, null, diagnosticCode);
}

/// <summary>Plan section 6.1's catalog listing. Carries a diagnostic alongside the entries (round 1
/// review of PR #97) so a corrupt or unreadable `catalog.json` reports
/// <see cref="DiagnosticCodes.ProjectCatalogUnreadable"/> cleanly through `forge project list`/
/// `forge workspace summary` instead of throwing an unhandled exception out of either command.
/// <see cref="Entries"/> is always empty when <see cref="DiagnosticCode"/> is not
/// <see cref="DiagnosticCodes.None"/>.</summary>
public sealed record ProjectCatalogListing(IReadOnlyList<ProjectCatalogEntry> Entries, string DiagnosticCode);

/// <summary>
/// User-scoped, Desktop-installation-local persistence for <see cref="ProjectCatalogEntry"/> rows.
/// Stored beside the existing user configuration file (<see cref="ConfigurationStoreFactory.UserPath"/>'s
/// own instance-scoped directory under <see cref="IEnvironmentPaths.LocalApplicationData"/>) --
/// never inside any project's own `.forge/` directory (ADR 0043/0049; plan section 6.1: "outside
/// project state"). Every operation here reads or writes only this one file: adding, removing,
/// relinking, aliasing, or selecting a catalog entry never touches repository data.
/// </summary>
public sealed class ProjectCatalogStore(
    IEnvironmentPaths paths,
    IClock clock,
    ProjectRootResolver rootResolver,
    IConfigurationRegistry registry)
{
    public const string ContractVersion = "1.0.0";
    public const int MaxAliasLength = 200;
    public const int MaxRouteLength = 200;

    /// <summary>Same bound as <see cref="SprintScheduler.MaxSupersessionInstructionLength"/> and the
    /// rewind reason itself (ADR 0048): a draft is the not-yet-submitted form of that same bounded
    /// text, so it can never exceed what the eventual submission would allow anyway.</summary>
    public const int MaxDraftLength = SprintScheduler.MaxSupersessionInstructionLength;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    /// <summary>One lock per catalog path, matching <see cref="FileSprintEventLog"/>'s own per-path
    /// critical-section idiom (its static <c>Locks</c> dictionary). <see cref="ProjectCatalogStore"/>
    /// is registered as a DI singleton and every mutating method here does a plain
    /// read-modify-write with nothing else serializing the pair, so two overlapping calls (e.g. a
    /// `SelectAsync` on every navigation racing an `AddAsync`/`SetAliasAsync`/`RemoveAsync`) could
    /// otherwise interleave into a lost update (round 1 review of PR #97). Keyed by path rather than
    /// a single static gate so unrelated <see cref="IEnvironmentPaths"/> instances (distinct test
    /// environments in the same process) never contend with each other.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    public static string CatalogPath(IEnvironmentPaths environmentPaths)
    {
        ArgumentNullException.ThrowIfNull(environmentPaths);
        return Path.Combine(
            environmentPaths.LocalApplicationData, "Forge", environmentPaths.InstanceId, "catalog.json");
    }

    public async Task<ProjectCatalogListing> ListAsync(CancellationToken cancellationToken)
    {
        CatalogReadResult read = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
        return new(read.Persisted.Entries, read.DiagnosticCode);
    }

    /// <summary>Adds a catalog row for an already-initialized project, anchored on its manifest's
    /// own <c>project_id</c> (plan section 6.1: "stable project ID when initialized"). Never
    /// initializes a project itself -- a caller targeting an uninitialized root is told to run
    /// `forge init` first rather than this silently doing it.</summary>
    public async Task<ProjectCatalogResult> AddAsync(string? root, CancellationToken cancellationToken)
    {
        ProjectRootStatus status = await rootResolver.ResolveAsync(root, cancellationToken).ConfigureAwait(false);
        if (!status.Initialized)
        {
            return ProjectCatalogResult.Fail(
                status.DiagnosticCode == DiagnosticCodes.None
                    ? DiagnosticCodes.ProjectNotInitialized
                    : status.DiagnosticCode);
        }

        Guid projectId = await ProjectIdentity.ReadProjectIdAsync(status.Root, registry, cancellationToken)
            .ConfigureAwait(false);
        string path = CatalogPath(paths);
        SemaphoreSlim gate = Locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CatalogReadResult read = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
            if (read.DiagnosticCode != DiagnosticCodes.None)
            {
                return ProjectCatalogResult.Fail(read.DiagnosticCode);
            }

            Persisted persisted = read.Persisted;
            if (persisted.Entries.Any(entry => entry.ProjectId == projectId))
            {
                return ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogEntryExists);
            }

            ProjectCatalogEntry created = new(projectId, status.Root, null, clock.UtcNow, null, null);
            persisted.Entries.Add(created);
            await WriteAsync(persisted, cancellationToken).ConfigureAwait(false);
            return new(true, created, DiagnosticCodes.None);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Removes the catalog row only. Never deletes the repository or its `.forge/`
    /// directory (plan section 6.1/12.1).</summary>
    public async Task<ProjectCatalogResult> RemoveAsync(Guid projectId, CancellationToken cancellationToken)
    {
        string path = CatalogPath(paths);
        SemaphoreSlim gate = Locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CatalogReadResult read = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
            if (read.DiagnosticCode != DiagnosticCodes.None)
            {
                return ProjectCatalogResult.Fail(read.DiagnosticCode);
            }

            Persisted persisted = read.Persisted;
            ProjectCatalogEntry? existing = persisted.Entries.FirstOrDefault(entry => entry.ProjectId == projectId);
            if (existing is null)
            {
                return ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogEntryNotFound);
            }

            persisted.Entries.Remove(existing);
            await WriteAsync(persisted, cancellationToken).ConfigureAwait(false);
            return new(true, existing, DiagnosticCodes.None);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Relinks a moved project: verifies the manifest's own `project_id` at
    /// <paramref name="newRoot"/> matches <paramref name="projectId"/> before accepting the new root
    /// (plan section 6.1) -- never trusts the caller's claim that the two refer to the same
    /// project.</summary>
    public async Task<ProjectCatalogResult> RelinkAsync(
        Guid projectId, string? newRoot, CancellationToken cancellationToken)
    {
        string path = CatalogPath(paths);
        SemaphoreSlim gate = Locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CatalogReadResult read = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
            if (read.DiagnosticCode != DiagnosticCodes.None)
            {
                return ProjectCatalogResult.Fail(read.DiagnosticCode);
            }

            Persisted persisted = read.Persisted;
            int index = persisted.Entries.FindIndex(entry => entry.ProjectId == projectId);
            if (index < 0)
            {
                return ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogEntryNotFound);
            }

            ProjectRootStatus status =
                await rootResolver.ResolveAsync(newRoot, cancellationToken).ConfigureAwait(false);
            if (!status.Initialized)
            {
                return ProjectCatalogResult.Fail(
                    status.DiagnosticCode == DiagnosticCodes.None
                        ? DiagnosticCodes.ProjectNotInitialized
                        : status.DiagnosticCode);
            }

            Guid actualProjectId = await ProjectIdentity.ReadProjectIdAsync(status.Root, registry, cancellationToken)
                .ConfigureAwait(false);
            if (actualProjectId != projectId)
            {
                return ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogRelinkMismatch);
            }

            ProjectCatalogEntry updated =
                persisted.Entries[index] with { Root = status.Root, LastOpenedAt = clock.UtcNow };
            persisted.Entries[index] = updated;
            await WriteAsync(persisted, cancellationToken).ConfigureAwait(false);
            return new(true, updated, DiagnosticCodes.None);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Sets or clears (empty/whitespace input) the local display alias. Never touches the
    /// project's own manifest (plan section 4.2: "a local alias belongs to the user project catalog
    /// and does not modify the repository manifest").</summary>
    public async Task<ProjectCatalogResult> SetAliasAsync(
        Guid projectId, string? alias, CancellationToken cancellationToken)
    {
        if (alias is { Length: > MaxAliasLength })
        {
            return ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogAliasTooLong);
        }

        string path = CatalogPath(paths);
        SemaphoreSlim gate = Locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CatalogReadResult read = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
            if (read.DiagnosticCode != DiagnosticCodes.None)
            {
                return ProjectCatalogResult.Fail(read.DiagnosticCode);
            }

            Persisted persisted = read.Persisted;
            int index = persisted.Entries.FindIndex(entry => entry.ProjectId == projectId);
            if (index < 0)
            {
                return ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogEntryNotFound);
            }

            ProjectCatalogEntry updated = persisted.Entries[index] with
            {
                Alias = string.IsNullOrWhiteSpace(alias) ? null : alias,
            };
            persisted.Entries[index] = updated;
            await WriteAsync(persisted, cancellationToken).ConfigureAwait(false);
            return new(true, updated, DiagnosticCodes.None);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Records the last selected sprint/route for a catalog entry and bumps
    /// <see cref="ProjectCatalogEntry.LastOpenedAt"/> (plan section 11 Slice 4 item 3) -- the
    /// plumbing a navigation shell calls on every selection so the last valid route survives a
    /// restart (plan section 12.1). <paramref name="sprintId"/> is <see langword="null"/> for a
    /// project-level route (no sprint selected).</summary>
    public async Task<ProjectCatalogResult> SelectAsync(
        Guid projectId, Guid? sprintId, string? route, CancellationToken cancellationToken)
    {
        if (route is { Length: > MaxRouteLength })
        {
            return ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogRouteTooLong);
        }

        string path = CatalogPath(paths);
        SemaphoreSlim gate = Locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CatalogReadResult read = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
            if (read.DiagnosticCode != DiagnosticCodes.None)
            {
                return ProjectCatalogResult.Fail(read.DiagnosticCode);
            }

            Persisted persisted = read.Persisted;
            int index = persisted.Entries.FindIndex(entry => entry.ProjectId == projectId);
            if (index < 0)
            {
                return ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogEntryNotFound);
            }

            ProjectCatalogEntry updated = persisted.Entries[index] with
            {
                LastOpenedAt = clock.UtcNow,
                LastSelectedSprintId = sprintId,
                LastRoute = string.IsNullOrWhiteSpace(route) ? null : route,
            };
            persisted.Entries[index] = updated;
            await WriteAsync(persisted, cancellationToken).ConfigureAwait(false);
            return new(true, updated, DiagnosticCodes.None);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Slice 6's unread-position tracking (plan section 4.3): records the highest
    /// <see cref="SprintTimelineItem"/> occurrence sequence the user has actually seen for one
    /// sprint. A caller only ever advances this forward -- a lower watermark than the entry's current
    /// one is ignored rather than rewinding "read" state, since a stale render racing a later, fresher
    /// one must never mark newer items unread again.</summary>
    public Task<ProjectCatalogResult> SetTimelineWatermarkAsync(
        Guid projectId, Guid sprintId, long watermark, CancellationToken cancellationToken) =>
        MutateEntryAsync(projectId, entry =>
        {
            string key = sprintId.ToString("D");
            Dictionary<string, long> watermarks = entry.TimelineReadWatermarks is { } existing
                ? new(existing, StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
            if (!watermarks.TryGetValue(key, out long current) || watermark > current)
            {
                watermarks[key] = watermark;
            }

            return entry with { TimelineReadWatermarks = watermarks };
        }, cancellationToken);

    /// <summary>Slice 6's minimal unsent-draft preservation (plan section 11 Slice 6 item 4): the
    /// sprint workspace's rewind-reason input text, restored after an app restart. A
    /// <see langword="null"/> or whitespace-only <paramref name="draft"/> clears the entry, matching
    /// <see cref="SetAliasAsync"/>'s own empty-clears convention.</summary>
    public Task<ProjectCatalogResult> SetSprintDraftAsync(
        Guid projectId, Guid sprintId, string? draft, CancellationToken cancellationToken)
    {
        if (draft is { Length: > MaxDraftLength })
        {
            return Task.FromResult(ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogDraftTooLong));
        }

        return MutateEntryAsync(projectId, entry =>
        {
            string key = sprintId.ToString("D");
            Dictionary<string, string> drafts = entry.SprintDrafts is { } existing
                ? new(existing, StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(draft))
            {
                drafts.Remove(key);
            }
            else
            {
                drafts[key] = draft;
            }

            return entry with { SprintDrafts = drafts };
        }, cancellationToken);
    }

    /// <summary>ADR 0054's parallel draft slot for the sprint workspace's message composer (see
    /// <see cref="ProjectCatalogEntry.MessageDrafts"/>'s own remarks for why this is not a reuse of
    /// <see cref="SetSprintDraftAsync"/>) -- same bound and empty-clears convention.</summary>
    public Task<ProjectCatalogResult> SetSprintMessageDraftAsync(
        Guid projectId, Guid sprintId, string? draft, CancellationToken cancellationToken)
    {
        if (draft is { Length: > MaxDraftLength })
        {
            return Task.FromResult(ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogDraftTooLong));
        }

        return MutateEntryAsync(projectId, entry =>
        {
            string key = sprintId.ToString("D");
            Dictionary<string, string> drafts = entry.MessageDrafts is { } existing
                ? new(existing, StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(draft))
            {
                drafts.Remove(key);
            }
            else
            {
                drafts[key] = draft;
            }

            return entry with { MessageDrafts = drafts };
        }, cancellationToken);
    }

    /// <summary>Plan 12.1 final-sweep gap 1: persists the sidebar's per-project active-sprint-list
    /// disclosure state so it survives an app restart, the same local write every other catalog
    /// mutation here already uses -- never a project Host round-trip (the whole-sidebar rail is the
    /// only sibling preference stored in user configuration instead; see
    /// <see cref="ProjectCatalogEntry.SprintListCollapsed"/>'s own remarks for why this one is not).
    /// </summary>
    public Task<ProjectCatalogResult> SetSprintListCollapsedAsync(
        Guid projectId, bool collapsed, CancellationToken cancellationToken) =>
        MutateEntryAsync(projectId, entry => entry with { SprintListCollapsed = collapsed }, cancellationToken);

    /// <summary>Plan 12.1 final-sweep gap 2: persists the sprint workspace's last scroll offset for
    /// one sprint so it survives an app restart -- previously held only in an in-memory field on the
    /// page instance (<c>WorkspaceShellPage.SprintWorkspace.cs</c>'s
    /// <c>Forge.Desktop.Presentation.ScrollPositionPersistCoordinator</c>). PR #105 review findings
    /// 3/4: this method itself is a plain unordered write, same as every sibling mutation here -- the
    /// caller now debounces by elapsed time rather than scroll distance, and sequence-stamps each call
    /// so a stale, late-completing call can never be allowed to overwrite a fresher one that already
    /// landed (see that coordinator's own remarks for where that guarantee actually lives). A
    /// non-positive <paramref name="position"/> clears the entry, matching every other per-sprint
    /// dictionary's own empty-clears convention (there is nothing to restore below the top). NaN/
    /// infinite values are rejected outright -- <see cref="System.Text.Json.JsonSerializer"/> cannot
    /// round-trip either, and no legitimate scroll offset is ever one.</summary>
    public Task<ProjectCatalogResult> SetSprintScrollPositionAsync(
        Guid projectId, Guid sprintId, double position, CancellationToken cancellationToken)
    {
        if (double.IsNaN(position) || double.IsInfinity(position))
        {
            return Task.FromResult(ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogScrollPositionInvalid));
        }

        return MutateEntryAsync(projectId, entry =>
        {
            string key = sprintId.ToString("D");
            Dictionary<string, double> positions = entry.SprintScrollPositions is { } existing
                ? new(existing, StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
            if (position <= 0)
            {
                positions.Remove(key);
            }
            else
            {
                positions[key] = position;
            }

            return entry with { SprintScrollPositions = positions };
        }, cancellationToken);
    }

    /// <summary>Shared read-modify-write shape every mutating method above already applies inline;
    /// factored out only for the two Slice 6 additions above so their own logic is the one-line
    /// transform, not another copy of the lock/read/find/write boilerplate.</summary>
    private async Task<ProjectCatalogResult> MutateEntryAsync(
        Guid projectId,
        Func<ProjectCatalogEntry, ProjectCatalogEntry> mutate,
        CancellationToken cancellationToken)
    {
        string path = CatalogPath(paths);
        SemaphoreSlim gate = Locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CatalogReadResult read = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
            if (read.DiagnosticCode != DiagnosticCodes.None)
            {
                return ProjectCatalogResult.Fail(read.DiagnosticCode);
            }

            Persisted persisted = read.Persisted;
            int index = persisted.Entries.FindIndex(entry => entry.ProjectId == projectId);
            if (index < 0)
            {
                return ProjectCatalogResult.Fail(DiagnosticCodes.ProjectCatalogEntryNotFound);
            }

            ProjectCatalogEntry updated = mutate(persisted.Entries[index]);
            persisted.Entries[index] = updated;
            await WriteAsync(persisted, cancellationToken).ConfigureAwait(false);
            return new(true, updated, DiagnosticCodes.None);
        }
        finally
        {
            gate.Release();
        }
    }

    private readonly record struct CatalogReadResult(Persisted Persisted, string DiagnosticCode);

    /// <summary>Reads `catalog.json`, recovering from its `.previous` sibling (the same convention
    /// <see cref="Forge.Configuration.JsonConfigurationStore"/> already establishes for user
    /// configuration) when the primary file is corrupt, malformed, or carries a `schema_version`
    /// this build does not recognize -- never silently downgrading a newer catalog by dropping the
    /// fields it does not know about (round 1 review of PR #97: matches
    /// <see cref="SprintTimelineCursorCodec.TryDecode"/>'s own "a foreign or future-versioned token
    /// is rejected, never misread" contract). Recovering also rewrites the primary file from the
    /// recovered bytes so the next read no longer needs to fall back. When neither the primary file
    /// nor its `.previous` sibling is usable, reports <see cref="DiagnosticCodes.ProjectCatalogUnreadable"/>
    /// with an empty catalog instead of throwing the underlying parse failure out to a caller.
    /// </summary>
    private async Task<CatalogReadResult> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        string path = CatalogPath(paths);
        if (!File.Exists(path))
        {
            return new(new Persisted(), DiagnosticCodes.None);
        }

        try
        {
            return new(await ReadFileAsync(path, cancellationToken).ConfigureAwait(false), DiagnosticCodes.None);
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            string previousPath = $"{path}.previous";
            if (!File.Exists(previousPath))
            {
                return new(new Persisted(), DiagnosticCodes.ProjectCatalogUnreadable);
            }

            try
            {
                Persisted recovered =
                    await ReadFileAsync(previousPath, cancellationToken).ConfigureAwait(false);
                byte[] contents =
                    await File.ReadAllBytesAsync(previousPath, cancellationToken).ConfigureAwait(false);
                await AtomicConfigurationFile
                    .WriteAsync(path, contents, cancellationToken, retainPrevious: false)
                    .ConfigureAwait(false);
                return new(recovered, DiagnosticCodes.None);
            }
            catch (Exception recoveryError) when (IsRecoverable(recoveryError))
            {
                return new(new Persisted(), DiagnosticCodes.ProjectCatalogUnreadable);
            }
        }
    }

    private static async Task<Persisted> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        Persisted? persisted = await JsonSerializer
            .DeserializeAsync<Persisted>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        persisted ??= new();
        if (persisted.SchemaVersion != ContractVersion)
        {
            // Treated exactly like a parse failure -- caught by ReadCatalogAsync's own recovery
            // path -- rather than silently accepted: System.Text.Json drops any property it does
            // not recognize, so reading a newer catalog as this version and later rewriting it
            // would permanently discard whatever the newer schema added.
            throw new InvalidDataException(
                $"catalog.json schema_version '{persisted.SchemaVersion}' is not the recognized " +
                    $"'{ContractVersion}'.");
        }

        return persisted;
    }

    private static bool IsRecoverable(Exception error) => error is JsonException or IOException or InvalidDataException;

    private async Task WriteAsync(Persisted persisted, CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(persisted, JsonOptions);
        // Reuses the same atomic temp-file-then-replace primitive user configuration already relies
        // on (Forge.Configuration.AtomicConfigurationFile) rather than a second durability mechanism
        // for what is, functionally, another piece of user-scoped local state. retainPrevious keeps
        // the file this write replaces around as `.previous` -- the same recovery convention
        // JsonConfigurationStore relies on -- so ReadCatalogAsync has something to fall back to if a
        // later write is ever left corrupt by a crash mid-write.
        await AtomicConfigurationFile.WriteAsync(CatalogPath(paths), bytes, cancellationToken, retainPrevious: true)
            .ConfigureAwait(false);
    }

    private sealed class Persisted
    {
        public string SchemaVersion { get; set; } = ContractVersion;

        public List<ProjectCatalogEntry> Entries { get; set; } = [];
    }
}
