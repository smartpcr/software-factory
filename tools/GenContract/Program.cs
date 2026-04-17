// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using GenContract.Emitters;

var outputPath = "src/WorkflowUI/src/engine-contract.generated.ts";

// Parse --output flag
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--output")
    {
        outputPath = args[i + 1];
        break;
    }
}

var engineVersion = typeof(ExecutionEngine.Workflow.WorkflowDefinition)
    .Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

var w = new TsWriter();

// File header
w.Line("// GENERATED. Do not edit. Run tools/GenContract to regenerate.");
w.Line($"// Engine assembly version: {engineVersion}");
w.Line();

// 1. Enums (7 parity enums matching engine-enums.json fixture)
var enumTypes = EnumEmitter.GetEnumTypes();
EnumEmitter.Emit(w, enumTypes);

// 2. Policy DTOs (includes supporting enum types like RetryStrategy)
PolicyEmitter.Emit(w);

// 3. Node DTOs (base + kinds + union)
DtoEmitter.Emit(w);

// 4. NodeConnectionDto
ConnectionEmitter.Emit(w);

// 5. WorkflowDefinitionDto (forward reference for SubflowNodeDefinition)
w.Line("export interface WorkflowDefinitionDto {");
w.Indent();
w.Line("workflowId?: string;");
w.Line("workflowName?: string;");
w.Line("description?: string;");
w.Line("version?: string;");
w.Line("nodes?: NodeDto[];");
w.Line("connections?: NodeConnectionDto[];");
w.Line("metadata?: Record<string, string>;");
w.Line("entryPointNodeId?: string;");
w.Line("maxConcurrency?: number;");
w.Line("allowPause?: boolean;");
w.Line("timeoutSeconds?: number;");
w.Line("defaultVariables?: Record<string, string>;");
w.Dedent();
w.Line("}");
w.Line();

// Write output
var content = w.ToString();
var dir = Path.GetDirectoryName(outputPath);
if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
{
    Directory.CreateDirectory(dir);
}

// Write with LF line endings and UTF-8 no BOM
await File.WriteAllTextAsync(outputPath, content, new UTF8Encoding(false));

Console.WriteLine($"Emitted {content.Length} bytes to {outputPath}; engine v{engineVersion}; " +
    $"enums={enumTypes.Count}; dtos={DtoEmitter.GetKindTypes().Count + 1}");

return 0;
