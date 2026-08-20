using System.Diagnostics;
using System.Text;
using Forge.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Forge.Infrastructure;

public sealed class PhysicalFileSystem : IFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(path, cancellationToken);

    public Task WriteAllTextAsync(
        string path,
        string contents,
        CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, contents, cancellationToken);
}

public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>Bare UTF-8, no byte-order mark. `Encoding.UTF8` emits a BOM preamble on a
    /// `StreamWriter`'s first write, which would silently prepend three corrupting bytes
    /// (`EF BB BF`) to every prompt sent to a provider's stdin -- exactly the byte-fidelity
    /// boundary this class exists to protect (see <see cref="ReadStreamAsync"/>'s own doc comment
    /// for the read-side half of that same guarantee).</summary>
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public async Task<ProcessResult> RunAsync(
        ProcessRequest request,
        IProcessOutputSink? outputSink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        ProcessStartInfo startInfo = new()
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = request.StandardInput is not null,
            // Without an explicit encoding, redirected output decodes with the OS/console
            // codepage rather than the UTF-8 bytes the child process (always `git` today) actually
            // writes -- silently corrupting non-ASCII content on a machine whose codepage isn't
            // UTF-8, which would then break `GitContextReader`'s byte-identical reproducibility
            // guarantee (ADR 0012).
            StandardOutputEncoding = Utf8WithoutBom,
            StandardErrorEncoding = Utf8WithoutBom,
            StandardInputEncoding = request.StandardInput is not null ? Utf8WithoutBom : null,
            UseShellExecute = false,
        };

        if (request.ReplaceEnvironment)
        {
            // ADR 0006: "Provider children receive a minimal environment assembled by Forge" --
            // `ProcessStartInfo.EnvironmentVariables` is pre-populated from this process's own
            // full environment by default; starting from nothing is what makes the child's
            // environment exactly `request.EnvironmentVariables`, not that plus everything Forge
            // itself happened to inherit.
            startInfo.EnvironmentVariables.Clear();
        }

        if (request.EnvironmentVariables is not null)
        {
            foreach ((string key, string value) in request.EnvironmentVariables)
            {
                startInfo.EnvironmentVariables[key] = value;
            }
        }

        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        process.Start();

        // Read loops are started before stdin is written so a child that begins writing output
        // before it has fully consumed stdin can never deadlock against Forge's own unread pipe
        // buffer -- classic bidirectional-pipe ordering, not specific to any one provider.
        Task<string> output = ReadStreamAsync(process.StandardOutput, outputSink, isError: false);
        Task<string> error = ReadStreamAsync(process.StandardError, outputSink, isError: true);

        try
        {
            if (request.StandardInput is not null)
            {
                try
                {
                    await process.StandardInput.WriteAsync(request.StandardInput.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    // Close() only after a write that actually completed: it flushes synchronously
                    // and uncancellably, which could itself hang against the same full,
                    // nobody-reading pipe that a cancelled write above would indicate. A
                    // cancellation is deliberately NOT caught here -- it falls straight through to
                    // the outer catch's process.Kill(true), which tears down the child (and every
                    // one of its pipes, stdin included) without needing a graceful close at all.
                    process.StandardInput.Close();
                }
                catch (Exception writeError) when (writeError is IOException or ObjectDisposedException)
                {
                    // The child closed or never opened its stdin before consuming the full input --
                    // not Forge's error to raise; the child's own exit code/stderr already reflects
                    // why. The pipe is already broken/torn down, so no close is needed here either.
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between the state check and termination.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await output.ConfigureAwait(false),
            await error.ConfigureAwait(false));
    }

    /// <summary>A generic per-logical-line safety ceiling, independent of any caller's own bound
    /// policy: without it, a child that never emits a newline could grow the line buffer below
    /// without limit, and no caller-owned bound (e.g. `ProviderExecution`'s much smaller
    /// `MaxLineLengthBytes`) would ever get a chance to see and reject it, since that check only
    /// runs on a line this read loop hands off. No legitimate caller today (git, installers,
    /// provider adapters) ever needs a single unterminated line anywhere near this large.</summary>
    private const int MaxBufferedLineChars = 4 * 1024 * 1024;

    /// <summary>
    /// Reads as data arrives in fixed chunks (ADR 0006: "Stdout and stderr are consumed
    /// concurrently as bounded streams") rather than the whole stream at once, notifying
    /// <paramref name="sink"/> once per logical line while separately returning every character
    /// exactly as decoded -- including original line endings and a missing/present trailing
    /// newline. `GitContextReader` hashes this exact returned text into a content digest (ADR
    /// 0012), so a line-splitting read (e.g. <c>ReadLineAsync</c>, which normalizes CRLF/bare CR
    /// to LF and drops a trailing newline) would silently change that digest for any CRLF or
    /// no-final-newline content; only the line-buffer used for sink notification strips `\r`, never
    /// the returned raw text. A line longer than <see cref="MaxBufferedLineChars"/> is forcibly
    /// handed to the sink mid-line (as if it had ended) so a caller-owned per-line bound always
    /// gets evaluated within a bounded amount of buffering, even against a child that never emits
    /// a newline at all. Reads and sink notifications alike use a fixed, unconditional
    /// <see cref="CancellationToken.None"/>, for the same reason the prior buffered
    /// implementation's reads did: a cancellation here must never race the caller's own
    /// kill-then-drain sequence above, which is what actually stops the child (closing these pipes
    /// and unblocking the read with a natural end-of-stream) and needs these same tasks to
    /// complete cleanly afterward, not fault independently mid-drain.
    /// </summary>
    private static async Task<string> ReadStreamAsync(StreamReader reader, IProcessOutputSink? sink, bool isError)
    {
        StringBuilder raw = new();
        StringBuilder line = new();
        char[] chunk = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(chunk.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
        {
            raw.Append(chunk, 0, read);
            for (int index = 0; index < read; index++)
            {
                char current = chunk[index];
                if (current == '\n')
                {
                    await NotifyLineAsync(line, sink, isError).ConfigureAwait(false);
                    line.Clear();
                    continue;
                }

                if (current == '\r')
                {
                    continue;
                }

                line.Append(current);
                if (line.Length >= MaxBufferedLineChars)
                {
                    await NotifyLineAsync(line, sink, isError).ConfigureAwait(false);
                    line.Clear();
                }
            }
        }

        if (line.Length > 0)
        {
            await NotifyLineAsync(line, sink, isError).ConfigureAwait(false);
        }

        return raw.ToString();
    }

    /// <summary>A caller-supplied sink (e.g. a provider's activity-recording callback) must never
    /// be able to break this loop's own pipe draining -- an unhandled fault or an indefinite hang
    /// here would stop stdout/stderr from ever being consumed, which can starve the child's writes
    /// and make even the caller's own cancellation kill-then-drain sequence (which awaits this same
    /// task) hang forever. A bounded wait plus a broad catch means a misbehaving sink is simply
    /// treated as "nothing more to notify," never as a reason this read loop itself stops making
    /// progress.</summary>
    private static readonly TimeSpan SinkNotificationTimeout = TimeSpan.FromSeconds(30);

    private static async Task NotifyLineAsync(StringBuilder line, IProcessOutputSink? sink, bool isError)
    {
        if (sink is null)
        {
            return;
        }

        try
        {
            string text = line.ToString();
            Task notify = isError
                ? sink.OnStandardErrorLineAsync(text, CancellationToken.None)
                : sink.OnStandardOutputLineAsync(text, CancellationToken.None);

            using CancellationTokenSource timeoutCancellation = new();
            Task timeout = Task.Delay(SinkNotificationTimeout, timeoutCancellation.Token);
            Task completed = await Task.WhenAny(notify, timeout).ConfigureAwait(false);
            if (completed == notify)
            {
                // Cancel the still-pending delay immediately: on a stream permitting up to
                // MaxEventCount lines, leaving one live 30-second timer per line until it expires
                // on its own would otherwise pile up.
                await timeoutCancellation.CancelAsync().ConfigureAwait(false);
                await notify.ConfigureAwait(false);
            }
            else
            {
                // The sink call is stuck; give up waiting on it rather than let a caller (e.g. a
                // cancellation's kill-then-drain sequence, which also awaits this same read loop)
                // block on it too. Observe whatever it eventually does so a later fault can never
                // escape as an unhandled UnobservedTaskException instead of being swallowed here,
                // as every other sink failure already is.
                _ = notify.ContinueWith(
                    static faulted => _ = faulted.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (Exception)
        {
            // See the summary above: a sink's own failure is never allowed to fault this loop.
        }
    }
}

/// <summary>Resolves the current commit through `git rev-parse HEAD`, never a shell string.</summary>
public sealed class GitRepository(IProcessRunner processRunner) : IRepository
{
    public async Task<string> GetHeadAsync(string projectRoot, CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner
            .RunAsync(new("git", ["rev-parse", "HEAD"], projectRoot), cancellationToken)
            .ConfigureAwait(false);
        string head = result.StandardOutput.Trim();
        if (result.ExitCode != 0 || head.Length == 0)
        {
            throw new InvalidOperationException("'git rev-parse HEAD' did not resolve a commit.");
        }

        return head;
    }

    public async Task<string?> GetCurrentBranchAsync(string projectRoot, CancellationToken cancellationToken)
    {
        ProcessResult result = await processRunner
            .RunAsync(new("git", ["symbolic-ref", "--short", "HEAD"], projectRoot), cancellationToken)
            .ConfigureAwait(false);
        string branch = result.StandardOutput.Trim();
        return result.ExitCode == 0 && branch.Length > 0 ? branch : null;
    }

    public async Task<GitOperationResult> MergeSprintIntoDefaultBranchAsync(
        string projectRoot, string defaultBranch, string sourceBranch, CancellationToken cancellationToken)
    {
        ProcessResult status = await processRunner
            .RunAsync(new("git", ["status", "--porcelain"], projectRoot), cancellationToken).ConfigureAwait(false);
        if (status.ExitCode != 0 || status.StandardOutput.Trim().Length > 0)
        {
            return GitOperationResult.Fail(DiagnosticCodes.RepositoryDirty, status.StandardError);
        }

        string? currentBranch = await GetCurrentBranchAsync(projectRoot, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(currentBranch, defaultBranch, StringComparison.Ordinal))
        {
            return GitOperationResult.Fail(DiagnosticCodes.RepositoryBranchMismatch);
        }

        ProcessResult merged = await processRunner
            .RunAsync(new("git", ["merge", "--ff-only", "--", sourceBranch], projectRoot), cancellationToken)
            .ConfigureAwait(false);
        if (merged.ExitCode != 0)
        {
            return GitOperationResult.Fail(DiagnosticCodes.WorktreeIntegrationDiverged, merged.StandardError);
        }

        return GitOperationResult.Ok(await GetHeadAsync(projectRoot, cancellationToken).ConfigureAwait(false));
    }
}

public sealed class NetworkClient(HttpClient client) : INetworkClient
{
    public Task<Stream> GetStreamAsync(Uri uri, CancellationToken cancellationToken) =>
        client.GetStreamAsync(uri, cancellationToken);
}

/// <summary>
/// The real, OS-backed <see cref="IEnvironmentPaths"/>. <see cref="InstanceId"/> defaults to the
/// build configuration's release/Debug split — matching <c>Forge.Host.Client.InstanceIdentity</c>'s
/// <c>Release</c>/<c>Debug</c> constants, duplicated rather than referenced because
/// <c>Forge.Host.Client</c> is a leaf transport/protocol project and this neutral engine project
/// takes no dependency on it (see <c>ControlProtocol.JsonOptions</c>'s doc comment for the same
/// pattern). A composition root that already knows a more specific instance id (e.g. Forge.Host's
/// own <c>--instance-id</c>, including the unique ephemeral id automated tests spawn a real Host
/// process with) supplies it explicitly instead.
/// </summary>
public sealed class SystemEnvironmentPaths(string? instanceId = null) : IEnvironmentPaths
{
    private const string ReleaseInstanceId = "forge";
    private const string DebugInstanceId = "forge-dev";

    private static readonly string DefaultInstanceId =
#if DEBUG
        DebugInstanceId;
#else
        ReleaseInstanceId;
#endif

    public string LocalApplicationData { get; } =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public string UserProfile { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string CurrentDirectory => Environment.CurrentDirectory;

    // Whitespace-only is treated the same as omitted: a blank --instance-id must never silently
    // collapse Path.Combine's instance segment back to the unscoped path this type exists to avoid.
    public string InstanceId { get; } = string.IsNullOrWhiteSpace(instanceId) ? DefaultInstanceId : instanceId;
}

public static class InfrastructureServices
{
    public static IServiceCollection AddForgeInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IFileSystem, PhysicalFileSystem>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IRepository, GitRepository>();
        services.AddSingleton<IWorktreeManager, GitWorktreeManager>();
        // Forge's own release bundle download can run into the hundreds of megabytes; the
        // default 100-second HttpClient.Timeout covers the whole request, including the body,
        // and would abort a slow-connection download mid-stream.
        services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
        services.AddSingleton<INetworkClient, NetworkClient>();
        // TryAdd, matching IPlatformPreflight's convention (ForgeHost.cs): signals this is an
        // intended override point. Forge.Host's composition root always overrides this with an
        // instance-scoped SystemEnvironmentPaths afterward via a plain AddSingleton, which still
        // wins for singular resolution regardless of Try here — this only makes that override
        // relationship explicit instead of an accident of registration order.
        services.TryAddSingleton<IEnvironmentPaths, SystemEnvironmentPaths>();
        services.AddSingleton<ISafeLogger, SafeLogger>();
        return services;
    }
}
