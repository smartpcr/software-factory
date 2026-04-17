// -----------------------------------------------------------------------
// <copyright file="ShapeTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace GenContract.Tests;

using System.Text;
using System.Text.RegularExpressions;
using GenContract.Emitters;
using Xunit;

/// <summary>
/// Tests that assert the shape of the generated TypeScript output.
/// </summary>
public class ShapeTests
{
    private readonly string generatedTs;

    public ShapeTests()
    {
        var w = new TsWriter();

        var engineVersion = typeof(ExecutionEngine.Workflow.WorkflowDefinition)
            .Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

        w.Line("// GENERATED. Do not edit. Run tools/GenContract to regenerate.");
        w.Line($"// Engine assembly version: {engineVersion}");
        w.Line();

        var enumTypes = EnumEmitter.GetEnumTypes();
        EnumEmitter.Emit(w, enumTypes);
        EnumEmitter.EmitSupportingEnums(w);
        PolicyEmitter.Emit(w);
        DtoEmitter.Emit(w);
        ConnectionEmitter.Emit(w);

        this.generatedTs = w.ToString();
    }

    [Fact]
    public void NodeDtoBase_Has11Properties()
    {
        var match = Regex.Match(this.generatedTs, @"export interface NodeDtoBase \{([^}]+)\}");
        Assert.True(match.Success, "NodeDtoBase not found");
        var body = match.Groups[1].Value;
        var propCount = Regex.Matches(body, @"^\s+\w+\??: ", RegexOptions.Multiline).Count;
        // 11 base properties + 1 runtimeType discriminator = 12 total
        Assert.Equal(12, propCount);
    }

    [Fact]
    public void NodeDtoBase_DiscriminatorTypedAs_RuntimeType()
    {
        Assert.Contains("runtimeType: RuntimeType;", this.generatedTs);
    }

    [Fact]
    public void RetryPolicyDto_Has_7_Properties_In_Alphabetical_Order()
    {
        var match = Regex.Match(this.generatedTs, @"export interface RetryPolicyDto \{([^}]+)\}");
        Assert.True(match.Success);
        var body = match.Groups[1].Value;
        var props = Regex.Matches(body, @"^\s+(\w+)\??: ", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(7, props.Count);
        Assert.Equal(props.OrderBy(p => p, StringComparer.Ordinal).ToList(), props);
    }

    [Fact]
    public void CircuitBreakerPolicyDto_Has_4_Properties()
    {
        var match = Regex.Match(this.generatedTs, @"export interface CircuitBreakerPolicyDto \{([^}]+)\}");
        Assert.True(match.Success);
        var body = match.Groups[1].Value;
        var propCount = Regex.Matches(body, @"^\s+\w+\??: ", RegexOptions.Multiline).Count;
        Assert.Equal(4, propCount);
    }

    [Fact]
    public void NodeConnectionDto_Has_9_Properties()
    {
        var match = Regex.Match(this.generatedTs, @"export interface NodeConnectionDto \{([^}]+)\}");
        Assert.True(match.Success);
        var body = match.Groups[1].Value;
        var propCount = Regex.Matches(body, @"^\s+\w+\??: ", RegexOptions.Multiline).Count;
        Assert.Equal(9, propCount);
    }

    [Fact]
    public void Subflow_Has_Timeout_As_String_Comment_Mentions_ISO8601()
    {
        Assert.Contains("timeout: string; // ISO-8601 duration", this.generatedTs);
    }

    [Fact]
    public void Container_ChildNodes_Typed_As_NodeDto_Array()
    {
        Assert.Contains("childNodes?: NodeDto[];", this.generatedTs);
    }

    [Fact]
    public void Enum_RuntimeType_Emits_13_StringLiteralUnion_Members()
    {
        var match = Regex.Match(this.generatedTs, @"export type RuntimeType =\n((?:\s+\| '[^']+'\n?)+)");
        Assert.True(match.Success);
        var members = Regex.Matches(match.Groups[1].Value, @"'(\w+)'").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(13, members.Count);
    }

    [Fact]
    public void Enum_NodePriority_Emits_Low_Normal_High_NoCritical()
    {
        Assert.Contains("| 'Low'", this.generatedTs);
        Assert.Contains("| 'Normal'", this.generatedTs);
        Assert.Contains("| 'High'", this.generatedTs);
        Assert.DoesNotContain("'Critical'", this.generatedTs);
    }

    [Fact]
    public void CSharpTaskNodeDefinition_ScriptContent_ExecutorTypeName_ExecutorAssemblyPath_AllOptional()
    {
        var match = Regex.Match(this.generatedTs, @"export interface CSharpTaskNodeDto extends NodeDtoBase \{([^}]+)\}");
        Assert.True(match.Success);
        var body = match.Groups[1].Value;
        Assert.Contains("executorAssemblyPath?:", body);
        Assert.Contains("executorTypeName?:", body);
        Assert.Contains("scriptContent?:", body);
    }

    [Fact]
    public void NullableString_Emits_Optional_Property_Question_Mark()
    {
        Assert.Contains("description?:", this.generatedTs);
        Assert.Contains("compensationNodeId?:", this.generatedTs);
    }

    [Fact]
    public void EnumProperty_Emits_UnionName_Not_InlineEnum()
    {
        Assert.Contains("joinType: JoinType;", this.generatedTs);
        Assert.DoesNotContain("joinType: 'Any' | 'All';", this.generatedTs);
    }

    [Fact]
    public void Output_HasLF_LineEndings_Only()
    {
        Assert.DoesNotContain("\r\n", this.generatedTs);
        Assert.DoesNotContain("\r", this.generatedTs);
        Assert.Contains("\n", this.generatedTs);
    }

    [Fact]
    public void Output_Has_GENERATED_Header()
    {
        Assert.StartsWith("// GENERATED", this.generatedTs);
    }

    [Fact]
    public void Output_Has_EngineVersion_In_Header()
    {
        Assert.Contains("// Engine assembly version:", this.generatedTs);
    }
}
