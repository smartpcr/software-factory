using System.Diagnostics;
using System.Text.Json;

namespace GraphMemory.Tests;

/// <summary>
/// Verifies that gen_schema.py produces output structurally identical to the
/// checked-in json-schema/graph-memory.schema.json — prevents generator drift.
/// </summary>
public class SchemaGeneratorParityTests
{
    private static string RepoRoot => Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..");

    [Fact]
    public void Generated_Schema_Matches_Checked_In_Schema()
    {
        var checkedInPath = Path.Combine(RepoRoot, "json-schema", "graph-memory.schema.json");
        var checkedInText = File.ReadAllText(checkedInPath);
        var checkedIn = JsonDocument.Parse(checkedInText);

        // Run gen_schema.py to a temp file
        var tempDir = Path.Combine(Path.GetTempPath(), $"graph-memory-schema-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "json-schema"));

        var genScriptPath = Path.Combine(RepoRoot, "gen_schema.py");

        try
        {
            // Copy gen_schema.py to temp and run it there
            var tempScript = Path.Combine(tempDir, "gen_schema.py");
            File.Copy(genScriptPath, tempScript);
            Directory.CreateDirectory(Path.Combine(tempDir, "json-schema"));

            var psi = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = tempScript,
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            Assert.NotNull(process);
            process!.WaitForExit(30_000);
            Assert.Equal(0, process.ExitCode);

            var generatedPath = Path.Combine(tempDir, "json-schema", "graph-memory.schema.json");
            Assert.True(File.Exists(generatedPath), "gen_schema.py did not produce output");

            var generatedText = File.ReadAllText(generatedPath);
            var generated = JsonDocument.Parse(generatedText);

            // Structural comparison: normalize both to canonical JSON and compare
            var checkedInNorm = NormalizeJson(checkedIn.RootElement);
            var generatedNorm = NormalizeJson(generated.RootElement);

            Assert.Equal(checkedInNorm, generatedNorm);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* cleanup best-effort */ }
        }
    }

    [Fact]
    public void Schema_Has_Expected_Version()
    {
        var checkedInPath = Path.Combine(RepoRoot, "json-schema", "graph-memory.schema.json");
        var doc = JsonDocument.Parse(File.ReadAllText(checkedInPath));

        var version = doc.RootElement.GetProperty("properties")
            .GetProperty("schemaVersion").GetProperty("const").GetString();
        Assert.Equal("0.3.0", version);
    }

    [Fact]
    public void Schema_Has_All_Expected_Defs()
    {
        var checkedInPath = Path.Combine(RepoRoot, "json-schema", "graph-memory.schema.json");
        var doc = JsonDocument.Parse(File.ReadAllText(checkedInPath));
        var defs = doc.RootElement.GetProperty("$defs");

        string[] expectedDefs = [
            "GraphMetadata", "Provenance", "VersionRange", "Layer",
            "SourceLocation", "FunctionParameter", "SymbolKind",
            "CallSite", "ImportedSymbol", "InheritanceMechanism",
            "GraphNode", "GraphEdge"
        ];

        foreach (var def in expectedDefs)
        {
            Assert.True(defs.TryGetProperty(def, out _), $"Missing $defs/{def} in schema");
        }
    }

    private static string NormalizeJson(JsonElement element)
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        return JsonSerializer.Serialize(element, opts);
    }
}
