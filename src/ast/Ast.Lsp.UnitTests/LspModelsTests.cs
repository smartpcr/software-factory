// -----------------------------------------------------------------------
// <copyright file="LspModelsTests.cs" company="Microsoft Corp.">
//     Copyright (c) Microsoft Corp. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace Ast.Lsp.UnitTests;

using System.Text.Json;

[TestClass]
public class LspModelsTests
{
    [TestMethod]
    public void Position_ShouldSerializeCorrectly()
    {
        // Arrange
        var position = new Position(10, 5);

        // Act
        var json = JsonSerializer.Serialize(position);
        var deserialized = JsonSerializer.Deserialize<Position>(json);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Line.Should().Be(10);
        deserialized.Character.Should().Be(5);
    }

    [TestMethod]
    public void Range_ShouldSerializeCorrectly()
    {
        // Arrange
        var range = new Range(
            new Position(1, 0),
            new Position(5, 20));

        // Act
        var json = JsonSerializer.Serialize(range);
        var deserialized = JsonSerializer.Deserialize<Range>(json);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Start.Line.Should().Be(1);
        deserialized.Start.Character.Should().Be(0);
        deserialized.End.Line.Should().Be(5);
        deserialized.End.Character.Should().Be(20);
    }

    [TestMethod]
    public void Location_ShouldSerializeCorrectly()
    {
        // Arrange
        var location = new Location(
            "file:///c:/test/file.cs",
            new Range(new Position(10, 5), new Position(10, 15)));

        // Act
        var json = JsonSerializer.Serialize(location);
        var deserialized = JsonSerializer.Deserialize<Location>(json);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Uri.Should().Be("file:///c:/test/file.cs");
        deserialized.Range.Start.Line.Should().Be(10);
    }

    [TestMethod]
    public void HoverResult_ShouldDeserializeCorrectly()
    {
        // Arrange
        var json = """
        {
            "contents": {
                "kind": "markdown",
                "value": "```csharp\nint Add(int a, int b)\n```\nAdds two numbers."
            },
            "range": {
                "start": { "line": 5, "character": 4 },
                "end": { "line": 5, "character": 7 }
            }
        }
        """;

        // Act
        var hover = JsonSerializer.Deserialize<HoverResult>(json);

        // Assert
        hover.Should().NotBeNull();
        hover!.Contents.Should().NotBeNull();
        hover.Contents!.Kind.Should().Be("markdown");
        hover.Contents.Value.Should().Contain("int Add");
        hover.Range.Should().NotBeNull();
        hover.Range!.Start.Line.Should().Be(5);
    }

    [TestMethod]
    public void DocumentSymbol_ShouldDeserializeCorrectly()
    {
        // Arrange
        var json = """
        {
            "name": "MyClass",
            "detail": "class MyClass",
            "kind": 5,
            "range": {
                "start": { "line": 0, "character": 0 },
                "end": { "line": 50, "character": 1 }
            },
            "selectionRange": {
                "start": { "line": 0, "character": 13 },
                "end": { "line": 0, "character": 20 }
            },
            "children": [
                {
                    "name": "DoSomething",
                    "kind": 6,
                    "range": {
                        "start": { "line": 10, "character": 4 },
                        "end": { "line": 15, "character": 5 }
                    },
                    "selectionRange": {
                        "start": { "line": 10, "character": 16 },
                        "end": { "line": 10, "character": 27 }
                    }
                }
            ]
        }
        """;

        // Act
        var symbol = JsonSerializer.Deserialize<DocumentSymbol>(json);

        // Assert
        symbol.Should().NotBeNull();
        symbol!.Name.Should().Be("MyClass");
        symbol.Kind.Should().Be(SymbolKind.Class);
        symbol.Children.Should().HaveCount(1);
        symbol.Children![0].Name.Should().Be("DoSomething");
        symbol.Children[0].Kind.Should().Be(SymbolKind.Method);
    }

    [TestMethod]
    public void Diagnostic_ShouldDeserializeCorrectly()
    {
        // Arrange
        var json = """
        {
            "range": {
                "start": { "line": 10, "character": 8 },
                "end": { "line": 10, "character": 20 }
            },
            "severity": 1,
            "code": "CS0168",
            "source": "csharp",
            "message": "The variable 'x' is declared but never used"
        }
        """;

        // Act
        var diagnostic = JsonSerializer.Deserialize<Diagnostic>(json);

        // Assert
        diagnostic.Should().NotBeNull();
        diagnostic!.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.Code.Should().Be("CS0168");
        diagnostic.Source.Should().Be("csharp");
        diagnostic.Message.Should().Contain("never used");
    }

    [TestMethod]
    public void CallHierarchyItem_ShouldDeserializeCorrectly()
    {
        // Arrange
        var json = """
        {
            "name": "ProcessData",
            "kind": 6,
            "detail": "void ProcessData(string input)",
            "uri": "file:///c:/test/service.cs",
            "range": {
                "start": { "line": 20, "character": 4 },
                "end": { "line": 30, "character": 5 }
            },
            "selectionRange": {
                "start": { "line": 20, "character": 16 },
                "end": { "line": 20, "character": 27 }
            }
        }
        """;

        // Act
        var item = JsonSerializer.Deserialize<CallHierarchyItem>(json);

        // Assert
        item.Should().NotBeNull();
        item!.Name.Should().Be("ProcessData");
        item.Kind.Should().Be(SymbolKind.Method);
        item.Detail.Should().Contain("void ProcessData");
    }

    [TestMethod]
    public void CallHierarchyIncomingCall_ShouldDeserializeCorrectly()
    {
        // Arrange
        var json = """
        {
            "from": {
                "name": "CallMethod",
                "kind": 6,
                "uri": "file:///c:/test/caller.cs",
                "range": {
                    "start": { "line": 5, "character": 0 },
                    "end": { "line": 10, "character": 1 }
                },
                "selectionRange": {
                    "start": { "line": 5, "character": 10 },
                    "end": { "line": 5, "character": 20 }
                }
            },
            "fromRanges": [
                {
                    "start": { "line": 7, "character": 8 },
                    "end": { "line": 7, "character": 19 }
                }
            ]
        }
        """;

        // Act
        var call = JsonSerializer.Deserialize<CallHierarchyIncomingCall>(json);

        // Assert
        call.Should().NotBeNull();
        call!.From.Should().NotBeNull();
        call.From.Name.Should().Be("CallMethod");
        call.FromRanges.Should().HaveCount(1);
    }

    [TestMethod]
    public void InitializeParams_ShouldSerializeCorrectly()
    {
        // Arrange
        var param = new InitializeParams
        {
            ProcessId = 1234,
            RootUri = "file:///c:/test/project",
            Capabilities = new ClientCapabilities
            {
                TextDocument = new TextDocumentClientCapabilities
                {
                    Synchronization = new SynchronizationCapabilities
                    {
                        DidOpen = true,
                        DidChange = true
                    }
                }
            },
            WorkspaceFolders =
            [
                new WorkspaceFolder { Uri = "file:///c:/test/project", Name = "project" }
            ]
        };

        // Act
        var json = JsonSerializer.Serialize(param);
        var deserialized = JsonSerializer.Deserialize<InitializeParams>(json);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.ProcessId.Should().Be(1234);
        deserialized.RootUri.Should().Be("file:///c:/test/project");
        deserialized.WorkspaceFolders.Should().HaveCount(1);
    }

    [TestMethod]
    public void SymbolKind_ShouldHaveCorrectValues()
    {
        // Assert - verify enum values match LSP specification
        ((int)SymbolKind.File).Should().Be(1);
        ((int)SymbolKind.Class).Should().Be(5);
        ((int)SymbolKind.Method).Should().Be(6);
        ((int)SymbolKind.Property).Should().Be(7);
        ((int)SymbolKind.Field).Should().Be(8);
        ((int)SymbolKind.Constructor).Should().Be(9);
        ((int)SymbolKind.Interface).Should().Be(11);
        ((int)SymbolKind.Function).Should().Be(12);
    }

    [TestMethod]
    public void DiagnosticSeverity_ShouldHaveCorrectValues()
    {
        // Assert - verify enum values match LSP specification
        ((int)DiagnosticSeverity.Error).Should().Be(1);
        ((int)DiagnosticSeverity.Warning).Should().Be(2);
        ((int)DiagnosticSeverity.Information).Should().Be(3);
        ((int)DiagnosticSeverity.Hint).Should().Be(4);
    }
}