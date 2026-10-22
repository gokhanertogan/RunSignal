using System.Text.Json;

namespace RunSignal.Tests;

/// <summary>Reads the cross-language conformance vectors in spec/conformance.</summary>
internal static class Conformance
{
    public static JsonElement Load(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "spec", "conformance")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("spec/conformance was not found above the test output directory.");

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "spec", "conformance", file)));
        return json.RootElement.Clone();
    }
}
