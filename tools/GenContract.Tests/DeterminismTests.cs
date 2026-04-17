// -----------------------------------------------------------------------
// <copyright file="DeterminismTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace GenContract.Tests;

using System.Text;
using GenContract.Emitters;
using Xunit;

/// <summary>
/// Tests that the generator produces deterministic output.
/// </summary>
public class DeterminismTests
{
    [Fact]
    public void Run2x_ByteIdentical()
    {
        var bytes1 = GenerateToBytes();
        var bytes2 = GenerateToBytes();
        Assert.Equal(bytes1, bytes2);
    }

    private static byte[] GenerateToBytes()
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

        return Encoding.UTF8.GetBytes(w.ToString());
    }
}
