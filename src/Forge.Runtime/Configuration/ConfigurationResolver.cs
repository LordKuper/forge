using System.Text.Json;

namespace Forge.Configuration;

public sealed class ConfigurationResolver(IConfigurationRegistry registry)
{
    public EffectiveConfigurationValue ResolveUser(
        string key,
        IReadOnlyDictionary<string, JsonElement> session,
        ConfigurationDocument user)
    {
        ConfigurationKey descriptor = registry.FindRequired(key);
        registry.RequireScope(key, ConfigurationScope.User);

        if (session.TryGetValue(key, out JsonElement sessionValue))
        {
            if (!descriptor.AllowsSessionOverride)
            {
                throw new InvalidOperationException($"Session override is forbidden for '{key}'.");
            }

            return new(key, sessionValue, ConfigurationProvenance.Session);
        }

        if (user.Values.TryGetValue(key, out JsonElement userValue))
        {
            return new(key, userValue, ConfigurationProvenance.User);
        }

        if (descriptor.Inherits is not null)
        {
            EffectiveConfigurationValue inherited = ResolveUser(descriptor.Inherits, session, user);
            return new(key, inherited.Value, ConfigurationProvenance.Inherited);
        }

        return new(
            key,
            descriptor.DefaultValue ?? throw new InvalidOperationException($"No default for '{key}'."),
            ConfigurationProvenance.BuiltInDefault);
    }

    public EffectiveConfigurationValue ResolveProject(
        string key,
        ConfigurationDocument project)
    {
        ConfigurationKey descriptor = registry.FindRequired(key);
        registry.RequireScope(key, ConfigurationScope.Project);
        return project.Values.TryGetValue(key, out JsonElement value)
            ? new(key, value, ConfigurationProvenance.Project)
            : new(
                key,
                descriptor.DefaultValue ??
                    throw new InvalidOperationException($"No default for '{key}'."),
                ConfigurationProvenance.BuiltInDefault);
    }
}
