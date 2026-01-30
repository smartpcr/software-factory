// -----------------------------------------------------------------------
// <copyright file="ErrorHandlingAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
/// Unit tests for ErrorHandlingAnalyzer.
/// </summary>
[TestClass]
public class ErrorHandlingAnalyzerTests
{
    private ErrorHandlingAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new ErrorHandlingAnalyzer();
    }

    #region Exception Throw Tests

    [TestMethod]
    public void AnalyzeExceptions_ThrowNewException_DetectsThrow()
    {
        // Arrange
        var code = @"
public class OrderService
{
    public void ProcessOrder(int orderId)
    {
        if (orderId <= 0)
            throw new ArgumentException(""Invalid order ID"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var exceptions = this.analyzer.AnalyzeExceptions(tree, "OrderService.cs");

        // Assert
        exceptions.Should().ContainSingle();
        exceptions[0].ExceptionType.Should().Be("ArgumentException");
        exceptions[0].Operation.Should().Be(ExceptionOperation.Throw);
        exceptions[0].Message.Should().Be("Invalid order ID");
    }

    [TestMethod]
    public void AnalyzeExceptions_ThrowCustomException_DetectsType()
    {
        // Arrange
        var code = @"
public class OrderService
{
    public void ProcessOrder(int orderId)
    {
        throw new OrderNotFoundException(""Order not found"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var exceptions = this.analyzer.AnalyzeExceptions(tree, "OrderService.cs");

        // Assert
        exceptions.Should().ContainSingle();
        exceptions[0].ExceptionType.Should().Be("OrderNotFoundException");
    }

    [TestMethod]
    public void AnalyzeExceptions_ThrowWithInterpolatedString_DetectsMessage()
    {
        // Arrange
        var code = @"
public class OrderService
{
    public void ProcessOrder(int orderId)
    {
        throw new InvalidOperationException($""Order {orderId} cannot be processed"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var exceptions = this.analyzer.AnalyzeExceptions(tree, "OrderService.cs");

        // Assert
        exceptions.Should().ContainSingle();
        exceptions[0].ExceptionType.Should().Be("InvalidOperationException");
        exceptions[0].Message.Should().Contain("orderId");
    }

    #endregion

    #region Exception Catch Tests

    [TestMethod]
    public void AnalyzeExceptions_CatchBlock_DetectsCatch()
    {
        // Arrange
        var code = @"
public class OrderService
{
    public void ProcessOrder()
    {
        try
        {
            DoWork();
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var exceptions = this.analyzer.AnalyzeExceptions(tree, "OrderService.cs");

        // Assert
        exceptions.Should().ContainSingle();
        exceptions[0].ExceptionType.Should().Be("Exception");
        exceptions[0].Operation.Should().Be(ExceptionOperation.Catch);
        exceptions[0].VariableName.Should().Be("ex");
    }

    [TestMethod]
    public void AnalyzeExceptions_CatchWithSpecificType_DetectsType()
    {
        // Arrange
        var code = @"
public class OrderService
{
    public void ProcessOrder()
    {
        try
        {
            DoWork();
        }
        catch (InvalidOperationException ex)
        {
            HandleError(ex);
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var exceptions = this.analyzer.AnalyzeExceptions(tree, "OrderService.cs");

        // Assert
        exceptions.Should().ContainSingle();
        exceptions[0].ExceptionType.Should().Be("InvalidOperationException");
    }

    [TestMethod]
    public void AnalyzeExceptions_CatchWithWhenFilter_DetectsFilter()
    {
        // Arrange
        var code = @"
public class OrderService
{
    public void ProcessOrder()
    {
        try
        {
            DoWork();
        }
        catch (Exception ex) when (ex.Message.Contains(""timeout""))
        {
            HandleTimeout(ex);
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var exceptions = this.analyzer.AnalyzeExceptions(tree, "OrderService.cs");

        // Assert
        exceptions.Should().ContainSingle();
        exceptions[0].WhenFilter.Should().Contain("timeout");
    }

    #endregion

    #region Rethrow Tests

    [TestMethod]
    public void AnalyzeExceptions_BareRethrow_DetectsRethrow()
    {
        // Arrange
        var code = @"
public class OrderService
{
    public void ProcessOrder()
    {
        try
        {
            DoWork();
        }
        catch (Exception)
        {
            LogError();
            throw;
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var exceptions = this.analyzer.AnalyzeExceptions(tree, "OrderService.cs");

        // Assert
        exceptions.Should().Contain(e => e.Operation == ExceptionOperation.Rethrow);
    }

    [TestMethod]
    public void AnalyzeExceptions_RethrowVariable_DetectsRethrow()
    {
        // Arrange
        var code = @"
public class OrderService
{
    public void ProcessOrder()
    {
        try
        {
            DoWork();
        }
        catch (Exception ex)
        {
            LogError(ex);
            throw ex;
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var exceptions = this.analyzer.AnalyzeExceptions(tree, "OrderService.cs");

        // Assert
        exceptions.Should().Contain(e => e.Operation == ExceptionOperation.Rethrow);
    }

    #endregion

    #region Try-Catch Block Tests

    [TestMethod]
    public void AnalyzeTryCatchBlocks_SimpleTryCatch_DetectsBlock()
    {
        // Arrange
        var code = @"
public class OrderService
{
    public void ProcessOrder()
    {
        try
        {
            DoWork();
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var blocks = this.analyzer.AnalyzeTryCatchBlocks(tree, "OrderService.cs");

        // Assert
        blocks.Should().ContainSingle();
        blocks[0].CatchCount.Should().Be(1);
        blocks[0].HasFinally.Should().BeFalse();
    }

    [TestMethod]
    public void AnalyzeTryCatchBlocks_TryCatchFinally_DetectsFinally()
    {
        // Arrange
        var code = @"
public class OrderService
{
    public void ProcessOrder()
    {
        try
        {
            DoWork();
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
        finally
        {
            Cleanup();
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var blocks = this.analyzer.AnalyzeTryCatchBlocks(tree, "OrderService.cs");

        // Assert
        blocks.Should().ContainSingle();
        blocks[0].HasFinally.Should().BeTrue();
    }

    [TestMethod]
    public void AnalyzeTryCatchBlocks_MultipleCatches_DetectsAll()
    {
        // Arrange
        var code = @"
public class OrderService
{
    public void ProcessOrder()
    {
        try
        {
            DoWork();
        }
        catch (ArgumentException ex)
        {
            HandleArgumentError(ex);
        }
        catch (InvalidOperationException ex)
        {
            HandleInvalidOperation(ex);
        }
        catch (Exception ex)
        {
            HandleGenericError(ex);
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var blocks = this.analyzer.AnalyzeTryCatchBlocks(tree, "OrderService.cs");

        // Assert
        blocks.Should().ContainSingle();
        blocks[0].CatchCount.Should().Be(3);
        blocks[0].Catches.Should().Contain(c => c.ExceptionType == "ArgumentException");
        blocks[0].Catches.Should().Contain(c => c.ExceptionType == "InvalidOperationException");
    }

    [TestMethod]
    public void AnalyzeTryCatchBlocks_NestedTryCatch_DetectsNested()
    {
        // Arrange
        var code = @"
public class OrderService
{
    public void ProcessOrder()
    {
        try
        {
            try
            {
                InnerWork();
            }
            catch (ArgumentException)
            {
            }
        }
        catch (Exception)
        {
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var blocks = this.analyzer.AnalyzeTryCatchBlocks(tree, "OrderService.cs");

        // Assert
        blocks.Should().HaveCount(2);
        blocks.Should().Contain(b => b.IsNested);
    }

    #endregion

    #region Polly Resilience Tests

    [TestMethod]
    public void AnalyzeResiliencePolicies_PollyRetry_DetectsPolicy()
    {
        // Arrange
        var code = @"
public class HttpService
{
    private readonly IAsyncPolicy _retryPolicy;

    public HttpService()
    {
        _retryPolicy = Policy
            .Handle<HttpRequestException>()
            .RetryAsync(3);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var policies = this.analyzer.AnalyzeResiliencePolicies(tree, "HttpService.cs");

        // Assert
        policies.Should().ContainSingle();
        policies[0].PolicyType.Should().Be(ResiliencePolicyType.Retry);
        policies[0].IsAsync.Should().BeTrue();
        policies[0].RetryCount.Should().Be(3);
    }

    [TestMethod]
    public void AnalyzeResiliencePolicies_PollyWaitAndRetry_DetectsPolicy()
    {
        // Arrange
        var code = @"
public class HttpService
{
    private readonly IAsyncPolicy _retryPolicy;

    public HttpService()
    {
        _retryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var policies = this.analyzer.AnalyzeResiliencePolicies(tree, "HttpService.cs");

        // Assert
        policies.Should().ContainSingle();
        policies[0].PolicyType.Should().Be(ResiliencePolicyType.Retry);
        policies[0].MethodName.Should().Be("WaitAndRetryAsync");
    }

    [TestMethod]
    public void AnalyzeResiliencePolicies_PollyCircuitBreaker_DetectsPolicy()
    {
        // Arrange
        var code = @"
public class HttpService
{
    private readonly IAsyncPolicy _circuitBreaker;

    public HttpService()
    {
        _circuitBreaker = Policy
            .Handle<HttpRequestException>()
            .CircuitBreakerAsync(5, TimeSpan.FromMinutes(1));
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var policies = this.analyzer.AnalyzeResiliencePolicies(tree, "HttpService.cs");

        // Assert
        policies.Should().ContainSingle();
        policies[0].PolicyType.Should().Be(ResiliencePolicyType.CircuitBreaker);
    }

    [TestMethod]
    public void AnalyzeResiliencePolicies_PollyFallback_DetectsPolicy()
    {
        // Arrange
        var code = @"
public class HttpService
{
    private readonly IAsyncPolicy<string> _fallbackPolicy;

    public HttpService()
    {
        _fallbackPolicy = Policy<string>
            .Handle<HttpRequestException>()
            .FallbackAsync(""default"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var policies = this.analyzer.AnalyzeResiliencePolicies(tree, "HttpService.cs");

        // Assert
        policies.Should().ContainSingle();
        policies[0].PolicyType.Should().Be(ResiliencePolicyType.Fallback);
    }

    [TestMethod]
    public void AnalyzeResiliencePolicies_PollyTimeout_DetectsPolicy()
    {
        // Arrange
        var code = @"
public class HttpService
{
    private readonly IAsyncPolicy _timeoutPolicy;

    public HttpService()
    {
        _timeoutPolicy = Policy.TimeoutAsync(30);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var policies = this.analyzer.AnalyzeResiliencePolicies(tree, "HttpService.cs");

        // Assert
        policies.Should().ContainSingle();
        policies[0].PolicyType.Should().Be(ResiliencePolicyType.Timeout);
    }

    [TestMethod]
    public void AnalyzeResiliencePolicies_PollyBulkhead_DetectsPolicy()
    {
        // Arrange
        var code = @"
public class HttpService
{
    private readonly IAsyncPolicy _bulkheadPolicy;

    public HttpService()
    {
        _bulkheadPolicy = Policy.BulkheadAsync(10, 20);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var policies = this.analyzer.AnalyzeResiliencePolicies(tree, "HttpService.cs");

        // Assert
        policies.Should().ContainSingle();
        policies[0].PolicyType.Should().Be(ResiliencePolicyType.Bulkhead);
    }

    [TestMethod]
    public void AnalyzeResiliencePolicies_PolicyWrap_DetectsPolicy()
    {
        // Arrange
        var code = @"
public class HttpService
{
    private readonly IAsyncPolicy _wrappedPolicy;

    public HttpService()
    {
        _wrappedPolicy = Policy.WrapAsync(retryPolicy, circuitBreakerPolicy);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var policies = this.analyzer.AnalyzeResiliencePolicies(tree, "HttpService.cs");

        // Assert
        policies.Should().ContainSingle();
        policies[0].PolicyType.Should().Be(ResiliencePolicyType.PolicyWrap);
    }

    #endregion
}
