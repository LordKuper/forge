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
        return string.IsNullOrWhiteSpace(title)
            ? string.Format(
                CultureInfo.InvariantCulture,
                text.Resolve(MessageKeys.SprintUntitledFallback),
                creationSequence)
            : title;
    }
}
