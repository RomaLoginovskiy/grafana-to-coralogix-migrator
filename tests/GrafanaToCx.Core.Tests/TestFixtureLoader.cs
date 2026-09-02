using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Tests;

internal static class TestFixtureLoader
{
    public static JObject LoadFixture(string fileName)
    {
        var fixturePath = Path.Combine(
            RepositoryRoot(),
            "tests", "GrafanaToCx.Core.Tests", "Fixtures", fileName);

        var json = File.ReadAllText(fixturePath);
        return JObject.Parse(json);
    }

    /// <summary>Walks up from the test output directory to the directory holding the solution file.</summary>
    public static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GrafanaToCx.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate GrafanaToCx.sln above '{AppContext.BaseDirectory}' to resolve test fixtures.");
    }
}
