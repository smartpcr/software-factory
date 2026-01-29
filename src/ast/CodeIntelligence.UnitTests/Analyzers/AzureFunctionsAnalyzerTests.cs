// -----------------------------------------------------------------------
// <copyright file="AzureFunctionsAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Tests for <see cref="AzureFunctionsAnalyzer"/>.
/// </summary>
[TestClass]
public class AzureFunctionsAnalyzerTests
{
    private AzureFunctionsAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new AzureFunctionsAnalyzer();
    }

    [TestMethod]
    public void Analyze_HttpTriggerFunction_DetectsFunction()
    {
        // Arrange
        var code = @"
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Test
{
    public class HttpFunctions
    {
        [Function(""GetUsers"")]
        public HttpResponseData GetUsers(
            [HttpTrigger(AuthorizationLevel.Function, ""get"")] HttpRequestData req)
        {
            return req.CreateResponse();
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var functions = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        functions.Should().HaveCount(1);
        var func = functions[0];
        func.FunctionName.Should().Be("GetUsers");
        func.MethodName.Should().Be("GetUsers");
        func.Trigger.Should().NotBeNull();
        func.Trigger!.TriggerType.Should().Be("Http");
    }

    [TestMethod]
    public void Analyze_TimerTriggerFunction_DetectsTrigger()
    {
        // Arrange
        var code = @"
using Microsoft.Azure.Functions.Worker;

namespace Test
{
    public class TimerFunctions
    {
        [Function(""CleanupJob"")]
        public void RunCleanup([TimerTrigger(""0 */5 * * * *"")] TimerInfo timer)
        {
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var functions = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        functions.Should().HaveCount(1);
        var func = functions[0];
        func.Trigger!.TriggerType.Should().Be("Timer");
        func.Trigger.ParameterName.Should().Be("timer");
    }

    [TestMethod]
    public void Analyze_QueueTriggerFunction_DetectsTrigger()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class QueueFunctions
    {
        [FunctionName(""ProcessMessage"")]
        public void ProcessQueue([QueueTrigger(""myqueue"")] string message)
        {
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var functions = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        functions.Should().HaveCount(1);
        functions[0].Trigger!.TriggerType.Should().Be("Queue");
    }

    [TestMethod]
    public void Analyze_BlobTriggerFunction_DetectsTrigger()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class BlobFunctions
    {
        [Function(""ProcessBlob"")]
        public void ProcessBlob([BlobTrigger(""container/{name}"")] string blobContent)
        {
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var functions = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        functions.Should().HaveCount(1);
        functions[0].Trigger!.TriggerType.Should().Be("Blob");
    }

    [TestMethod]
    public void Analyze_ServiceBusTriggerFunction_DetectsTrigger()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class ServiceBusFunctions
    {
        [Function(""ProcessServiceBus"")]
        public void ProcessMessage([ServiceBusTrigger(""mytopic"", ""mysubscription"")] string message)
        {
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var functions = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        functions.Should().HaveCount(1);
        functions[0].Trigger!.TriggerType.Should().Be("ServiceBus");
    }

    [TestMethod]
    public void Analyze_FunctionWithBindings_DetectsBindings()
    {
        // Arrange
        var code = @"
namespace Test
{
    public class BlobFunctions
    {
        [Function(""CopyBlob"")]
        public void CopyBlob(
            [BlobTrigger(""input/{name}"")] string inputBlob,
            [Blob(""output/{name}"")] out string outputBlob)
        {
            outputBlob = inputBlob;
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var functions = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        functions.Should().HaveCount(1);
        var func = functions[0];
        func.Trigger.Should().NotBeNull();
        func.Bindings.Should().HaveCount(1);
        func.Bindings[0].BindingType.Should().Be("Blob");
    }

    [TestMethod]
    public void Analyze_AsyncFunction_DetectsAsync()
    {
        // Arrange
        var code = @"
using System.Threading.Tasks;

namespace Test
{
    public class AsyncFunctions
    {
        [Function(""AsyncFunction"")]
        public async Task<string> RunAsync([HttpTrigger] HttpRequestData req)
        {
            return ""result"";
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var functions = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        functions.Should().HaveCount(1);
        functions[0].IsAsync.Should().BeTrue();
        functions[0].ReturnType.Should().StartWith("Task");
    }

    [TestMethod]
    public void Analyze_IsolatedWorkerModel_DetectsModel()
    {
        // Arrange
        var code = @"
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Test
{
    public class IsolatedFunctions
    {
        [Function(""IsolatedFunc"")]
        public HttpResponseData Run([HttpTrigger] HttpRequestData req)
        {
            return req.CreateResponse();
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var functions = this.analyzer.Analyze(tree, "test.cs");

        // Assert
        functions.Should().HaveCount(1);
        functions[0].IsIsolatedWorker.Should().BeTrue();
    }
}
