using System.Text.RegularExpressions;
using GraphMemory.Models;
using GraphMemory.Serialization;

namespace GraphMemory.Tests;

/// <summary>
/// Verifies that TypeScript declarations in graph-memory.d.ts match the C#
/// models and JSON Schema — catches drift between the three contract surfaces.
/// </summary>
public class TypeScriptParityTests
{
    private static readonly string TsContent;

    static TypeScriptParityTests()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "GraphMemory", "TypeScript", "graph-memory.d.ts");
        TsContent = File.ReadAllText(path);
    }

    // -----------------------------------------------------------------------
    // Discriminated union: every NodeKind has a typed variant
    // -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllNodeKinds))]
    public void TypeScript_Has_Discriminated_Node_Variant(NodeKind kind, string wireKind)
    {
        // Each kind should have a typed variant like: { kind: "file"; properties: FileProperties }
        var pattern = $"kind: \"{wireKind}\"";
        Assert.Contains(pattern, TsContent);
    }

    [Theory]
    [MemberData(nameof(AllEdgeKinds))]
    public void TypeScript_Has_Discriminated_Edge_Variant(EdgeKind kind, string wireKind)
    {
        var pattern = $"kind: \"{wireKind}\"";
        Assert.Contains(pattern, TsContent);
    }

    // -----------------------------------------------------------------------
    // Verify discriminated union structure
    // -----------------------------------------------------------------------

    [Fact]
    public void TypeScript_GraphNode_Is_Union_Of_Typed_Variants()
    {
        // GraphNode should be a type alias (not interface) that is a union
        Assert.Contains("export type GraphNode =", TsContent);
        Assert.Contains("RepoNode", TsContent);
        Assert.Contains("FileNode", TsContent);
        Assert.Contains("ClassNode", TsContent);
        Assert.Contains("FunctionNode", TsContent);
        Assert.Contains("MethodNode", TsContent);
        Assert.Contains("WorkspaceNode", TsContent);
    }

    [Fact]
    public void TypeScript_GraphEdge_Is_Union_Of_Typed_Variants()
    {
        Assert.Contains("export type GraphEdge =", TsContent);
        Assert.Contains("ContainsEdge", TsContent);
        Assert.Contains("CallsEdge", TsContent);
        Assert.Contains("ExpertiseEdge", TsContent);
        Assert.Contains("SimilarToEdge", TsContent);
    }

    // -----------------------------------------------------------------------
    // Property-level parity: TS interface properties match C# wire names
    // -----------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(NodePropertyBags))]
    public void TypeScript_Node_Properties_Interface_Has_All_CSharp_Fields(NodeKind kind, Type propsType, string tsInterfaceName)
    {
        var csharpFields = GetCSharpWireNames(propsType);
        var tsBlock = ExtractTsInterfaceBlock(tsInterfaceName);
        Assert.NotNull(tsBlock);

        foreach (var field in csharpFields)
        {
            Assert.True(tsBlock!.Contains(field),
                $"TS interface {tsInterfaceName} missing field '{field}' from C# {propsType.Name}");
        }
    }

    [Theory]
    [MemberData(nameof(EdgePropertyBags))]
    public void TypeScript_Edge_Properties_Interface_Has_All_CSharp_Fields(EdgeKind kind, Type propsType, string tsInterfaceName)
    {
        var csharpFields = GetCSharpWireNames(propsType);
        var tsBlock = ExtractTsInterfaceBlock(tsInterfaceName);
        Assert.NotNull(tsBlock);

        foreach (var field in csharpFields)
        {
            Assert.True(tsBlock!.Contains(field),
                $"TS interface {tsInterfaceName} missing field '{field}' from C# {propsType.Name}");
        }
    }

    // -----------------------------------------------------------------------
    // IGraphStore parity: TS has findByName and getNodeAtCommit
    // -----------------------------------------------------------------------

    [Fact]
    public void TypeScript_IGraphStore_Has_FindByName()
    {
        Assert.Contains("findByName(", TsContent);
    }

    [Fact]
    public void TypeScript_IGraphStore_Has_GetNodeAtCommit()
    {
        Assert.Contains("getNodeAtCommit(", TsContent);
    }

    // -----------------------------------------------------------------------
    // Supporting type parity
    // -----------------------------------------------------------------------

    [Fact]
    public void TypeScript_Has_All_Supporting_Interfaces()
    {
        Assert.Contains("export interface Provenance", TsContent);
        Assert.Contains("export interface VersionRange", TsContent);
        Assert.Contains("export interface SourceLocation", TsContent);
        Assert.Contains("export interface FunctionParameter", TsContent);
        Assert.Contains("export interface CallSite", TsContent);
        Assert.Contains("export interface ImportedSymbol", TsContent);
        Assert.Contains("export interface ExpertiseEvidence", TsContent);
        Assert.Contains("export interface GraphMetadata", TsContent);
    }

    [Fact]
    public void TypeScript_VersionRange_Has_Correct_Fields()
    {
        var block = ExtractTsInterfaceBlock("VersionRange");
        Assert.NotNull(block);
        Assert.Contains("introducedIn:", block!);
        Assert.Contains("lastSeenIn:", block);
        Assert.Contains("removedIn?:", block);
    }

    [Fact]
    public void TypeScript_SimilarTo_Uses_Cosine_Similarity_Naming()
    {
        var block = ExtractTsInterfaceBlock("SimilarToProperties");
        Assert.NotNull(block);
        Assert.Contains("cosine_similarity:", block!);
        Assert.DoesNotContain("cosineSimilarity:", block);
    }

    // -----------------------------------------------------------------------
    // MemberData providers
    // -----------------------------------------------------------------------

    public static IEnumerable<object[]> AllNodeKinds()
    {
        foreach (var (kind, wire) in SnakeCaseEnumMaps.NodeKindToString)
            yield return [kind, wire];
    }

    public static IEnumerable<object[]> AllEdgeKinds()
    {
        foreach (var (kind, wire) in SnakeCaseEnumMaps.EdgeKindToString)
            yield return [kind, wire];
    }

    public static IEnumerable<object[]> NodePropertyBags()
    {
        yield return [NodeKind.Repo, typeof(RepoProperties), "RepoProperties"];
        yield return [NodeKind.Package, typeof(PackageProperties), "PackageProperties"];
        yield return [NodeKind.Module, typeof(ModuleProperties), "ModuleProperties"];
        yield return [NodeKind.Directory, typeof(DirectoryProperties), "DirectoryProperties"];
        yield return [NodeKind.File, typeof(FileProperties), "FileProperties"];
        yield return [NodeKind.Class, typeof(ClassProperties), "ClassProperties"];
        yield return [NodeKind.Interface, typeof(InterfaceProperties), "InterfaceProperties"];
        yield return [NodeKind.TypeAlias, typeof(TypeAliasProperties), "TypeAliasProperties"];
        yield return [NodeKind.Function, typeof(FunctionProperties), "FunctionProperties"];
        yield return [NodeKind.Method, typeof(MethodProperties), "MethodProperties"];
        yield return [NodeKind.Variable, typeof(VariableProperties), "VariableProperties"];
        yield return [NodeKind.Enum, typeof(EnumProperties), "EnumProperties"];
        yield return [NodeKind.Symbol, typeof(SymbolProperties), "SymbolProperties"];
        yield return [NodeKind.SemanticCluster, typeof(SemanticClusterProperties), "SemanticClusterProperties"];
        yield return [NodeKind.Topic, typeof(TopicProperties), "TopicProperties"];
        yield return [NodeKind.ExpertiseNote, typeof(ExpertiseNoteProperties), "ExpertiseNoteProperties"];
        yield return [NodeKind.Pattern, typeof(PatternProperties), "PatternProperties"];
        yield return [NodeKind.Pitfall, typeof(PitfallProperties), "PitfallProperties"];
        yield return [NodeKind.Preference, typeof(PreferenceProperties), "PreferenceProperties"];
        yield return [NodeKind.Insight, typeof(InsightProperties), "InsightProperties"];
        yield return [NodeKind.Workspace, typeof(WorkspaceProperties), "WorkspaceProperties"];
    }

    public static IEnumerable<object[]> EdgePropertyBags()
    {
        yield return [EdgeKind.Contains, typeof(ContainsProperties), "ContainsProperties"];
        yield return [EdgeKind.Calls, typeof(CallsProperties), "CallsProperties"];
        yield return [EdgeKind.CalledBy, typeof(CalledByProperties), "CalledByProperties"];
        yield return [EdgeKind.Imports, typeof(ImportsProperties), "ImportsProperties"];
        yield return [EdgeKind.ReExports, typeof(ReExportsProperties), "ReExportsProperties"];
        yield return [EdgeKind.Exports, typeof(ExportsProperties), "ExportsProperties"];
        yield return [EdgeKind.Inherits, typeof(InheritsProperties), "InheritsProperties"];
        yield return [EdgeKind.Overrides, typeof(OverridesProperties), "OverridesProperties"];
        yield return [EdgeKind.Implements, typeof(ImplementsProperties), "ImplementsProperties"];
        yield return [EdgeKind.Instantiates, typeof(InstantiatesProperties), "InstantiatesProperties"];
        yield return [EdgeKind.Expertise, typeof(ExpertiseProperties), "ExpertiseEdgeProperties"];
        yield return [EdgeKind.AppliesTo, typeof(AppliesToProperties), "AppliesToProperties"];
        yield return [EdgeKind.SimilarTo, typeof(SimilarToProperties), "SimilarToProperties"];
        yield return [EdgeKind.BelongsToCluster, typeof(BelongsToClusterProperties), "BelongsToClusterProperties"];
        yield return [EdgeKind.Supersedes, typeof(SupersedesProperties), "SupersedesProperties"];
        yield return [EdgeKind.DerivedFrom, typeof(DerivedFromProperties), "DerivedFromProperties"];
        yield return [EdgeKind.FrequentlyUsedWith, typeof(FrequentlyUsedWithProperties), "FrequentlyUsedWithProperties"];
        yield return [EdgeKind.DependsOn, typeof(DependsOnProperties), "DependsOnProperties"];
        yield return [EdgeKind.References, typeof(ReferencesProperties), "ReferencesProperties"];
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static List<string> GetCSharpWireNames(Type type)
    {
        var names = new List<string>();
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.GetMethod == null) continue;
            var attr = prop.GetCustomAttributes(false)
                .OfType<System.Text.Json.Serialization.JsonPropertyNameAttribute>()
                .FirstOrDefault();
            var wireName = attr?.Name ?? System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            names.Add(wireName);
        }
        return names;
    }

    private static string? ExtractTsInterfaceBlock(string interfaceName)
    {
        // Match "export interface Foo {" or "export interface Foo<T> {" through the closing "}"
        var pattern = $@"export interface {Regex.Escape(interfaceName)}\b[^{{]*\{{[^}}]*\}}";
        var match = Regex.Match(TsContent, pattern, RegexOptions.Singleline);
        return match.Success ? match.Value : null;
    }
}
