namespace Forge.Compiler;

/// <summary>Syntactic relative-path safety checks shared by <see cref="ForgeDocumentCompiler"/>'s
/// `.forge/`-relative `references` check (ADR 0009) and <c>Forge.Infrastructure.GitContextReader</c>'s
/// project-root-relative `git_show`/`git_grep` path checks (ADR 0012) — the same four shape rules
/// (empty, backslash, drive/root prefix, unsafe segment) apply to both, even though what each does
/// with a syntactically safe path afterward (containment/existence/known-document-set membership vs.
/// handing it straight to `git`) differs completely.</summary>
internal static class RelativePathShape
{
    public static bool HasBackslash(string raw) => raw.Contains('\\', StringComparison.Ordinal);

    public static bool HasDriveOrRootPrefix(string raw) =>
        raw.StartsWith('/') || raw.Contains(':', StringComparison.Ordinal);

    public static bool HasUnsafeSegment(string raw) =>
        raw.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..");

    public static bool IsSyntacticallySafe(string raw) =>
        !string.IsNullOrWhiteSpace(raw) && !HasBackslash(raw) && !HasDriveOrRootPrefix(raw) && !HasUnsafeSegment(raw);
}
