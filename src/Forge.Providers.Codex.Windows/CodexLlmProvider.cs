using System.Text.Json;
using Forge.Application;
using Forge.Domain;

namespace Forge.Providers.Codex;

/// <summary>
/// The Codex CLI integration (ADR 0008): every Codex-specific path, install/update command, and
/// event shape lives here, never in the neutral core.
/// </summary>
public sealed class CodexLlmProvider(
    IEnvironmentPaths paths,
    IProcessRunner processRunner,
    IProviderReleaseSource releaseSource,
    IProviderReleaseCache releaseCache,
    IProviderDefaultModelCache defaultModelCache,
    IProviderInstallLock installLock,
    IClock clock,
    TimeSpan? versionProbeTimeout = null,
    TimeSpan? installTimeout = null,
    TimeSpan? installLockTimeout = null,
    TimeSpan? authenticationProbeTimeout = null,
    TimeSpan? defaultModelProbeTimeout = null) : ILlmProvider
{
    public static readonly ProviderId Codex = new("codex");

    /// <summary>Codex authenticates through its own local credential store (`codex login
    /// status`), not an environment variable Forge reads or writes today — so this adapter's
    /// authentication-variable allowlist category (ADR 0006) is currently empty rather than
    /// guessing an unverified vendor variable name.</summary>
    private static readonly IReadOnlyList<string> AuthenticationVariableNames = [];

    /// <summary>The nested item subtype `codex exec --json` reports for a shell command it ran.</summary>
    private const string CommandExecutionItemType = "command_execution";

    /// <summary>The nested item subtype `codex exec --json` reports for a file it created, modified,
    /// or deleted.</summary>
    private const string FileChangeItemType = "file_change";

    /// <summary>Nested item subtypes that are real, recognized provider content but not tool calls:
    /// ordinary model narration. They must produce neither a tool-call row nor an unmapped-drift
    /// increment -- a normal Codex run emits several of them, so counting them as drift would make
    /// <see cref="ProviderRunResult.UnmappedItemCount"/> non-zero on every healthy run (ADR 0060).
    /// `reasoning` is allowlisted defensively: it is a documented sibling of `agent_message` and is
    /// unambiguously narration, even though the capture this mapping was built from did not contain
    /// one.</summary>
    private static readonly HashSet<string> NonToolCallItemTypes =
        new(StringComparer.Ordinal) { "agent_message", "reasoning" };

    /// <summary>A Forge-owned working directory for probes that must not pick up a project-local
    /// vendor config file (ADR 0008: "from a Forge-owned probe directory").</summary>
    private readonly string probeDirectory = FileProviderReleaseCache.ProviderStateDirectory(paths);

    /// <summary>
    /// The fully-qualified in-box Windows PowerShell path, never a bare `powershell.exe` (ADR
    /// 0002): a bare name is resolved through `CreateProcess`'s search order, which checks the
    /// calling image's own directory and the current directory before `System32`.
    /// </summary>
    private static readonly string PowerShellExecutable = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    private readonly ProviderInstallSpec spec = new(
        ExecutablePath: Path.Combine(
            paths.LocalApplicationData,
            "Programs",
            "OpenAI",
            "Codex",
            "bin",
            "codex.exe"),
        InstallExecutable: PowerShellExecutable,
        InstallArguments:
        [
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
            "$env:CODEX_NON_INTERACTIVE = '1'; irm https://chatgpt.com/codex/install.ps1 | iex",
        ],
        // No documented standalone update subcommand; the installer script itself compares the
        // installed version and is safe to rerun (see ADR 0002).
        UpdateArguments: null,
        MinimumVersion: null);

    public ProviderId Id => Codex;

    /// <summary>The value <see cref="DefaultModel"/> reports before any successful probe (ADR 0063).
    /// Non-empty because `execution-profile.schema.json` requires `model` to have `minLength: 1`, and
    /// worded to read correctly wherever a frozen profile is displayed: a sprint frozen in this state
    /// really does run on whatever the user's own Codex configuration resolves. Never sent as
    /// `-m`.</summary>
    private const string UnresolvedModel = "vendor-default";

    /// <summary>The placeholder every Forge release up to v0.84.1 hardcoded as this adapter's
    /// <see cref="DefaultModel"/> and froze into every Codex sprint. Codex 0.149.1 rejects it
    /// outright (`400 invalid_request_error, "The 'gpt-5' model is not supported"`), so a sprint
    /// frozen before v0.85.0 must not have it sent on its next attempt — that would turn a value that
    /// was merely inaccurate into one that fails the run. It is suppressed exactly like
    /// <see cref="UnresolvedModel"/>: such a sprint keeps running on the user's own configured model,
    /// which is what it has always actually done (ADR 0063).</summary>
    private const string RetiredPlaceholderModel = "gpt-5";

    /// <summary>The property path to the config-resolved model in `codex doctor --json`; see
    /// <see cref="ParseDefaultModel"/>.</summary>
    private static readonly IReadOnlyList<string> DefaultModelPath = ["checks", "config.load", "details", "model"];

    /// <summary>Assigned only by a SUCCESSFUL <see cref="RefreshDefaultModelAsync"/>, so a transient
    /// probe failure keeps the last known-good value for the process lifetime instead of regressing
    /// to <see cref="UnresolvedModel"/>; retry cadence is the cache's job, not this field's.</summary>
    private volatile string? resolvedDefaultModel;

    /// <summary>The model Codex itself reports it would use for a run started right now, resolved
    /// from the user's own configuration by <see cref="RefreshDefaultModelAsync"/> (ADR 0063), or
    /// <see cref="UnresolvedModel"/> until one such probe has succeeded.</summary>
    public string DefaultModel => resolvedDefaultModel ?? UnresolvedModel;

    public async Task<ProviderStatus> DiscoverAsync(bool bypassReleaseCache, CancellationToken cancellationToken)
    {
        ProviderStatus status = await ProviderInstallation.DiscoverAsync(
            Id,
            spec,
            processRunner,
            releaseSource,
            releaseCache,
            clock,
            bypassReleaseCache,
            versionProbeTimeout ?? ProviderInstallation.DefaultVersionProbeTimeout,
            cancellationToken).ConfigureAwait(false);
        await RefreshDefaultModelAsync(bypassReleaseCache, cancellationToken).ConfigureAwait(false);
        return status;
    }

    public async Task<ProviderStatus> InstallOrUpdateAsync(
        bool bypassReleaseCache, CancellationToken cancellationToken)
    {
        ProviderStatus status = await ProviderInstallation.InstallOrUpdateAsync(
            Id,
            spec,
            processRunner,
            releaseSource,
            releaseCache,
            installLock,
            clock,
            bypassReleaseCache,
            versionProbeTimeout ?? ProviderInstallation.DefaultVersionProbeTimeout,
            installTimeout ?? ProviderInstallation.DefaultInstallTimeout,
            installLockTimeout ?? ProviderInstallation.DefaultInstallLockTimeout,
            cancellationToken).ConfigureAwait(false);
        await RefreshDefaultModelAsync(bypassReleaseCache, cancellationToken).ConfigureAwait(false);
        return status;
    }

    /// <summary>
    /// Resolves <see cref="DefaultModel"/> from Codex's own `doctor --json` diagnostic (ADR 0063),
    /// throttled on the same 24h/1h cadence as the release check. Called from
    /// <see cref="DiscoverAsync"/> and <see cref="InstallOrUpdateAsync"/> and forwarding their own
    /// bypass flag, so resolution rides the provider-capability pass that already runs before any
    /// sprint can be created — no new call site, and <see cref="DefaultModel"/> stays a synchronous
    /// property.
    ///
    /// The child environment is the same minimal one <see cref="RunAsync"/> builds, deliberately: the
    /// probe must resolve the model under exactly the environment an attempt will run under, or the
    /// answer would describe a run that never happens. Never throws and never reports failure — a
    /// failed probe simply leaves the previous value in place.
    /// </summary>
    public async Task RefreshDefaultModelAsync(bool bypassCache, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(probeDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }

        string? executable = await ResolveExecutableAsync(cancellationToken).ConfigureAwait(false);
        string? model = await ProviderInstallation.ResolveDefaultModelAsync(
            Id,
            executable,
            processRunner,
            ["doctor", "--json"],
            probeDirectory,
            defaultModelCache,
            clock,
            bypassCache,
            defaultModelProbeTimeout ?? ProviderInstallation.DefaultModelProbeTimeout,
            ParseDefaultModel,
            cancellationToken,
            ProviderEnvironmentPolicy.BuildMinimalEnvironment(AuthenticationVariableNames)).ConfigureAwait(false);
        if (model is not null)
        {
            resolvedDefaultModel = model;
        }
    }

    /// <summary>
    /// Reads `checks["config.load"].details.model` from `codex doctor --json`, the one place Codex
    /// 0.149.1 reports the CONFIG-RESOLVED model — the model an attempt started now would actually
    /// use, including a `model = "..."` the user set in their own `~/.codex/config.toml`. Verified
    /// against `tests/Forge.Tests/Unit/fixtures/providers/codex-doctor.json`, a real captured run.
    ///
    /// `codex debug models` was rejected as the source: it is a generic catalog of what the release
    /// serves, not what this machine resolves, and it loses to the user's own configuration (ADR
    /// 0063).
    ///
    /// Every step is a shape check. A vendor JSON surprise at any nesting level — invalid JSON, a
    /// missing or non-object `checks`/`config.load`/`details`, a missing or non-string `model` — is a
    /// failed probe, never an exception out of a routine provider check.
    /// </summary>
    private static string? ParseDefaultModel(ProcessResult result)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
            JsonElement element = document.RootElement;
            foreach (string property in DefaultModelPath)
            {
                if (element.ValueKind != JsonValueKind.Object ||
                    !element.TryGetProperty(property, out JsonElement child))
                {
                    return null;
                }

                element = child;
            }

            return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(spec.ExecutablePath) ? spec.ExecutablePath : null);

    /// <summary>`codex login status` is documented to be scriptable by exit code alone: 0 means
    /// authenticated, 1 means not — no output parsing needed or attempted.</summary>
    public async Task<ProviderAuthenticationStatus> CheckAuthenticationAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(probeDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return ProviderAuthenticationStatus.CheckFailed;
        }

        string? executable = await ResolveExecutableAsync(cancellationToken).ConfigureAwait(false);
        return await ProviderInstallation.CheckAuthenticationAsync(
            executable,
            processRunner,
            ["login", "status"],
            probeDirectory,
            authenticationProbeTimeout ?? ProviderInstallation.DefaultAuthenticationProbeTimeout,
            result => result.ExitCode == 0 ? ProviderAuthenticationStatus.Ready : ProviderAuthenticationStatus.Required,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The effort levels every model in Codex 0.149.1's own catalog accepts, lowest to
    /// highest. `codex exec` itself validates nothing — an unrecognized `model_reasoning_effort`
    /// reaches the run header verbatim and fails at the API — so this set comes from `codex debug
    /// models`, where each entry lists its `supported_reasoning_levels`. `low`/`medium`/`high`/`xhigh`
    /// are the levels common to all of them; `max` and `ultra` exist in the wire enum but only some
    /// models offer them, so a profile frozen above `xhigh` clamps down to it rather than risking a
    /// rejection. `none` and `minimal` are in the enum but offered by no catalogued model, and clamp
    /// up to `low`.
    ///
    /// This set stays model-INDEPENDENT even though ADR 0063 now resolves which model a run will use,
    /// and that is a deliberate non-goal rather than an oversight: widening the accepted set per
    /// resolved model would make the effort a sprint runs at depend on a value resolved after the
    /// sprint's profile was frozen, which is the class of defect ADR 0062 exists to remove. The
    /// common-denominator set is correct for every model Codex serves; per-model widening belongs
    /// with real per-project model selection, not here.</summary>
    private static readonly IReadOnlyList<string> SupportedEffortLevels = ["low", "medium", "high", "xhigh"];

    /// <summary>
    /// Event shape per `developers.openai.com/codex` (`type`: `thread.started`, `turn.started`,
    /// `turn.completed`, `turn.failed`, `item.*`). Item subtypes are documented only in prose, so
    /// text extraction stays conservative rather than guessing field names.
    ///
    /// ADR 0062: the frozen profile's effort is applied through `-c model_reasoning_effort=&lt;level&gt;`,
    /// verified against Codex 0.149.1 (the value reaches the run header's `reasoning effort:` line).
    ///
    /// ADR 0063: <paramref name="model"/> is now applied through `-m`, replacing this adapter's
    /// earlier decision to send no model flag at all. That decision existed because
    /// <see cref="DefaultModel"/> was a hardcoded slug Codex rejects; it is now resolved from the
    /// user's own Codex configuration, so the value being sent is the one Codex would have resolved
    /// anyway — sending it explicitly is what makes the recorded profile a fact rather than a
    /// prediction. Two values are deliberately suppressed instead: <see cref="UnresolvedModel"/> (no
    /// probe has succeeded, so there is nothing true to send) and
    /// <see cref="RetiredPlaceholderModel"/> (frozen by a pre-v0.85.0 release and rejected by Codex).
    /// Either one degrades to exactly the pre-ADR-0063 command line, leaving the run on the model the
    /// user's own configuration resolves.
    /// </summary>
    public async Task<ProviderRunResult> RunAsync(
        string prompt,
        string workingDirectory,
        string? model,
        string? effort,
        CancellationToken cancellationToken,
        Func<AttemptActivityKind, CancellationToken, Task>? onActivity = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(prompt);
        string? executable = await ResolveExecutableAsync(cancellationToken).ConfigureAwait(false);
        // The prompt travels on stdin, never a command-line argument (ADR 0006): `codex exec
        // --json` has no positional prompt argument at all (ADR 0002).
        List<string> arguments = ["exec", "--json"];
        if (ProviderInstallation.NormalizeModelName(model) is { } sendableModel &&
            sendableModel is not (UnresolvedModel or RetiredPlaceholderModel))
        {
            arguments.Add("-m");
            arguments.Add(sendableModel);
        }

        if (ProviderEffortLevels.Resolve(effort, SupportedEffortLevels) is { } resolvedEffort)
        {
            arguments.Add("-c");
            arguments.Add($"model_reasoning_effort={resolvedEffort}");
        }

        return await ProviderExecution.RunAsync(
            executable,
            processRunner,
            arguments,
            prompt,
            workingDirectory,
            ProviderEnvironmentPolicy.BuildMinimalEnvironment(AuthenticationVariableNames),
            Classify,
            _ => null,
            cancellationToken,
            onActivity,
            ExtractToolCall,
            ExtractUsage).ConfigureAwait(false);
    }

    /// <summary>
    /// ADR 0061. Reads the token accounting `codex exec --json` puts on its terminal `turn.completed`
    /// event, verified against the last line of the same real capture ADR 0060's mapping was built
    /// from (`tests/Forge.Tests/Unit/fixtures/providers/codex-exec-json-tool-calls.jsonl`):
    /// `{"type":"turn.completed","usage":{"input_tokens":88641,...,"output_tokens":544,...}}`.
    ///
    /// Only `input_tokens` and `output_tokens` are mapped. Codex's usage object carries NO
    /// context-window field of any kind, so <see cref="ProviderUsage.ContextWindow"/> is
    /// unconditionally null for this provider -- reported honestly as absent rather than filled in from
    /// a per-model table Forge would have to guess and then keep current. Codex's cache counters
    /// (`cached_input_tokens`, `cache_write_input_tokens`) are likewise left unmapped for now: their
    /// relationship to Claude's own two cache fields is not something one capture per vendor
    /// establishes, and inventing an equivalence would be exactly the guess this slice refuses to make.
    ///
    /// `turn.failed` reaches this method too (<see cref="Classify"/> calls both terminal), and falls
    /// through the type check to null: a failed turn's work never reaches the integration branch, so
    /// nothing about it is ever recorded.
    /// </summary>
    private static ProviderUsage? ExtractUsage(JsonElement root)
    {
        if (TypeOf(root) != "turn.completed" ||
            !root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new(
            NonNegativeInt32(usage, "input_tokens"),
            NonNegativeInt32(usage, "output_tokens"),
            CacheReadTokens: null,
            CacheCreationTokens: null,
            ContextWindow: null);
    }

    /// <summary>A vendor number that is missing, non-numeric, fractional, out of <see cref="int"/>
    /// range, or negative is treated as "not reported" rather than coerced: the durable contract
    /// declares every token count a non-negative integer, and a value that is not one is not a
    /// smaller/clamped version of the truth.</summary>
    private static int? NonNegativeInt32(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out int number) && number >= 0
            ? number
            : null;

    /// <summary>
    /// ADR 0060. Maps one already-`ToolUse`-classified `item.started`/`item.completed` line onto the
    /// neutral tool-call contract. Built strictly from a real recorded `codex exec --json` stream
    /// (`tests/Forge.Tests/Unit/fixtures/providers/codex-exec-json-tool-calls.jsonl`, captured from
    /// Codex CLI 0.149.1): the wrapper's `type` is the lifecycle marker and the actual subtype is
    /// nested one level deeper, at `item.type`. Subtypes that appear in vendor documentation but in no
    /// real capture (`mcp_tool_call`, `web_search`, `patch_apply`) are deliberately left unmapped
    /// rather than guessed -- they fall through to <see cref="ProviderToolCallExtraction.Unmapped"/>,
    /// which is exactly the drift signal that would tell us a capture is now worth taking.
    ///
    /// Reads only the specific named fields below and never enumerates the item generically. In
    /// particular it never reads `command` or `aggregated_output`, which routinely contain secrets
    /// (an `Authorization:` header, an inline environment assignment, whatever the command printed)
    /// and which ADR 0006 forbids persisting in any form. A command is therefore recorded as the bare
    /// fact that one ran, plus its exit code and success -- there is no safe "which command" summary
    /// short of the banned text itself, so <c>RawTarget</c> is unconditionally null for one.
    /// </summary>
    private static ProviderToolCallExtraction ExtractToolCall(JsonElement root)
    {
        string wrapperType = TypeOf(root);
        if (wrapperType is not ("item.started" or "item.completed"))
        {
            return ProviderToolCallExtraction.Unmapped;
        }

        if (!root.TryGetProperty("item", out JsonElement item) || item.ValueKind != JsonValueKind.Object)
        {
            return ProviderToolCallExtraction.Unmapped;
        }

        string itemType = TypeOf(item);
        if (NonToolCallItemTypes.Contains(itemType))
        {
            return ProviderToolCallExtraction.Ignored;
        }

        bool isCompletion = wrapperType == "item.completed";
        string? correlationId = item.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
        return itemType switch
        {
            CommandExecutionItemType => ProviderToolCallExtraction.Of(
                [
                    new ProviderToolCallCandidate(
                        ProviderToolCallKinds.Command,
                        RawTarget: null,
                        correlationId,
                        isCompletion,
                        isCompletion ? ExitCodeOf(item) : null,
                        isCompletion ? SucceededOf(item) : null),
                ]),
            FileChangeItemType => ExtractFileChanges(item, correlationId, isCompletion),
            // Carries the id, so the core counts this unrecognized ITEM once even though Codex
            // describes it on both an `item.started` and an `item.completed` line.
            _ => ProviderToolCallExtraction.UnmappedItem(correlationId),
        };
    }

    /// <summary>`file_change` carries `changes` as an ARRAY. Every real capture so far held exactly
    /// one entry, but the shape allows more, so each entry becomes its own candidate sharing the
    /// item's correlation id (and therefore its measured duration) -- an entry past the first is never
    /// silently dropped. Paths are handed back verbatim; relativizing, safety-checking, and redacting
    /// them is core policy, not this adapter's (ADR 0008). `kind` (`"update"`, ...) is deliberately
    /// NOT read: one capture cannot responsibly define a change-kind vocabulary.</summary>
    private static ProviderToolCallExtraction ExtractFileChanges(
        JsonElement item, string? correlationId, bool isCompletion)
    {
        if (!item.TryGetProperty("changes", out JsonElement changes) || changes.ValueKind != JsonValueKind.Array)
        {
            return ProviderToolCallExtraction.UnmappedItem(correlationId);
        }

        List<ProviderToolCallCandidate> candidates = [];
        foreach (JsonElement change in changes.EnumerateArray())
        {
            if (change.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? path = change.TryGetProperty("path", out JsonElement pathValue) &&
                pathValue.ValueKind == JsonValueKind.String
                ? pathValue.GetString()
                : null;
            candidates.Add(new(
                ProviderToolCallKinds.Edit,
                path,
                correlationId,
                isCompletion,
                ExitCode: null,
                Succeeded: null));
        }

        // An empty or entirely malformed `changes` array is a shape this mapping does not actually
        // cover, so `Of` reports it as drift rather than as a recognized item with nothing in it --
        // still as one identifiable item, so its start and completion lines count once between them.
        return ProviderToolCallExtraction.Of(candidates, correlationId);
    }

    private static int? ExitCodeOf(JsonElement item) =>
        item.TryGetProperty("exit_code", out JsonElement exitCode) && exitCode.ValueKind == JsonValueKind.Number &&
            exitCode.TryGetInt32(out int value)
            ? value
            : null;

    /// <summary>Codex reports `status` (`in_progress`/`completed`/...) beside `exit_code`. Only a
    /// `completed` status carries a decidable outcome: zero is success, anything else is failure.
    /// Every other status -- including a `completed` item that somehow reported no exit code at all --
    /// stays <see langword="null"/> ("unknown") rather than being guessed either way.</summary>
    private static bool? SucceededOf(JsonElement item)
    {
        string? status = item.TryGetProperty("status", out JsonElement statusValue) &&
            statusValue.ValueKind == JsonValueKind.String
            ? statusValue.GetString()
            : null;
        return status == "completed" && ExitCodeOf(item) is { } exitCode ? exitCode == 0 : null;
    }

    /// <summary>
    /// Only `turn.completed`/`turn.failed` are genuinely terminal; `turn.started` is a lifecycle
    /// marker mid-run. An earlier version of this classifier matched any `turn.*` type, which
    /// misclassified `turn.started` as a terminal result and would have broken the "exactly one
    /// terminal result" uniqueness check (ADR 0006) on every normal run.
    /// </summary>
    private static ProviderEventKind Classify(JsonElement root)
    {
        string type = TypeOf(root);
        if (type is "turn.completed" or "turn.failed")
        {
            return ProviderEventKind.Result;
        }

        return type.StartsWith("item.", StringComparison.Ordinal)
            ? ProviderEventKind.ToolUse
            : ProviderEventKind.Unknown;
    }

    private static string TypeOf(JsonElement root) =>
        root.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String
            ? type.GetString() ?? string.Empty
            : string.Empty;
}
