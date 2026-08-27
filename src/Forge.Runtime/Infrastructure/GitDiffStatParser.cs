using System.Globalization;
using Forge.Application;
using Forge.Domain;

namespace Forge.Infrastructure;

/// <summary>
/// ADR 0059: turns `git diff --numstat -z` and `git diff --name-status -z` output into one
/// <see cref="DiffPayload"/>. Pure text parsing with no I/O, so the exact byte shapes real git
/// produces (binary markers, renames, non-ASCII paths) are testable without a repository.
/// </summary>
/// <remarks>
/// `-z` makes both commands NUL-terminate every field, which removes git's C-quoting entirely: a
/// path containing a space, a double quote, a backslash, or a non-ASCII byte arrives verbatim. It
/// also splits a rename into two independent fields rather than the `old =&gt; new` /
/// `dir/{a =&gt; b}` shorthand plain `--numstat` emits, which is ambiguous to parse (a real path may
/// itself contain `=&gt;`).
/// </remarks>
internal static class GitDiffStatParser
{
    public static DiffPayload Parse(string numstat, string nameStatus)
    {
        ArgumentNullException.ThrowIfNull(numstat);
        ArgumentNullException.ThrowIfNull(nameStatus);
        Dictionary<string, string> statuses = ParseNameStatus(nameStatus);
        List<DiffFileStat> files = [];
        int filesChanged = 0;
        int insertions = 0;
        int deletions = 0;
        int elided = 0;
        foreach (NumstatEntry entry in ParseNumstat(numstat))
        {
            filesChanged++;
            insertions += entry.Added;
            deletions += entry.Deleted;

            // Totals above cover every changed file; only the per-file rows are capped, so a reader
            // never sees an under-reported total merely because the change was large.
            if (files.Count >= GitWorktreeManagerDiffStatBudget.MaxFiles)
            {
                elided++;
                continue;
            }

            files.Add(new(entry.Path, entry.Added, entry.Deleted, ChangeKind(entry, statuses)));
        }

        return new(filesChanged, insertions, deletions, files, elided);
    }

    /// <summary>A binary file wins over whatever add/delete/modify status `--name-status` also
    /// reports for it: `--numstat` gives it no line counts at all (`-`/`-`), so "how many lines
    /// changed" — the only question the other kinds answer — has no answer for it.</summary>
    private static string ChangeKind(NumstatEntry entry, Dictionary<string, string> statuses)
    {
        if (entry.IsBinary)
        {
            return DiffChangeKinds.Binary;
        }

        // `--name-status` prefixes R/C with a similarity score (`R100`), so only the first character
        // is a status. A status git added after this was written, or an entry `--numstat` reports
        // that `--name-status` somehow does not, degrades to `modified` rather than failing the whole
        // attempt's diff record over a classification detail.
        return statuses.GetValueOrDefault(entry.Path) is { Length: > 0 } status
            ? status[0] switch
            {
                'A' => DiffChangeKinds.Added,
                'D' => DiffChangeKinds.Deleted,
                'R' or 'C' => DiffChangeKinds.Renamed,
                _ => DiffChangeKinds.Modified,
            }
            : entry.IsRename ? DiffChangeKinds.Renamed : DiffChangeKinds.Modified;
    }

    /// <summary>Keyed by the *new* path for a rename, matching how `--numstat -z` identifies the
    /// same entry.</summary>
    private static Dictionary<string, string> ParseNameStatus(string output)
    {
        Dictionary<string, string> statuses = new(StringComparer.Ordinal);
        string[] fields = output.Split('\0');
        for (int index = 0; index < fields.Length; index++)
        {
            string status = fields[index];
            if (status.Length == 0)
            {
                continue;
            }

            bool isRenameOrCopy = status[0] is 'R' or 'C';
            int pathIndex = index + (isRenameOrCopy ? 2 : 1);
            if (pathIndex >= fields.Length)
            {
                break;
            }

            statuses[fields[pathIndex]] = status;
            index = pathIndex;
        }

        return statuses;
    }

    private readonly record struct NumstatEntry(string Path, int Added, int Deleted, bool IsBinary, bool IsRename);

    private static List<NumstatEntry> ParseNumstat(string output)
    {
        List<NumstatEntry> entries = [];
        string[] fields = output.Split('\0');
        for (int index = 0; index < fields.Length; index++)
        {
            string field = fields[index];
            if (field.Length == 0)
            {
                continue;
            }

            int firstTab = field.IndexOf('\t');
            if (firstTab < 0)
            {
                continue;
            }

            int secondTab = field.IndexOf('\t', firstTab + 1);
            if (secondTab < 0)
            {
                continue;
            }

            string addedText = field[..firstTab];
            string deletedText = field[(firstTab + 1)..secondTab];
            string path = field[(secondTab + 1)..];

            // An empty remainder after the second tab is git's `-z` rename form: the old and new
            // paths follow as two further NUL-terminated fields. The new path is the entry's identity
            // (it is what `--name-status -z` keys on too).
            bool isRename = path.Length == 0;
            if (isRename)
            {
                if (index + 2 >= fields.Length)
                {
                    break;
                }

                path = fields[index + 2];
                index += 2;
            }

            if (path.Length == 0)
            {
                continue;
            }

            // `-`/`-` is git's "no textual diff" marker for a binary file.
            bool isBinary = addedText == "-" || deletedText == "-";
            entries.Add(new(
                path,
                isBinary ? 0 : ParseCount(addedText),
                isBinary ? 0 : ParseCount(deletedText),
                isBinary,
                isRename));
        }

        return entries;
    }

    private static int ParseCount(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : 0;
}
