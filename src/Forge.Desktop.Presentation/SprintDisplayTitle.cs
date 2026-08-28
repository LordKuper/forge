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
    /// The row-naming form of <see cref="Resolve"/>: the sprint's ordinal in parentheses, followed
    /// by the resolved title, when -- and only when -- the title is a frozen one. This is the
    /// single string a sidebar row both <em>draws</em> and <em>announces</em>; it is not an
    /// accessibility-only variant layered over a plainer visible label.
    /// </summary>
    /// <remarks>
    /// PR #122 review findings 1 and 2. A frozen <c>SprintDefinition.Title</c> is free text with no
    /// uniqueness constraint (ADR 0057 trims, redacts and length-bounds it to 200 characters,
    /// nothing more), so two sprints in one project may share one, and a label carrying the title
    /// alone renders and announces them identically -- the same defect the ordinal-only name had,
    /// relocated onto same-titled sprints. Adding <see cref="Ordinal"/> restores the disambiguator.
    ///
    /// Round 1 applied this to the spoken name only, which left the *visible* rows still colliding
    /// (round 2 finding 2): two same-titled sprints drew byte-identical buttons, and a history row
    /// -- which draws nothing but this label and its state -- was wholly indistinguishable. One
    /// function now backs both renderings, so the two cannot drift apart again.
    ///
    /// The ordinal LEADS the titled form rather than trailing it (round 3 finding 1). A sidebar row
    /// draws this string into a fixed-width rail under <c>LineBreakMode.TailTruncation</c>, and a
    /// 200-character title routinely overruns that width; only what sits at the HEAD of the string
    /// is guaranteed to survive. Round 2 tried to keep a trailing ordinal visible with
    /// <c>MiddleTruncation</c> instead, which does not hold: MAUI's Windows renderer maps every
    /// head/middle mode onto <c>TextTrimming.WordEllipsis</c>, because WinUI implements trailing
    /// trimming only -- so the trailing ordinal was still dropped, and dropped one whole word at a
    /// time. Anchoring the disambiguator at the front makes its visibility a property of the string
    /// rather than of a truncation mode the platform does not implement.
    ///
    /// The ordinal is unconditional on the titled path rather than applied only when a title
    /// actually collides. A collision-conditional rule would have to know the whole rendered set,
    /// making this a set-aware operation instead of a pure per-sprint one; it would relabel a row
    /// whenever an unrelated sprint was created or archived; and its uniqueness scope across the
    /// separately rendered active and history lists is ambiguous exactly where the two lists sit
    /// adjacent in the rail. A stable, local, always-present ordinal costs one short parenthetical.
    ///
    /// The untitled path takes no ordinal prefix: its resolved title already <em>is</em> the
    /// ordinal, and repeating it would read "(Sprint 2) Sprint 2". The branch is the same
    /// <see cref="string.IsNullOrWhiteSpace"/> test <see cref="Resolve"/> itself makes, never a
    /// comparison against the rendered fallback text.
    /// </remarks>
    public static string ResolveRowTitle(string? title, int creationSequence, SurfaceText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.IsNullOrWhiteSpace(title)
            ? Ordinal(creationSequence, text)
            : string.Create(
                CultureInfo.InvariantCulture,
                $"({Ordinal(creationSequence, text)}) {title}");
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
