// -----------------------------------------------------------------------
// <copyright file="LspEnrichmentServiceTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Lsp.UnitTests;

using Ast.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

[TestClass]
public class LspEnrichmentServiceTests
{
    [TestMethod]
    public void LspEnrichmentData_ShouldInitializeWithDefaults()
    {
        // Act
        var data = new LspEnrichmentData();

        // Assert
        data.Signature.Should().BeNull();
        data.ReturnType.Should().BeNull();
        data.Documentation.Should().BeNull();
        data.Parameters.Should().BeEmpty();
        data.Definitions.Should().BeEmpty();
        data.References.Should().BeEmpty();
        data.IncomingCalls.Should().BeEmpty();
        data.OutgoingCalls.Should().BeEmpty();
        data.Diagnostics.Should().BeEmpty();
    }

    [TestMethod]
    public void ParameterInfo_ShouldStoreValues()
    {
        // Arrange & Act
        var param = new ParameterInfo
        {
            Name = "input",
            Type = "string",
            HasDefault = true,
            DefaultValue = "null"
        };

        // Assert
        param.Name.Should().Be("input");
        param.Type.Should().Be("string");
        param.HasDefault.Should().BeTrue();
        param.DefaultValue.Should().Be("null");
    }

    [TestMethod]
    public async Task EnrichBatchAsync_ShouldReturnOriginalNodes_WhenLspDisabled()
    {
        // Arrange
        var config = new LspConfiguration { Enabled = false };
        var options = Options.Create(config);
        var loggerFactory = LoggerFactory.Create(builder => { });
        var manager = new LspServerManager(options, loggerFactory);
        var service = new LspEnrichmentService(manager, options, loggerFactory.CreateLogger<LspEnrichmentService>());

        var nodes = new List<HypergraphNode>
        {
            new HypergraphNode
            {
                Id = "node1",
                Name = "Test",
                NodeType = "Method",
                Language = "C#",
                FilePath = "test.cs",
                LineStart = 1
            }
        };

        // Act
        var result = await service.EnrichBatchAsync(nodes, @"C:\workspace");

        // Assert
        result.Should().BeEquivalentTo(nodes);
    }

    [TestMethod]
    public async Task EnrichBatchAsync_ShouldReturnEmpty_WhenNoNodes()
    {
        // Arrange
        var config = new LspConfiguration { Enabled = true };
        var options = Options.Create(config);
        var loggerFactory = LoggerFactory.Create(builder => { });
        var manager = new LspServerManager(options, loggerFactory);
        var service = new LspEnrichmentService(manager, options, loggerFactory.CreateLogger<LspEnrichmentService>());

        // Act
        var result = await service.EnrichBatchAsync([], @"C:\workspace");

        // Assert
        result.Should().BeEmpty();
    }
}
