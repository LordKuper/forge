using System.Globalization;
using Forge.Localization;

namespace Forge.Desktop.Presentation;

/// <summary>
/// ADR 0057's presentation-only fallback for a sprint with no frozen
/// <c>SprintDefinition.Title</c> — an untitled sprint, or one created before that field existed.
/// The durable contract stays honestly nullable (nothing synthesizes a title into the journal, the
/// snapshot, or the workspace summary); only a surface that must render *something* resolves one,
/// here, the same shape <see cref="ProjectDisplayName"/> already established for a project's own
/// "real value or synthesized fallback" case.
/// </summary>
/// <remarks>
/// The fallback is the sprint's own creation sequence ("Sprint 3"), never the project root or
/// directory name: every sprint in a project would otherwise render the identical label, which is
/// strictly worse than the bare number it replaced.
/// </remarks>
public static class SprintDisplayTitle
{
    public static string Resolve(string? title, int creationSequence, SurfaceText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.IsNullOrWhiteSpace(title) ? Ordinal(creationSequence, text) : title;
    }

    /// <summary>
    /// The row-naming form of <see cref="Resolve"/>: the resolved title, followed by the sprint's
    /// ordinal when -- and only when -- the title is a frozen one.
    /// </summary>
    /// <remarks>
    /// PR #122 review finding 1. A frozen <c>SprintDefinition.Title</c> is free text with no
    /// uniqueness constraint (ADR 0057 trims, redacts and length-bounds it, nothing more), so two
    /// sprints in one project may share one, and a name carrying the title alone would render and
    /// announce them identically -- the same defect the ordinal-only name had, relocated onto
    /// same-titled sprints. Appending <see cref="Ordinal"/> restores the disambiguator.
    /// The untitled path takes no suffix: its resolved title already <em>is</em> the ordinal, and
    /// repeating it would speak "Sprint 2 (Sprint 2)". The branch is the same
    /// <see cref="string.IsNullOrWhiteSpace"/> test <see cref="Resolve"/> itself makes, never a
    /// comparison against the rendered fallback text.
    /// </remarks>
    public static string ResolveAccessible(string? title, int creationSequence, SurfaceText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.IsNullOrWhiteSpace(title)
            ? Ordinal(creationSequence, text)
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{title} ({Ordinal(creationSequence, text)})");
    }

    /// <summary>The sprint's ordinal label ("Sprint 3"). One piece of copy serves both roles because
    /// they are the same fact: the untitled fallback is precisely this ordinal standing in for a
    /// title, so <see cref="MessageKeys.SprintUntitledFallback"/> stays the single canonical source
    /// rather than being duplicated under a second key.</summary>
    private static string Ordinal(int creationSequence, SurfaceText text) => string.Format(
        CultureInfo.InvariantCulture,
        text.Resolve(MessageKeys.SprintUntitledFallback),
        creationSequence);
}
