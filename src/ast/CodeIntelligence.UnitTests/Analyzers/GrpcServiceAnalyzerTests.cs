// -----------------------------------------------------------------------
// <copyright file="GrpcServiceAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class GrpcServiceAnalyzerTests
{
    private GrpcServiceAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new GrpcServiceAnalyzer();
    }

    #region gRPC Service Detection

    [TestMethod]
    public void Analyze_GrpcServiceImplementation_DetectsService()
    {
        // Arrange
        var code = @"
public class GreeterService : Greeter.GreeterBase
{
    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
    {
        return Task.FromResult(new HelloReply { Message = ""Hello "" + request.Name });
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var operations = this.analyzer.Analyze(tree, "GreeterService.cs");

        // Assert
        operations.Should().HaveCountGreaterThanOrEqualTo(1);
        var serviceOp = operations.FirstOrDefault(o => o.OperationType == GrpcOperationType.ServiceDefinition);
        serviceOp.Should().NotBeNull();
        serviceOp!.ServiceName.Should().Be("Greeter");
        serviceOp.ClassName.Should().Be("GreeterService");
    }

    [TestMethod]
    public void Analyze_GrpcServiceMethod_DetectsMethod()
    {
        // Arrange
        var code = @"
public class OrderService : Orders.OrdersBase
{
    public override Task<OrderResponse> GetOrder(GetOrderRequest request, ServerCallContext context)
    {
        return Task.FromResult(new OrderResponse());
    }

    public override Task<OrderResponse> CreateOrder(CreateOrderRequest request, ServerCallContext context)
    {
        return Task.FromResult(new OrderResponse());
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var operations = this.analyzer.Analyze(tree, "OrderService.cs");

        // Assert
        var methods = operations.Where(o => o.OperationType == GrpcOperationType.ServiceMethod).ToList();
        methods.Should().HaveCount(2);
        methods.Should().Contain(m => m.MethodName == "GetOrder");
        methods.Should().Contain(m => m.MethodName == "CreateOrder");
    }

    [TestMethod]
    public void Analyze_UnaryMethod_DetectsUnaryType()
    {
        // Arrange
        var code = @"
public class MyService : Service.ServiceBase
{
    public override Task<Response> UnaryCall(Request request, ServerCallContext context)
    {
        return Task.FromResult(new Response());
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var operations = this.analyzer.Analyze(tree, "MyService.cs");

        // Assert
        var method = operations.First(o => o.OperationType == GrpcOperationType.ServiceMethod);
        method.GrpcMethodType.Should().Be(GrpcMethodType.Unary);
    }

    [TestMethod]
    public void Analyze_ServerStreamingMethod_DetectsStreamingType()
    {
        // Arrange
        var code = @"
public class StreamService : Stream.StreamBase
{
    public override async Task ListItems(
        ListRequest request,
        IServerStreamWriter<Item> responseStream,
        ServerCallContext context)
    {
        await responseStream.WriteAsync(new Item());
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var operations = this.analyzer.Analyze(tree, "StreamService.cs");

        // Assert
        var method = operations.First(o => o.OperationType == GrpcOperationType.ServiceMethod);
        method.GrpcMethodType.Should().Be(GrpcMethodType.ServerStreaming);
    }

    [TestMethod]
    public void Analyze_ClientStreamingMethod_DetectsStreamingType()
    {
        // Arrange
        var code = @"
public class UploadService : Upload.UploadBase
{
    public override async Task<UploadResponse> UploadData(
        IAsyncStreamReader<DataChunk> requestStream,
        ServerCallContext context)
    {
        while (await requestStream.MoveNext())
        {
            var chunk = requestStream.Current;
        }
        return new UploadResponse();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var operations = this.analyzer.Analyze(tree, "UploadService.cs");

        // Assert
        var method = operations.First(o => o.OperationType == GrpcOperationType.ServiceMethod);
        method.GrpcMethodType.Should().Be(GrpcMethodType.ClientStreaming);
    }

    [TestMethod]
    public void Analyze_BidirectionalStreamingMethod_DetectsStreamingType()
    {
        // Arrange
        var code = @"
public class ChatService : Chat.ChatBase
{
    public override async Task Chat(
        IAsyncStreamReader<ChatMessage> requestStream,
        IServerStreamWriter<ChatMessage> responseStream,
        ServerCallContext context)
    {
        while (await requestStream.MoveNext())
        {
            await responseStream.WriteAsync(requestStream.Current);
        }
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var operations = this.analyzer.Analyze(tree, "ChatService.cs");

        // Assert
        var method = operations.First(o => o.OperationType == GrpcOperationType.ServiceMethod);
        method.GrpcMethodType.Should().Be(GrpcMethodType.BidirectionalStreaming);
    }

    #endregion

    #region gRPC Client Detection

    [TestMethod]
    public void Analyze_GrpcClientCall_DetectsClientCall()
    {
        // Arrange
        var code = @"
public class OrderClient
{
    private readonly Orders.OrdersClient _client;

    public async Task<Order> GetOrderAsync(int id)
    {
        return await _client.GetOrderAsync(new GetOrderRequest { Id = id });
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var operations = this.analyzer.Analyze(tree, "OrderClient.cs");

        // Assert
        operations.Should().ContainSingle();
        var call = operations[0];
        call.OperationType.Should().Be(GrpcOperationType.ClientCall);
        call.MethodName.Should().Be("GetOrder");
        // The client expression is "_client"
        call.ClientExpression.Should().Be("_client");
    }

    [TestMethod]
    public void Analyze_GrpcClientWithUnderscorePrefix_DetectsCall()
    {
        // Arrange
        var code = @"
public class UserClient
{
    private readonly UserService.UserServiceClient _userServiceClient;

    public async Task<User> GetUserAsync(string id)
    {
        return await _userServiceClient.GetUserAsync(new GetUserRequest { Id = id });
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var operations = this.analyzer.Analyze(tree, "UserClient.cs");

        // Assert
        operations.Should().ContainSingle();
        operations[0].ServiceName.Should().Be("userService");
    }

    [TestMethod]
    public void Analyze_GrpcClientWithFullyQualifiedName_ExtractsService()
    {
        // Arrange
        var code = @"
public class GreeterClient
{
    private readonly Greeter.GreeterClient _greeterClient;

    public async Task<HelloReply> SayHelloAsync(string name)
    {
        return await _greeterClient.SayHelloAsync(new HelloRequest { Name = name });
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var operations = this.analyzer.Analyze(tree, "GreeterClient.cs");

        // Assert
        operations.Should().ContainSingle();
        var call = operations[0];
        call.OperationType.Should().Be(GrpcOperationType.ClientCall);
        call.MethodName.Should().Be("SayHello");
        call.ServiceName.Should().Be("greeter");
    }

    #endregion

    [TestMethod]
    public void Analyze_NoGrpc_ReturnsEmpty()
    {
        // Arrange
        var code = @"
public class Calculator
{
    public int Add(int a, int b) => a + b;
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var operations = this.analyzer.Analyze(tree, "Calculator.cs");

        // Assert
        operations.Should().BeEmpty();
    }
}
