namespace Forge.Providers;

/// <summary>
/// ADR 0006: "Provider children receive a minimal environment assembled by Forge. A frozen
/// provider environment contract allowlists required platform, home/temp, locale, proxy,
/// toolchain, and provider-authentication variables. Project content cannot add variable names or
/// values. Known nested-session markers and credentials for other providers are removed." This is
/// the one place that allowlist is defined; every adapter builds its child environment through
/// <see cref="BuildMinimalEnvironment"/> instead of inheriting the host process's own environment.
/// Exclusion is by omission: a nested-session marker (e.g. `CLAUDECODE`, `CI`) or another
/// provider's credential is simply never in the allowlist below, so it can never reach a child no
/// matter what the current host process happens to have set.
/// </summary>
public static class ProviderEnvironmentPolicy
{
    /// <summary>Platform and home/temp variables a native executable needs to resolve paths and
    /// run at all, on any of Forge's supported operating systems — never provider-specific.</summary>
    private static readonly IReadOnlyList<string> PlatformVariableNames =
    [
        // Windows
        "SystemRoot", "SystemDrive", "windir", "ComSpec", "PATHEXT",
        "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA",

        // POSIX
        "SHELL", "TERM", "HOME",
        "XDG_CACHE_HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME",

        // Cross-platform
        "PATH", "TEMP", "TMP", "TMPDIR", "USER", "USERNAME",
    ];

    private static readonly IReadOnlyList<string> LocaleVariableNames =
        ["LANG", "LANGUAGE", "LC_ALL", "LC_CTYPE"];

    private static readonly IReadOnlyList<string> ProxyVariableNames =
    [
        "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy",
        "NO_PROXY", "no_proxy", "ALL_PROXY", "all_proxy",
    ];

    /// <summary>Both official vendor CLIs are Node-based toolchains; without these, module
    /// resolution and TLS trust can silently misbehave in a minimal environment.</summary>
    private static readonly IReadOnlyList<string> ToolchainVariableNames =
        ["NODE_OPTIONS", "NODE_EXTRA_CA_CERTS", "NPM_CONFIG_PREFIX"];

    /// <summary>
    /// Builds the complete frozen environment for one provider child process: the fixed
    /// cross-platform allowlist above, resolved from the current host process, plus
    /// <paramref name="authenticationVariableNames"/> (the one vendor-specific category ADR 0006
    /// allows — each adapter names only its own provider's authentication variables, never
    /// another provider's). <paramref name="overrides"/> layers Forge-owned values (e.g. an
    /// updater opt-out flag) on top; overrides are never sourced from project content.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildMinimalEnvironment(
        IReadOnlyList<string> authenticationVariableNames,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(authenticationVariableNames);
        Dictionary<string, string> environment = new(StringComparer.Ordinal);
        foreach (string name in PlatformVariableNames
            .Concat(LocaleVariableNames)
            .Concat(ProxyVariableNames)
            .Concat(ToolchainVariableNames)
            .Concat(authenticationVariableNames))
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (value is not null)
            {
                environment[name] = value;
            }
        }

        if (overrides is not null)
        {
            foreach ((string key, string value) in overrides)
            {
                environment[key] = value;
            }
        }

        return environment;
    }
}
