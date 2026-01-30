using System;
using System.Linq;
using CodeIntelligence.Analyzers;
using Microsoft.CodeAnalysis.CSharp;

var analyzer = new LayerInteractionAnalyzer();

var code = @"
namespace MyApp.Infrastructure;
public class OrderRepositoryEntity : Entity
{
}";
var tree = CSharpSyntaxTree.ParseText(code);
var results = analyzer.AnalyzeInteractions(tree, "OrderRepositoryEntity.cs");

Console.WriteLine($"Total interactions: {results.Count}");
foreach (var r in results)
{
    Console.WriteLine($"  Source: {r.SourceType} ({r.SourceLayer}) -> Target: {r.TargetType} ({r.TargetLayer}) [{r.InteractionType}]");
}
