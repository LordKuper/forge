using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Forge.Application;

/// <summary>
/// Produces the immutable versioned status snapshot and the deterministic recommendation list.
/// Only safe recovery and initialization actions exist before the workflow engine lands.
/// </summary>
public sealed class StatusAdvisor(IClock clock)
{
    public const string ContractVersion = "1.0.0";
    private const int MaximumResults = 5;

    public ProjectStatusSnapshot CreateSnapshot(StartupStatus startup)
    {
        ArgumentNullException.ThrowIfNull(startup);
        long stateVersion = StateVersion(startup.Project);
        return new(
            ContractVersion,
            stateVersion,
            clock.UtcNow,
            new(startup.Project.Root, startup.Project.Initialized),
            startup.State,
            null,
            [],
            [],
            Recommend(startup, stateVersion));
    }

    /// <summary>
    /// The state version advances with every durable project mutation. Initialization is the only
    /// mutation the current stage owns, so the version distinguishes an initialized project root.
    /// </summary>
    public static long StateVersion(ProjectRootStatus project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Initialized ? 1 : 0;
    }

    private static IReadOnlyList<SuggestedAction> Recommend(StartupStatus startup, long stateVersion)
    {
        List<Candidate> candidates = [];
        StartupCheck? failed = startup.Checks.FirstOrDefault(
            check => check.State == StartupCheckState.Failed);
        if (failed is not null)
        {
            string checkId = JsonNamingPolicy.SnakeCaseLower.ConvertName(failed.Id.ToString());
            candidates.Add(new(
                "recover_startup",
                AttentionPriority.StartupBlocked,
                SafetyClass.ConfirmMutation,
                new("startup_check", checkId),
                ["startup.fail_closed", "recovery.available"],
                "RecoverStartup",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["check"] = checkId,
                    ["diagnostic_code"] = failed.DiagnosticCode,
                }));
        }

        // A failed startup leaves recovery as the only safe action, so no mutation is offered.
        if (failed is null && startup.Project is { Exists: true, Initialized: false, Unknown: false })
        {
            candidates.Add(new(
                "initialize_project",
                AttentionPriority.StartupBlocked,
                SafetyClass.ConfirmMutation,
                new("project", startup.Project.Root),
                ["project.root_confirmed", "project.forge_directory_absent"],
                "InitializeProject",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["project_root"] = startup.Project.Root,
                }));
        }

        return
        [
            .. candidates
                .OrderBy(candidate => (int)candidate.Priority)
                .ThenBy(candidate => (int)candidate.Safety)
                .ThenBy(candidate => candidate.ActionId, StringComparer.Ordinal)
                .Take(MaximumResults)
                .Select((candidate, index) => candidate.ToAction(index + 1, stateVersion)),
        ];
    }

    private sealed record Candidate(
        string ActionId,
        AttentionPriority Priority,
        SafetyClass Safety,
        ActionTarget Target,
        IReadOnlyList<string> Preconditions,
        string CommandName,
        IReadOnlyDictionary<string, string> Arguments)
    {
        public SuggestedAction ToAction(int rank, long stateVersion) =>
            new(
                ContractVersion,
                ActionId,
                rank,
                string.Create(CultureInfo.InvariantCulture, $"next.{ActionId}.rationale"),
                Arguments,
                Preconditions,
                Safety,
                Target,
                new(CommandName, Arguments, IdempotencyKey(ActionId, Target, stateVersion)),
                stateVersion,
                Safety == SafetyClass.Read
                    ? StaleBehavior.RefreshThenRead
                    : StaleBehavior.RejectWithoutSideEffect);
    }

    /// <summary>Derives a stable idempotency key so an unchanged snapshot repeats the same action.</summary>
    public static Guid IdempotencyKey(string actionId, ActionTarget target, long stateVersion)
    {
        ArgumentNullException.ThrowIfNull(target);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"{actionId}|{target.Kind}|{target.Id}|{stateVersion}")));
        return new(hash.AsSpan(0, 16));
    }
}
