using System.Globalization;

namespace Forge.Updater;

public sealed record SemanticVersion : IComparable<SemanticVersion>
{
    private SemanticVersion(int major, int minor, int patch, string? prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }

    public int Minor { get; }

    public int Patch { get; }

    public string? Prerelease { get; }

    public bool IsStable => Prerelease is null;

    public static SemanticVersion Parse(string value)
    {
        if (!TryParse(value, out SemanticVersion? version))
        {
            throw new FormatException($"'{value}' is not a valid Semantic Version.");
        }

        return version!;
    }

    public static bool TryParse(string? value, out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string candidate = value.Trim();
        if (candidate.StartsWith('v'))
        {
            candidate = candidate[1..];
        }

        string[] buildParts = candidate.Split('+', 2, StringSplitOptions.None);
        string[] prereleaseParts = buildParts[0].Split('-', 2, StringSplitOptions.None);
        string[] numbers = prereleaseParts[0].Split('.', StringSplitOptions.None);
        if (numbers.Length != 3 ||
            !TryParseIdentifier(numbers[0], out int major) ||
            !TryParseIdentifier(numbers[1], out int minor) ||
            !TryParseIdentifier(numbers[2], out int patch))
        {
            return false;
        }

        string? prerelease = prereleaseParts.Length == 2 ? prereleaseParts[1] : null;
        if (prerelease is not null &&
            (prerelease.Length == 0 || prerelease.Split('.').Any(part => !IsValidPrereleasePart(part))))
        {
            return false;
        }

        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        ArgumentNullException.ThrowIfNull(other);
        int numeric = Major.CompareTo(other.Major);
        numeric = numeric != 0 ? numeric : Minor.CompareTo(other.Minor);
        numeric = numeric != 0 ? numeric : Patch.CompareTo(other.Patch);
        if (numeric != 0 || string.Equals(Prerelease, other.Prerelease, StringComparison.Ordinal))
        {
            return numeric;
        }

        if (Prerelease is null)
        {
            return 1;
        }

        if (other.Prerelease is null)
        {
            return -1;
        }

        string[] left = Prerelease.Split('.');
        string[] right = other.Prerelease.Split('.');
        for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            int segment = ComparePrereleasePart(left[index], right[index]);
            if (segment != 0)
            {
                return segment;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) < 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) > 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) =>
        left.CompareTo(right) >= 0;

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}{(Prerelease is null ? string.Empty : $"-{Prerelease}")}";

    private static bool TryParseIdentifier(string value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) &&
        !(value.Length > 1 && value[0] == '0');

    private static bool IsValidPrereleasePart(string value) =>
        value.Length > 0 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') &&
        !(value.Length > 1 && value[0] == '0' && value.All(char.IsAsciiDigit));

    private static int ComparePrereleasePart(string left, string right)
    {
        bool leftNumeric = left.All(char.IsAsciiDigit);
        bool rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            return int.Parse(left, CultureInfo.InvariantCulture).CompareTo(
                int.Parse(right, CultureInfo.InvariantCulture));
        }

        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return string.CompareOrdinal(left, right);
    }
}
