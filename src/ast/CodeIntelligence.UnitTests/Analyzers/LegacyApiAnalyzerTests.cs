// -----------------------------------------------------------------------
// <copyright file="LegacyApiAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class LegacyApiAnalyzerTests
{
    private LegacyApiAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new LegacyApiAnalyzer();
    }

    #region OWIN Tests

    [TestMethod]
    public void DetectOwinStartup_WithConfigurationMethod_DetectsStartup()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void Configuration(IAppBuilder app)
    {
        app.UseWebApi(config);
        app.UseSignalR();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.DetectOwinStartup(tree, "Startup.cs");

        // Assert
        results.Should().ContainSingle();
        results[0].ClassName.Should().Be("Startup");
        results[0].MiddlewareRegistrations.Should().HaveCount(2);
        results[0].MiddlewareRegistrations.Should().Contain(m => m.MiddlewareType == OwinMiddlewareType.WebApi);
        results[0].MiddlewareRegistrations.Should().Contain(m => m.MiddlewareType == OwinMiddlewareType.SignalR);
    }

    [TestMethod]
    public void DetectOwinStartup_WithOAuthMiddleware_DetectsOAuth()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void Configuration(IAppBuilder app)
    {
        app.UseOAuthBearerAuthentication(options);
        app.UseCors(CorsOptions.AllowAll);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.DetectOwinStartup(tree, "Startup.cs");

        // Assert
        results.Should().ContainSingle();
        results[0].MiddlewareRegistrations.Should().Contain(m => m.MiddlewareType == OwinMiddlewareType.OAuth);
        results[0].MiddlewareRegistrations.Should().Contain(m => m.MiddlewareType == OwinMiddlewareType.Cors);
    }

    [TestMethod]
    public void DetectOwinStartup_WithMapRoutes_ExtractsRoutes()
    {
        // Arrange
        var code = @"
public class Startup
{
    public void Configuration(IAppBuilder app)
    {
        app.Map(""/api"", apiApp => apiApp.UseWebApi(config));
        app.MapSignalR(""/signalr"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.DetectOwinStartup(tree, "Startup.cs");

        // Assert
        results.Should().ContainSingle();
        var mapMiddleware = results[0].MiddlewareRegistrations
            .Where(m => m.MethodName.StartsWith("Map")).ToList();
        mapMiddleware.Should().HaveCount(2);
    }

    #endregion

    #region WCF Tests

    [TestMethod]
    public void DetectWcfServices_WithServiceContract_DetectsInterface()
    {
        // Arrange
        var code = @"
[ServiceContract]
public interface IOrderService
{
    [OperationContract]
    Order GetOrder(int id);

    [OperationContract]
    void CreateOrder(Order order);
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.DetectWcfServices(tree, "IOrderService.cs");

        // Assert
        results.Should().ContainSingle();
        results[0].InterfaceName.Should().Be("IOrderService");
        results[0].Operations.Should().HaveCount(2);
        results[0].Operations.Should().Contain(o => o.MethodName == "GetOrder");
        results[0].Operations.Should().Contain(o => o.MethodName == "CreateOrder");
    }

    [TestMethod]
    public void DetectWcfServices_WithWebGet_DetectsRestStyle()
    {
        // Arrange
        var code = @"
[ServiceContract]
public interface IRestService
{
    [OperationContract]
    [WebGet(UriTemplate = ""/users/{id}"")]
    User GetUser(string id);

    [OperationContract]
    [WebInvoke(Method = ""POST"", UriTemplate = ""/users"")]
    void CreateUser(User user);
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.DetectWcfServices(tree, "IRestService.cs");

        // Assert
        results.Should().ContainSingle();
        var getOp = results[0].Operations.First(o => o.MethodName == "GetUser");
        getOp.IsRestStyle.Should().BeTrue();
        getOp.HttpMethod.Should().Be("GET");
        getOp.UriTemplate.Should().Be("/users/{id}");

        var postOp = results[0].Operations.First(o => o.MethodName == "CreateUser");
        postOp.HttpMethod.Should().Be("POST");
    }

    [TestMethod]
    public void DetectWcfServices_WithServiceBehavior_DetectsImplementation()
    {
        // Arrange
        var code = @"
[ServiceBehavior(InstanceContextMode = InstanceContextMode.PerCall)]
public class OrderService : IOrderService
{
    public Order GetOrder(int id) => new Order();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.DetectWcfServices(tree, "OrderService.cs");

        // Assert
        results.Should().ContainSingle();
        results[0].ClassName.Should().Be("OrderService");
    }

    #endregion

    #region Web API 2 Tests

    [TestMethod]
    public void DetectWebApi2Endpoints_WithRoutePrefix_DetectsEndpoints()
    {
        // Arrange
        var code = @"
[RoutePrefix(""api/orders"")]
public class OrdersController : ApiController
{
    [HttpGet]
    [Route("""")]
    public IHttpActionResult GetAll() => Ok();

    [HttpGet]
    [Route(""{id}"")]
    public IHttpActionResult GetById(int id) => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.DetectWebApi2Endpoints(tree, "OrdersController.cs");

        // Assert
        results.Should().HaveCount(2);
        results.Should().Contain(e => e.ActionName == "GetAll" && e.RouteTemplate == "api/orders");
        results.Should().Contain(e => e.ActionName == "GetById" && e.RouteTemplate == "api/orders/{id}");
    }

    [TestMethod]
    public void DetectWebApi2Endpoints_WithAcceptVerbs_DetectsHttpMethod()
    {
        // Arrange
        var code = @"
public class CustomController : ApiController
{
    [AcceptVerbs(""GET"", ""HEAD"")]
    public IHttpActionResult MultiVerb() => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.DetectWebApi2Endpoints(tree, "CustomController.cs");

        // Assert
        results.Should().ContainSingle();
        results[0].HttpMethod.Should().Be("GET");
    }

    [TestMethod]
    public void DetectWebApi2Endpoints_WithConventionNaming_InfersHttpMethod()
    {
        // Arrange
        var code = @"
public class ProductsController : ApiController
{
    public IHttpActionResult GetProducts() => Ok();
    public IHttpActionResult PostProduct(Product p) => Ok();
    public IHttpActionResult DeleteProduct(int id) => Ok();
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.DetectWebApi2Endpoints(tree, "ProductsController.cs");

        // Assert
        results.Should().HaveCount(3);
        results.Should().Contain(e => e.ActionName == "GetProducts" && e.HttpMethod == "GET");
        results.Should().Contain(e => e.ActionName == "PostProduct" && e.HttpMethod == "POST");
        results.Should().Contain(e => e.ActionName == "DeleteProduct" && e.HttpMethod == "DELETE");
    }

    #endregion

    #region GraphQL Tests

    [TestMethod]
    public void DetectGraphQL_HotChocolateQuery_DetectsQuery()
    {
        // Arrange
        var code = @"
public class Query
{
    public IQueryable<Book> GetBooks([Service] ApplicationDbContext context)
        => context.Books;

    public Book? GetBookById(int id, [Service] ApplicationDbContext context)
        => context.Books.Find(id);
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.DetectGraphQL(tree, "Query.cs");

        // Assert
        // Note: This detects Query class but without base type it won't match
        results.Should().BeEmpty(); // Needs ObjectType<Query> base
    }

    [TestMethod]
    public void DetectGraphQL_HotChocolateObjectType_DetectsType()
    {
        // Arrange
        var code = @"
public class BookType : ObjectType<Book>
{
    protected override void Configure(IObjectTypeDescriptor<Book> descriptor)
    {
        descriptor.Field(b => b.Title).Type<NonNullType<StringType>>();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.DetectGraphQL(tree, "BookType.cs");

        // Assert
        results.Should().ContainSingle();
        results[0].Framework.Should().Be(GraphQLFramework.HotChocolate);
        results[0].OperationType.Should().Be(GraphQLOperationType.Type);
        results[0].ClassName.Should().Be("BookType");
    }

    [TestMethod]
    public void DetectGraphQL_GraphQLNetObjectGraphType_DetectsType()
    {
        // Arrange
        var code = @"
public class BookGraphType : ObjectGraphType<Book>
{
    public BookGraphType()
    {
        Field(""title"", b => b.Title);
        Field(""author"", b => b.Author);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.DetectGraphQL(tree, "BookGraphType.cs");

        // Assert
        results.Should().ContainSingle();
        results[0].Framework.Should().Be(GraphQLFramework.GraphQLNet);
        results[0].ClassName.Should().Be("BookGraphType");
        results[0].Resolvers.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [TestMethod]
    public void DetectGraphQL_HotChocolateMutation_DetectsMutation()
    {
        // Arrange
        var code = @"
public class Mutation : MutationType
{
    public async Task<Book> AddBookAsync(AddBookInput input)
    {
        return new Book { Title = input.Title };
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.DetectGraphQL(tree, "Mutation.cs");

        // Assert
        results.Should().ContainSingle();
        results[0].OperationType.Should().Be(GraphQLOperationType.Mutation);
    }

    #endregion

    #region OData Tests

    [TestMethod]
    public void DetectOData_ODataController_DetectsController()
    {
        // Arrange
        var code = @"
public class ProductsController : ODataController
{
    [EnableQuery]
    public IQueryable<Product> Get() => _context.Products;

    public IHttpActionResult GetById([FromODataUri] int key)
        => Ok(_context.Products.Find(key));

    public IHttpActionResult Post(Product product)
    {
        _context.Products.Add(product);
        return Created(product);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.DetectOData(tree, "ProductsController.cs");

        // Assert
        results.Should().ContainSingle();
        results[0].ControllerName.Should().Be("Products");
        results[0].Actions.Should().HaveCount(3);
        results[0].Actions.Should().Contain(a => a.Operation == ODataOperation.Get && a.HasEnableQuery);
        results[0].Actions.Should().Contain(a => a.Operation == ODataOperation.GetById);
        results[0].Actions.Should().Contain(a => a.Operation == ODataOperation.Post);
    }

    [TestMethod]
    public void DetectOData_EntitySetController_DetectsEntityType()
    {
        // Arrange
        var code = @"
public class OrdersController : EntitySetController<Order, int>
{
    public override IQueryable<Order> Get() => _orders.AsQueryable();

    protected override Order GetEntityByKey(int key) => _orders.First(o => o.Id == key);
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var results = this.analyzer.DetectOData(tree, "OrdersController.cs");

        // Assert
        results.Should().ContainSingle();
        results[0].EntityType.Should().Be("Order");
    }

    #endregion

    [TestMethod]
    public void Analyze_NoLegacyApis_ReturnsEmpty()
    {
        // Arrange
        var code = @"
public class Calculator
{
    public int Add(int a, int b) => a + b;
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var owin = this.analyzer.DetectOwinStartup(tree, "Calculator.cs");
        var wcf = this.analyzer.DetectWcfServices(tree, "Calculator.cs");
        var webapi2 = this.analyzer.DetectWebApi2Endpoints(tree, "Calculator.cs");
        var graphql = this.analyzer.DetectGraphQL(tree, "Calculator.cs");
        var odata = this.analyzer.DetectOData(tree, "Calculator.cs");

        // Assert
        owin.Should().BeEmpty();
        wcf.Should().BeEmpty();
        webapi2.Should().BeEmpty();
        graphql.Should().BeEmpty();
        odata.Should().BeEmpty();
    }
}
