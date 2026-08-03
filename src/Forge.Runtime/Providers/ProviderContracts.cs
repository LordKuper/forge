namespace Forge.Providers;

public enum ProviderKind
{
    Codex,
    ClaudeCode,
}

public interface IProviderAdapter
{
    ProviderKind Kind { get; }
}

public interface IProviderToolchainManager;
