using System.Text;
using System.Text.Json;
using Forge.Application;
using Forge.Bootstrap;
using Forge.Configuration;
using Forge.Domain;
using Forge.Host;
using Forge.Host.Client;
using Forge.Infrastructure;
using Forge.Presentation;
using Forge.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Forge.IntegrationTests;

public sealed class ControlPlaneTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task HandshakeAndPingRoundTrip()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        InitializeProjectResult initialized = await environment
            .InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        Assert.True(initialized.Succeeded);
        string instanceId = InstanceIdentity.CreateEphemeral();

        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            instanceId,
            cancellationToken);

        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));

        ControlDiagnostic connected = await client.EnsureConnectedAsync(null, cancellationToken);
        Assert.Equal(ControlDiagnosticCode.None, connected.Code);
        Assert.True(client.IsConnected);

        ControlResponse response = await client.PingAsync(cancellationToken);

        Assert.Equal(ControlDiagnosticCode.None, response.Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task HandshakeResponseAdvertisesTheHostsRealCapabilities()
    {
        // Regression coverage: the handshake response's Capabilities field used to be hardcoded to
        // an empty list regardless of what the Host actually supports (2026-08-15 audit finding).
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot, instanceId, cancellationToken);
        string endpointName = InstanceIdentity.ComputePipeName(instanceId, projectId);
        NamedPipeControlTransport transport = new();

        await using ILocalControlConnection connection = await transport
            .ConnectAsync(endpointName, TimeSpan.FromSeconds(5), cancellationToken);
        ControlHandshakeRequest handshake =
            new(ControlProtocol.Version, "1.0.0-test", instanceId, [], Guid.NewGuid());
        await connection.SendAsync(
            JsonSerializer.SerializeToUtf8Bytes(handshake, ControlProtocol.JsonOptions),
            TimeSpan.FromSeconds(5),
            cancellationToken);
        byte[] responseBytes = await connection.ReceiveAsync(TimeSpan.FromSeconds(5), cancellationToken);
        ControlHandshakeResponse response =
            JsonSerializer.Deserialize<ControlHandshakeResponse>(responseBytes, ControlProtocol.JsonOptions)!;

        Assert.Equal(handshake.CorrelationId, response.CorrelationId);
        Assert.Equal(CapabilityIds.Implemented, response.Capabilities);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task IncompatibleProtocolVersionIsRejectedBeforeAnyProjectAccess()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();

        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            instanceId,
            cancellationToken);

        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        string endpointName = InstanceIdentity.ComputePipeName(instanceId, projectId);
        NamedPipeControlTransport transport = new();
        await using ILocalControlConnection connection = await transport
            .ConnectAsync(endpointName, TimeSpan.FromSeconds(5), cancellationToken);
        ControlHandshakeRequest request = new("99.0.0", "1.0.0-test", instanceId, [], Guid.NewGuid());
        await connection.SendAsync(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(request, ControlProtocol.JsonOptions),
            TimeSpan.FromSeconds(5),
            cancellationToken);
        byte[] responseBytes = await connection.ReceiveAsync(TimeSpan.FromSeconds(5), cancellationToken);
        ControlHandshakeResponse response = System.Text.Json.JsonSerializer
            .Deserialize<ControlHandshakeResponse>(responseBytes, ControlProtocol.JsonOptions)!;

        Assert.Equal(ControlDiagnosticCode.VersionIncompatible, response.Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DiscoveryReportsUnavailableWhenNoHostIsListening()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(Guid.NewGuid(), InstanceIdentity.CreateEphemeral(), "1.0.0-test")
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(300),
            });

        ControlDiagnostic diagnostic = await client.EnsureConnectedAsync(null, cancellationToken);

        Assert.Equal(ControlDiagnosticCode.Unavailable, diagnostic.Code);
        Assert.False(client.IsConnected);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ASecondHostForTheSameProjectUnderADifferentInstanceIdStillExitsInsteadOfCompeting()
    {
        // ADR 0005: "Release, development, test, CLI, and Desktop instances use the same lease
        // namespace, so distinct instance data roots cannot become concurrent writers of one
        // `.forge/` tree." Two *different* instance ids — each with its own distinct pipe name — so
        // this proves the project lease itself refuses the second Host, not an incidental pipe-name
        // collision (which is what a same-instance-id pair would also produce).
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string firstInstanceId = InstanceIdentity.CreateEphemeral();
        string secondInstanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);

        await using ControlPlaneHost first = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            firstInstanceId,
            cancellationToken);

        // host.StartAsync() returns once ExecuteAsync starts running, not once it has won the lease — connect a
        // client first so "first" is confirmed listening (and therefore already holds the lease) before "second"
        // starts, or the two Hosts could race for the mutex in either order.
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, firstInstanceId, "1.0.0-test"));
        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);
        Assert.False(first.IsStopping);

        await using ControlPlaneHost second = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            secondInstanceId,
            cancellationToken);

        Assert.True(await second.WaitForStoppingAsync(TimeSpan.FromSeconds(10), cancellationToken));
        Assert.False(first.IsStopping);
        Assert.Equal(ControlDiagnosticCode.None, (await client.PingAsync(cancellationToken)).Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ClientReconnectsAfterTheHostRestarts()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));

        await using (ControlPlaneHost first = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            instanceId,
            cancellationToken))
        {
            Assert.Equal(
                ControlDiagnosticCode.None,
                (await client.EnsureConnectedAsync(null, cancellationToken)).Code);
            Assert.Equal(ControlDiagnosticCode.None, (await client.PingAsync(cancellationToken)).Diagnostic.Code);
        }

        // The first Host stopped (and released the project lease); pinging the now-dead connection must fail,
        // and the client must not retry silently.
        await Assert.ThrowsAsync<ControlProtocolException>(() => client.PingAsync(cancellationToken));
        Assert.False(client.IsConnected);

        await using ControlPlaneHost second = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            instanceId,
            cancellationToken);

        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);
        Assert.Equal(ControlDiagnosticCode.None, (await client.PingAsync(cancellationToken)).Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ANonStringCursorValueIsRejectedAsMalformedInsteadOfSilentlyReplayingFromScratch()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);

        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            instanceId,
            cancellationToken);
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);

        ControlResponse response = await client.SendAsync(
            ControlProtocol.ReadControlEventsKind,
            JsonSerializer.SerializeToElement(new { cursor = 123 }),
            cancellationToken);

        Assert.Equal(ControlDiagnosticCode.Malformed, response.Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ANullCursorValueStillMeansNoCursorSupplied()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);

        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            instanceId,
            cancellationToken);
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);

        ControlResponse response = await client.SendAsync(
            ControlProtocol.ReadControlEventsKind,
            JsonSerializer.SerializeToElement(new { cursor = (string?)null }),
            cancellationToken);

        Assert.Equal(ControlDiagnosticCode.None, response.Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AJournalReadFailureReportsAnInternalErrorInsteadOfHangingTheConnection()
    {
        if (!OperatingSystem.IsWindows())
        {
            // FileShare.None sharing-violation enforcement (this test's fault-injection mechanism) is
            // reliably a hard failure only on Windows; .NET's FileStream does not emulate it on Unix.
            return;
        }

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);

        string eventsPath = Path.Combine(
            FileSprintEventLog.SprintDirectory(environment.ProjectRoot, sprintId), "events.jsonl");
        await using FileStream exclusiveLock =
            new(eventsPath, FileMode.Open, FileAccess.Read, FileShare.None);

        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot,
            instanceId,
            cancellationToken);
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);

        ControlResponse response = await client.SendAsync(
            ControlProtocol.GetProjectSnapshotKind,
            JsonSerializer.SerializeToElement(new { detail = "full", sprint_id = sprintId.Value }),
            cancellationToken);

        Assert.Equal(ControlDiagnosticCode.InternalError, response.Diagnostic.Code);

        // The connection itself must still be usable afterward — a locked file must not have killed
        // the connection or the Host.
        Assert.Equal(ControlDiagnosticCode.None, (await client.PingAsync(cancellationToken)).Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AGarbagePayloadAfterAValidHandshakeEndsOnlyThatConnectionWithoutAffectingTheHost()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot, instanceId, cancellationToken);
        string endpointName = InstanceIdentity.ComputePipeName(instanceId, projectId);
        NamedPipeControlTransport transport = new();

        await using (ILocalControlConnection connection = await transport
            .ConnectAsync(endpointName, TimeSpan.FromSeconds(5), cancellationToken))
        {
            ControlHandshakeRequest handshake =
                new(ControlProtocol.Version, "1.0.0-test", instanceId, [], Guid.NewGuid());
            await connection.SendAsync(
                JsonSerializer.SerializeToUtf8Bytes(handshake, ControlProtocol.JsonOptions),
                TimeSpan.FromSeconds(5),
                cancellationToken);
            await connection.ReceiveAsync(TimeSpan.FromSeconds(5), cancellationToken);

            // A well-formed frame whose payload is not JSON at all — not even a malformed request
            // envelope, just garbage bytes.
            await connection.SendAsync(
                Encoding.UTF8.GetBytes("this is not json"),
                TimeSpan.FromSeconds(5),
                cancellationToken);

            // The Host closes this one connection (no CorrelationId to reply to) instead of hanging
            // or letting the JsonException escape unobserved.
            await Assert.ThrowsAsync<ControlProtocolException>(
                () => connection.ReceiveAsync(TimeSpan.FromSeconds(5), cancellationToken));
        }

        // The Host itself is unaffected: a fresh, well-behaved client is still served normally.
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);
        Assert.Equal(ControlDiagnosticCode.None, (await client.PingAsync(cancellationToken)).Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AGarbageFirstMessageInsteadOfAHandshakeEndsOnlyThatConnectionWithoutAffectingTheHost()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot, instanceId, cancellationToken);
        string endpointName = InstanceIdentity.ComputePipeName(instanceId, projectId);
        NamedPipeControlTransport transport = new();

        await using (ILocalControlConnection connection = await transport
            .ConnectAsync(endpointName, TimeSpan.FromSeconds(5), cancellationToken))
        {
            // A well-formed frame whose payload is not JSON at all, sent as the very first message
            // instead of a real ControlHandshakeRequest.
            await connection.SendAsync(
                Encoding.UTF8.GetBytes("this is not json"),
                TimeSpan.FromSeconds(5),
                cancellationToken);

            // The Host closes this connection instead of hanging or letting the JsonException
            // escape HandshakeAsync unobserved.
            await Assert.ThrowsAsync<ControlProtocolException>(
                () => connection.ReceiveAsync(TimeSpan.FromSeconds(5), cancellationToken));
        }

        // The Host itself is unaffected: a fresh, well-behaved client is still served normally.
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);
        Assert.Equal(ControlDiagnosticCode.None, (await client.PingAsync(cancellationToken)).Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AClientThatNeverHandshakesIsDroppedAfterItsDeadlineAndTheHostKeepsServing()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            new ControlPlaneOptions(environment.ProjectRoot, instanceId, HandshakeTimeout: TimeSpan.FromMilliseconds(300)),
            cancellationToken);
        string endpointName = InstanceIdentity.ComputePipeName(instanceId, projectId);
        NamedPipeControlTransport transport = new();

        await using (ILocalControlConnection silent = await transport
            .ConnectAsync(endpointName, TimeSpan.FromSeconds(5), cancellationToken))
        {
            // Never sends a handshake. The Host's own receive deadline (300ms) fires and it closes
            // this connection server-side rather than holding it open indefinitely; that closure is
            // what unblocks this pending client-side receive.
            await Assert.ThrowsAsync<ControlProtocolException>(
                () => silent.ReceiveAsync(TimeSpan.FromSeconds(5), cancellationToken));
        }

        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);
        Assert.Equal(ControlDiagnosticCode.None, (await client.PingAsync(cancellationToken)).Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AClientThatDisconnectsWhileIdleDoesNotAffectOtherConnections()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot, instanceId, cancellationToken);
        ForgeHostClientOptions clientOptions = new(projectId, instanceId, "1.0.0-test");

        await using (ForgeHostClient doomed = new(new NamedPipeControlTransport(), clientOptions))
        {
            Assert.Equal(
                ControlDiagnosticCode.None,
                (await doomed.EnsureConnectedAsync(null, cancellationToken)).Code);
            Assert.Equal(ControlDiagnosticCode.None, (await doomed.PingAsync(cancellationToken)).Diagnostic.Code);
        }

        // "doomed" disposed abruptly (simulating a crashed client) while the Host was idly waiting
        // for its next request; the Host must notice the closed connection and move on rather than
        // leaking a stuck serving task or affecting anyone else.
        await using ForgeHostClient client = new(new NamedPipeControlTransport(), clientOptions);
        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);
        Assert.Equal(ControlDiagnosticCode.None, (await client.PingAsync(cancellationToken)).Diagnostic.Code);
        Assert.False(host.IsStopping);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SetConfigurationRoundTripsThroughTheHostAndPersistsTheValue()
    {
        // ADR 0005: the Host is the only `.forge/` writer — RemoteForgeMutations is the real
        // production client, not a raw ForgeHostClient.SendAsync call.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);

        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot, instanceId, cancellationToken);
        ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        await using RemoteForgeMutations mutations = new(client);

        // A string-typed key keeps the raw surface text verbatim (ConfigurationValueParser.Parse) —
        // the raw value is the bare content ("ru"), not a JSON-quoted literal ("\"ru\"" would store
        // literal quote characters as part of the string).
        ConfigurationWriteResult result = await mutations.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            "artifacts.language.user_facing",
            "ru",
            cancellationToken);

        Assert.True(result.Succeeded, $"diag={result.DiagnosticCode}");
        ConfigurationView project = await environment.Application.GetProjectConfigurationAsync(
            environment.ProjectRoot,
            cancellationToken);
        EffectiveConfigurationValue value = Assert.Single(
            project.Values,
            item => item.Key == "artifacts.language.user_facing");
        Assert.Equal("\"ru\"", value.Value.GetRawText());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SetConfigurationWithAnEmptyKeyReturnsConfigurationKeyUnknownInsteadOfThrowing()
    {
        // Regression test: RemoteForgeMutations.SetConfigurationAsync previously rejected an empty
        // key with an unhandled ArgumentException instead of the diagnostic the local
        // ForgeApplication path returns for the same input (registry.FindRequired's
        // KeyNotFoundException, mapped to ConfigurationKeyUnknown).
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);

        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot, instanceId, cancellationToken);
        ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        await using RemoteForgeMutations mutations = new(client);

        ConfigurationWriteResult result = await mutations.SetConfigurationAsync(
            ConfigurationScope.Project,
            environment.ProjectRoot,
            string.Empty,
            "ru",
            cancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(DiagnosticCodes.ConfigurationKeyUnknown, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SetConfigurationRejectsUserScopeAsMalformed()
    {
        // User-scope configuration is not project state and must never be accepted by a project's
        // Host, even from a client that (incorrectly) sends it — RemoteForgeMutations itself
        // refuses to send this (see the corresponding unit test), so this proves the Host's own
        // defense-in-depth independent of a well-behaved client.
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);

        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot, instanceId, cancellationToken);
        await using ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        Assert.Equal(ControlDiagnosticCode.None, (await client.EnsureConnectedAsync(null, cancellationToken)).Code);

        ControlResponse response = await client.SendAsync(
            ControlProtocol.SetConfigurationKind,
            JsonSerializer.SerializeToElement(
                new SetConfigurationRequest("user", "interaction.confirm_destructive", "false"),
                ControlProtocol.JsonOptions),
            cancellationToken);

        Assert.Equal(ControlDiagnosticCode.Malformed, response.Diagnostic.Code);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RecoverStartupRoundTripsThroughTheHostWhenNoRecoveryIsNeeded()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);

        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot, instanceId, cancellationToken);
        ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        await using RemoteForgeMutations mutations = new(client);

        RecoverStartupResult result =
            await mutations.RecoverStartupAsync(environment.ProjectRoot, confirmed: true, cancellationToken);

        // A healthy project has nothing to recover — ForgeApplication.RecoverStartupAsync's own
        // early-return path, reached this time via the Host instead of in-process.
        Assert.True(result.Succeeded);
        Assert.Null(result.Check);
        Assert.Equal(DiagnosticCodes.None, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResolveGateRoundTripsThroughTheHostAndSettlesTheNode()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("gate", NodeKind.HumanGate, [])]),
            cancellationToken)).SprintId!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot, instanceId, cancellationToken);
        ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        await using RemoteForgeMutations mutations = new(client);

        NodeActionResult result = await mutations.ResolveGateAsync(
            environment.ProjectRoot, sprintId.Value, "gate", approved: true, confirmed: true, cancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(NodeState.Succeeded, result.Node!.State);
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes["gate"].State);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConfirmNodeRoundTripsThroughTheHostAndSettlesTheNode()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(),
                Graph: [new("confirm", NodeKind.Work, [], NodeRole.Confirmation)]),
            cancellationToken)).SprintId!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot, instanceId, cancellationToken);
        ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        await using RemoteForgeMutations mutations = new(client);

        RecordConfirmationResult result = await mutations.ConfirmNodeAsync(
            environment.ProjectRoot,
            sprintId.Value,
            "confirm",
            ConfirmationOutcome.Confirmed,
            "Feature X matches its agreed definition of done.",
            [new(ConfirmationEvidenceKind.Execution, "Ran the full test suite locally; all green.")],
            confirmed: true,
            cancellationToken);

        Assert.True(result.Succeeded, $"diag={result.DiagnosticCode}");
        Assert.Equal(ConfirmationOutcome.Confirmed, result.Confirmation!.Outcome);
        Assert.Equal(
            ConfirmationEvidenceKind.Execution, Assert.Single(result.Confirmation.Evidence).Kind);
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes["confirm"].State);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RecordTestWorkRoundTripsThroughTheHostAndSettlesTheNode()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(),
                Graph: [new("test_work", NodeKind.Work, [], NodeRole.TestWork)]),
            cancellationToken)).SprintId!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot, instanceId, cancellationToken);
        ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        await using RemoteForgeMutations mutations = new(client);

        RecordTestWorkResult result = await mutations.RecordTestWorkAsync(
            environment.ProjectRoot,
            sprintId.Value,
            "test_work",
            TestWorkOutcome.TestsAdded,
            "Added a regression test for the reported off-by-one.",
            confirmed: true,
            cancellationToken);

        Assert.True(result.Succeeded, $"diag={result.DiagnosticCode}");
        Assert.Equal(TestWorkOutcome.TestsAdded, result.TestWork!.Outcome);
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(NodeState.Succeeded, state.Nodes["test_work"].State);
    }

    // Unlike every other round-trip test in this file, `finalize_sprint`'s real action is genuine
    // git I/O against `environment.ProjectRoot` itself -- and unlike this fixture's other sprints
    // (created in-process against `environment`'s own FakeRepository), the real Host this test talks
    // to runs a REAL GitWorktreeManager-adjacent GitRepository against that same path, which is not
    // an actual git repository here. This deliberately does not assert the happy path (already
    // covered end to end by FinalizeSprintTests.cs against a FakeRepository) -- it proves the wire
    // path itself: the request serializes, reaches the real dispatch handler, runs real `git`, and a
    // well-formed `FinalizeSprintResult` structure comes back over the pipe, not a crash or a
    // malformed payload.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task FinalizeSprintRoundTripsThroughTheHostAndReturnsAWellFormedResult()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(),
                Graph: [new("finalization", NodeKind.Work, [], NodeRole.Finalization)]),
            cancellationToken)).SprintId!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot, instanceId, cancellationToken);
        ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        await using RemoteForgeMutations mutations = new(client);

        FinalizeSprintResult result = await mutations.FinalizeSprintAsync(
            environment.ProjectRoot, sprintId.Value, "finalization", confirmed: true, cancellationToken);

        Assert.False(result.Succeeded);
        Assert.NotEqual(DiagnosticCodes.None, result.DiagnosticCode);
        Assert.NotEqual(DiagnosticCodes.HostUnavailable, result.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SupersedeAttemptRoundTripsThroughTheHostAndLinksAReplacement()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintScheduler scheduler = environment.Resolve<SprintScheduler>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        SprintTransitionResult toReady = await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, 1, SprintOrchestrator.RunSprintKey(
                (await orchestrator.GetSprintAsync(environment.ProjectRoot, sprintId, cancellationToken))!)),
            cancellationToken);
        await orchestrator.RunSprintAsync(
            new(environment.ProjectRoot, sprintId, toReady.Sprint!.Version,
                SprintOrchestrator.RunSprintKey(toReady.Sprint)),
            cancellationToken);
        StartAttemptResult started = await scheduler.StartAttemptAsync(
            environment.ProjectRoot, sprintId, "a", 2, cancellationToken);
        Assert.True(started.Succeeded, $"diag={started.DiagnosticCode}");
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot, instanceId, cancellationToken);
        ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        await using RemoteForgeMutations mutations = new(client);

        CompleteAttemptResult result = await mutations.SupersedeAttemptAsync(
            environment.ProjectRoot, sprintId.Value, started.AttemptId!.Value, "Try a different approach.",
            confirmed: true, cancellationToken);

        Assert.True(result.Succeeded, $"diag={result.DiagnosticCode}");
        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(AttemptState.Cancelled, state.Attempts[started.AttemptId!.Value.ToString("D")].State);
        AttemptSnapshot replacement =
            Assert.Single(state.Attempts.Values, candidate => candidate.Id != started.AttemptId);
        Assert.Equal(started.AttemptId, replacement.SupersedesAttemptId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task SprintLifecycleCommandsRoundTripThroughTheHost()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TestEnvironment environment = new();
        await environment.InitializeAsync(environment.ProjectRoot, true, cancellationToken);
        // `create_sprint`'s own success path needs a real, registered `ILlmProvider` to freeze into
        // the sprint's routing profile — this harness's real Host composition (`ForgeHost.AddForgeCore`,
        // via `ControlPlaneHost` below) registers none, unlike the in-process `TestEnvironment` DI
        // container the other tests in this fixture use. `run`/`resume`/`cancel` have no such
        // dependency, so the sprint they exercise is created in-process first, matching
        // `ResolveGateRoundTripsThroughTheHostAndSettlesTheNode`'s own precedent.
        SprintOrchestrator orchestrator = environment.Resolve<SprintOrchestrator>();
        SprintId sprintId = (await orchestrator.CreateSprintAsync(
            new(environment.ProjectRoot, 1, Guid.NewGuid(), Graph: [new("a", NodeKind.Work, [])]),
            cancellationToken)).SprintId!;
        string instanceId = InstanceIdentity.CreateEphemeral();
        Guid projectId = await ProjectIdentity
            .ReadProjectIdAsync(environment.ProjectRoot, new ConfigurationRegistry(), cancellationToken);
        await using ControlPlaneHost host = await ControlPlaneHost.StartAsync(
            environment.ProjectRoot, instanceId, cancellationToken);
        ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(projectId, instanceId, "1.0.0-test"));
        await using RemoteForgeMutations mutations = new(client);

        // `create_sprint`'s wire mechanics (kind constant, dispatch, request/response serialization)
        // are still proven here, on its natural failure path in this harness rather than its success
        // one: a transport/serialization bug would surface as `HostUnavailable`/a thrown exception, not
        // this specific business-logic diagnostic.
        CreateSprintResult created = await mutations.CreateSprintAsync(environment.ProjectRoot, cancellationToken);
        Assert.False(created.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintProviderCandidatesEmpty, created.DiagnosticCode);

        SprintTransitionResult toReady =
            await mutations.RunSprintAsync(environment.ProjectRoot, sprintId.Value, cancellationToken);
        Assert.True(toReady.Succeeded, $"diag={toReady.DiagnosticCode}");
        Assert.Equal(SprintState.Ready, toReady.Sprint!.State);

        SprintTransitionResult toRunning =
            await mutations.RunSprintAsync(environment.ProjectRoot, sprintId.Value, cancellationToken);
        Assert.True(toRunning.Succeeded, $"diag={toRunning.DiagnosticCode}");
        Assert.Equal(SprintState.Running, toRunning.Sprint!.State);

        SprintTransitionResult resumeWhileRunningIsRefused =
            await mutations.ResumeSprintAsync(environment.ProjectRoot, sprintId.Value, cancellationToken);
        Assert.False(resumeWhileRunningIsRefused.Succeeded);
        Assert.Equal(DiagnosticCodes.SprintTransitionInvalid, resumeWhileRunningIsRefused.DiagnosticCode);

        SprintTransitionResult cancelled = await mutations
            .CancelSprintAsync(environment.ProjectRoot, sprintId.Value, confirmed: true, cancellationToken);
        Assert.True(cancelled.Succeeded, $"diag={cancelled.DiagnosticCode}");
        Assert.Equal(SprintState.Cancelled, cancelled.Sprint!.State);

        ISprintStore store = environment.Resolve<ISprintStore>();
        SprintWorkflowState state = (await store.LoadAsync(environment.ProjectRoot, sprintId, cancellationToken))!;
        Assert.Equal(SprintState.Cancelled, state.Sprint.State);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RemoteMutationsReportHostUnavailableInsteadOfThrowingWhenNoHostIsRunning()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(Guid.NewGuid(), InstanceIdentity.CreateEphemeral(), "1.0.0-test")
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(300),
            });
        await using RemoteForgeMutations mutations = new(client);

        ConfigurationWriteResult configResult = await mutations.SetConfigurationAsync(
            ConfigurationScope.Project,
            null,
            "artifacts.language.user_facing",
            "\"ru\"",
            cancellationToken);
        RecoverStartupResult recoverResult =
            await mutations.RecoverStartupAsync(null, confirmed: true, cancellationToken);

        Assert.False(configResult.Succeeded);
        Assert.Equal(DiagnosticCodes.HostUnavailable, configResult.DiagnosticCode);
        Assert.False(recoverResult.Succeeded);
        Assert.Equal(DiagnosticCodes.HostUnavailable, recoverResult.DiagnosticCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RemoteMutationsRefuseToSendUserScopeConfigurationAtAll()
    {
        // A programming error, not a runtime condition: catching it client-side (before ever
        // touching the network) is stronger than relying solely on the Host's own rejection.
        ForgeHostClient client = new(
            new NamedPipeControlTransport(),
            new ForgeHostClientOptions(Guid.NewGuid(), InstanceIdentity.CreateEphemeral(), "1.0.0-test"));
        RemoteForgeMutations mutations = new(client);

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => mutations
                .SetConfigurationAsync(
                    ConfigurationScope.User,
                    null,
                    "interaction.confirm_destructive",
                    "false",
                    TestContext.Current.CancellationToken)
                .GetAwaiter()
                .GetResult());
        Assert.Equal("scope", exception.ParamName);
    }
}

/// <summary>Runs <see cref="ControlPlaneHostedService"/> in-process against a real project directory for tests.</summary>
internal sealed class ControlPlaneHost : IAsyncDisposable
{
    private readonly IHost host;

    private ControlPlaneHost(IHost host) => this.host = host;

    public IServiceProvider Services => host.Services;

    /// <summary>True once <see cref="ControlPlaneHostedService"/> has called <c>StopApplication</c> — e.g. it lost the lease.</summary>
    public bool IsStopping =>
        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested;

    /// <summary>Polls <see cref="IsStopping"/> until it turns true or <paramref name="timeout"/> elapses.</summary>
    public async Task<bool> WaitForStoppingAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = new(timeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            while (!IsStopping)
            {
                await Task.Delay(20, linked.Token).ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return false;
        }
    }

    public static Task<ControlPlaneHost> StartAsync(
        string projectRoot,
        string instanceId,
        CancellationToken cancellationToken) =>
        StartAsync(new ControlPlaneOptions(projectRoot, instanceId), cancellationToken);

    public static async Task<ControlPlaneHost> StartAsync(
        ControlPlaneOptions options,
        CancellationToken cancellationToken)
    {
        IHost host = ForgeHost.CreateBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(options);
                // Matches Forge.Host/Program.cs: without this override, every in-process test Host
                // would share one Debug-build IEnvironmentPaths instead of this test's own ephemeral
                // instance id, writing into the real developer machine's %LOCALAPPDATA%.
                services.AddSingleton<IEnvironmentPaths>(new SystemEnvironmentPaths(options.InstanceId));
                // Matches TestEnvironment's own override: ForgeHost.AddForgeCore only registers
                // UnsupportedPlatformPreflight as a fallback, so a real StartupPipeline round-trip
                // (RecoverStartup, SetConfiguration) would otherwise always fail its Platform check
                // on this test harness, regardless of the request under test.
                services.AddSingleton<IPlatformPreflight>(new SupportedPlatformPreflight());
                services.AddSingleton(new ResumeSchedulerOptions(options.ProjectRoot));
                services.AddSingleton<ResumeSchedulerHostedService>();
                services.AddSingleton(new NotificationDeliveryOptions(options.ProjectRoot));
                services.AddSingleton<NotificationDeliveryHostedService>();
                services.AddSingleton(new IntakeExecutionOptions(options.ProjectRoot));
                services.AddSingleton<IntakeExecutionHostedService>();
                services.AddSingleton(new PlanningExecutionOptions(options.ProjectRoot));
                services.AddSingleton<PlanningExecutionHostedService>();
                services.AddSingleton(new ImplementationExecutionOptions(options.ProjectRoot));
                services.AddSingleton<ImplementationExecutionHostedService>();
                services.AddSingleton(new ReviewExecutionOptions(options.ProjectRoot));
                services.AddSingleton<ReviewExecutionHostedService>();
                services.AddHostedService<ControlPlaneHostedService>();
            })
            .Build();
        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        return new ControlPlaneHost(host);
    }

    public async ValueTask DisposeAsync()
    {
        await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        host.Dispose();
    }
}
