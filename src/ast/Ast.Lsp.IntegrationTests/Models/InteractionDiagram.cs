// -----------------------------------------------------------------------
// <copyright file="InteractionDiagram.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Lsp.IntegrationTests.Models;

/// <summary>
/// Interaction diagram showing module relationships (for PowerShell repos).
/// </summary>
public class InteractionDiagram
{
    /// <summary>
    /// Gets or sets the repository name.
    /// </summary>
    public string RepoName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the diagram was generated.
    /// </summary>
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Gets or sets the modules in the repository.
    /// </summary>
    public List<ModuleInfo> Modules { get; set; } = [];

    /// <summary>
    /// Gets or sets the interactions between modules.
    /// </summary>
    public List<ModuleInteraction> Interactions { get; set; } = [];

    /// <summary>
    /// Gets or sets the Mermaid sequence diagram.
    /// </summary>
    public string MermaidSequence { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Mermaid class diagram (for inheritance).
    /// </summary>
    public string MermaidClass { get; set; } = string.Empty;

    /// <summary>
    /// Generates Mermaid diagrams from modules and interactions.
    /// </summary>
    public void GenerateMermaid()
    {
        this.GenerateSequenceDiagram();
        this.GenerateClassDiagram();
    }

    private void GenerateSequenceDiagram()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("sequenceDiagram");

        // Add participants
        foreach (var module in this.Modules.Take(20)) // Limit for readability
        {
            sb.AppendLine($"    participant {SanitizeId(module.Name)} as {module.Name}");
        }

        // Add interactions
        foreach (var interaction in this.Interactions.Where(i =>
            i.InteractionType is ModuleInteractionType.Calls or ModuleInteractionType.Imports).Take(50))
        {
            var arrow = interaction.InteractionType switch
            {
                ModuleInteractionType.Calls => "->>",
                ModuleInteractionType.Imports => "-->>",
                _ => "->>"
            };
            var label = !string.IsNullOrEmpty(interaction.Symbol)
                ? interaction.Symbol
                : interaction.InteractionType.ToString();
            sb.AppendLine($"    {SanitizeId(interaction.FromModule)}{arrow}{SanitizeId(interaction.ToModule)}: {label}");
        }

        this.MermaidSequence = sb.ToString();
    }

    private void GenerateClassDiagram()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("classDiagram");

        // Add classes for modules with class definitions
        foreach (var module in this.Modules.Where(m => m.Classes.Count > 0).Take(20))
        {
            foreach (var cls in module.Classes)
            {
                sb.AppendLine($"    class {SanitizeId(cls.Name)} {{");
                foreach (var method in cls.Methods.Take(5))
                {
                    sb.AppendLine($"        +{method}()");
                }
                sb.AppendLine("    }");
            }
        }

        // Add inheritance relationships
        foreach (var interaction in this.Interactions.Where(i =>
            i.InteractionType == ModuleInteractionType.Inherits).Take(30))
        {
            sb.AppendLine($"    {SanitizeId(interaction.ToModule)} <|-- {SanitizeId(interaction.FromModule)}");
        }

        this.MermaidClass = sb.ToString();
    }

    private static string SanitizeId(string id)
    {
        return id.Replace(".", "_").Replace("-", "_").Replace(" ", "_").Replace("/", "_");
    }
}

/// <summary>
/// Information about a PowerShell module.
/// </summary>
public class ModuleInfo
{
    /// <summary>
    /// Gets or sets the module name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the file path.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the module version.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the exported functions.
    /// </summary>
    public List<string> ExportedFunctions { get; set; } = [];

    /// <summary>
    /// Gets or sets the exported cmdlets.
    /// </summary>
    public List<string> ExportedCmdlets { get; set; } = [];

    /// <summary>
    /// Gets or sets the classes defined in the module.
    /// </summary>
    public List<ClassInfo> Classes { get; set; } = [];

    /// <summary>
    /// Gets or sets the required modules.
    /// </summary>
    public List<string> RequiredModules { get; set; } = [];

    /// <summary>
    /// Gets or sets the modules imported via 'using module'.
    /// </summary>
    public List<string> UsingModules { get; set; } = [];
}

/// <summary>
/// Class information within a module.
/// </summary>
public class ClassInfo
{
    /// <summary>
    /// Gets or sets the class name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the base class name.
    /// </summary>
    public string? BaseClass { get; set; }

    /// <summary>
    /// Gets or sets the method names.
    /// </summary>
    public List<string> Methods { get; set; } = [];

    /// <summary>
    /// Gets or sets the property names.
    /// </summary>
    public List<string> Properties { get; set; } = [];
}

/// <summary>
/// Interaction between two modules.
/// </summary>
public class ModuleInteraction
{
    /// <summary>
    /// Gets or sets the source module name.
    /// </summary>
    public string FromModule { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the target module name.
    /// </summary>
    public string ToModule { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the interaction type.
    /// </summary>
    public ModuleInteractionType InteractionType { get; set; }

    /// <summary>
    /// Gets or sets the symbol involved (function name, class name, etc.).
    /// </summary>
    public string? Symbol { get; set; }

    /// <summary>
    /// Gets or sets the file path where the interaction occurs.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Gets or sets the line number.
    /// </summary>
    public int Line { get; set; }
}

/// <summary>
/// Type of module interaction.
/// </summary>
public enum ModuleInteractionType
{
    /// <summary>Imports module (Import-Module).</summary>
    Imports,

    /// <summary>Uses module (using module).</summary>
    UsingModule,

    /// <summary>Inherits from class in another module.</summary>
    Inherits,

    /// <summary>Calls function from another module.</summary>
    Calls,

    /// <summary>References type from another module.</summary>
    References
}