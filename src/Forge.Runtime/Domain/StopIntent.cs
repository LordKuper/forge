namespace Forge.Domain;

/// <summary>
/// A durable request to stop the exact active operation on <see cref="AttemptId"/> (plan section
/// 7). Recording this intent before relying on the in-memory active-operation registry is what
/// lets restart recovery and executors converge instead of resurrecting a stopped attempt after a
/// Host crash between request, process termination, state transitions, and worktree cleanup.
///
/// This is a plain contract type: no persistence wiring, no executor registration, and no
/// coordinator reads or writes it yet. The idempotent stop coordinator that durably records,
/// checks, and resolves this intent is introduced in a later slice (plan section 11, Slice 2).
/// </summary>
public sealed record StopIntent(AttemptId AttemptId, DateTimeOffset RequestedAt, string Reason);
