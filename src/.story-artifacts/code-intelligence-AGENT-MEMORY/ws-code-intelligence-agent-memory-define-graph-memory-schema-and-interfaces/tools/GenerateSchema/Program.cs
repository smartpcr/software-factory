using CodeIntelligence.GraphMemory.Serialization;

var schema = JsonSchemaGenerator.Generate();
var outputPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "schema", "code-knowledge-graph.schema.json");
outputPath = Path.GetFullPath(outputPath);

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath, schema);

Console.WriteLine($"Schema written to {outputPath}");
