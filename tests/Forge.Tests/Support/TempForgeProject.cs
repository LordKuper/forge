using System.Text;
using Forge.Application;

namespace Forge.Tests.Support;

/// <summary>A throwaway project root with an empty `.forge/` tree, for tests exercising
/// `Forge.Compiler` (ADR 0009/0010) without the full `TestEnvironment` DI graph.</summary>
internal sealed class TempForgeProject : IDisposable
{
    public TempForgeProject()
    {
        Root = Directory.CreateTempSubdirectory("forge-doc-tests-").FullName;
        ForgeRoot = ProjectRootResolver.ForgeDirectory(Root);
        Directory.CreateDirectory(ForgeRoot);
    }

    public string Root { get; }

    public string ForgeRoot { get; }

    public void WriteRule(string fileName, string content) => Write("rules", fileName, content);

    public void WriteKnowledge(string fileName, string content) => Write("knowledge", fileName, content);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a locked handle on a CI runner is not a test failure.
        }
    }

    private void Write(string directoryName, string fileName, string content)
    {
        string directory = Path.Combine(ForgeRoot, directoryName);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content, Encoding.UTF8);
    }
}
