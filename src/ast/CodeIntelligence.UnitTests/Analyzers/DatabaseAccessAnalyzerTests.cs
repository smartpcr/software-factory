// -----------------------------------------------------------------------
// <copyright file="DatabaseAccessAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class DatabaseAccessAnalyzerTests
{
    private DatabaseAccessAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new DatabaseAccessAnalyzer();
    }

    #region Entity Detection Tests

    [TestMethod]
    public void AnalyzeEntities_TableAttribute_DetectsEntity()
    {
        // Arrange
        var code = @"
[Table(""Users"")]
public class User
{
    public int Id { get; set; }
    public string Name { get; set; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entities = this.analyzer.AnalyzeEntities(tree, "User.cs");

        // Assert
        entities.Should().ContainSingle();
        entities[0].ClassName.Should().Be("User");
        entities[0].TableName.Should().Be("Users");
        entities[0].EntityType.Should().Be(DatabaseEntityType.Table);
    }

    [TestMethod]
    public void AnalyzeEntities_DbSetProperty_DetectsEntity()
    {
        // Arrange
        var code = @"
public class ApplicationDbContext : DbContext
{
    public DbSet<User> Users { get; set; }
    public DbSet<Order> Orders { get; set; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entities = this.analyzer.AnalyzeEntities(tree, "AppDbContext.cs");

        // Assert
        entities.Should().HaveCount(2);
        entities.Should().Contain(e => e.ClassName == "User" && e.DbSetPropertyName == "Users");
        entities.Should().Contain(e => e.ClassName == "Order" && e.DbSetPropertyName == "Orders");
    }

    [TestMethod]
    public void AnalyzeEntities_KeylessAttribute_DetectsKeylessEntity()
    {
        // Arrange
        var code = @"
[Keyless]
public class UserSummary
{
    public string UserName { get; set; }
    public int OrderCount { get; set; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entities = this.analyzer.AnalyzeEntities(tree, "UserSummary.cs");

        // Assert
        entities.Should().ContainSingle();
        entities[0].EntityType.Should().Be(DatabaseEntityType.Keyless);
    }

    [TestMethod]
    public void AnalyzeEntities_OwnedAttribute_DetectsOwnedEntity()
    {
        // Arrange
        var code = @"
[Owned]
public class Address
{
    public string Street { get; set; }
    public string City { get; set; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entities = this.analyzer.AnalyzeEntities(tree, "Address.cs");

        // Assert
        entities.Should().ContainSingle();
        entities[0].EntityType.Should().Be(DatabaseEntityType.Owned);
    }

    [TestMethod]
    public void AnalyzeEntities_NavigationProperty_DetectsNavigation()
    {
        // Arrange
        var code = @"
[Table(""Orders"")]
public class Order
{
    public int Id { get; set; }
    public virtual ICollection<OrderItem> Items { get; set; }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var entities = this.analyzer.AnalyzeEntities(tree, "Order.cs");

        // Assert
        entities.Should().ContainSingle();
        entities[0].Properties.Should().Contain(p => p.Name == "Items" && p.IsNavigation);
    }

    #endregion

    #region Query Detection Tests

    [TestMethod]
    public void AnalyzeQueries_WhereClause_DetectsLinqQuery()
    {
        // Arrange
        var code = @"
public class UserRepository
{
    private readonly DbContext _context;

    public IEnumerable<User> GetActiveUsers()
    {
        return _context.Users.Where(u => u.IsActive);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var queries = this.analyzer.AnalyzeQueries(tree, "UserRepository.cs");

        // Assert
        queries.Should().ContainSingle();
        queries[0].MethodName.Should().Be("Where");
        queries[0].QueryType.Should().Be(DatabaseQueryType.Linq);
    }

    [TestMethod]
    public void AnalyzeQueries_FirstOrDefaultAsync_DetectsSingleResult()
    {
        // Arrange
        var code = @"
public class UserService
{
    private readonly AppDbContext _dbContext;

    public async Task<User?> GetUserByIdAsync(int id)
    {
        return await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == id);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var queries = this.analyzer.AnalyzeQueries(tree, "UserService.cs");

        // Assert
        queries.Should().ContainSingle();
        queries[0].MethodName.Should().Be("FirstOrDefaultAsync");
        queries[0].QueryType.Should().Be(DatabaseQueryType.SingleResult);
        queries[0].IsAsync.Should().BeTrue();
    }

    [TestMethod]
    public void AnalyzeQueries_ToListAsync_DetectsMultipleResults()
    {
        // Arrange
        var code = @"
public class OrderRepository
{
    private readonly DbContext _context;

    public async Task<List<Order>> GetAllOrdersAsync()
    {
        return await _context.Orders.ToListAsync();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var queries = this.analyzer.AnalyzeQueries(tree, "OrderRepository.cs");

        // Assert
        queries.Should().ContainSingle();
        queries[0].QueryType.Should().Be(DatabaseQueryType.MultipleResults);
    }

    [TestMethod]
    public void AnalyzeQueries_Include_DetectsEagerLoading()
    {
        // Arrange
        var code = @"
public class OrderService
{
    private readonly DbContext _context;

    public Order GetOrderWithItems(int id)
    {
        return _context.Orders
            .Include(o => o.Items)
            .FirstOrDefault(o => o.Id == id);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var queries = this.analyzer.AnalyzeQueries(tree, "OrderService.cs");

        // Assert
        queries.Should().Contain(q => q.MethodName == "Include" && q.QueryType == DatabaseQueryType.EagerLoad);
    }

    [TestMethod]
    public void AnalyzeQueries_FromSqlRaw_DetectsRawSql()
    {
        // Arrange
        var code = @"
public class ReportService
{
    private readonly DbContext _context;

    public List<SalesReport> GetSalesReport()
    {
        return _context.Set<SalesReport>()
            .FromSqlRaw(""SELECT * FROM SalesReport"")
            .ToList();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var queries = this.analyzer.AnalyzeQueries(tree, "ReportService.cs");

        // Assert
        queries.Should().Contain(q => q.MethodName == "FromSqlRaw" && q.QueryType == DatabaseQueryType.RawSql);
    }

    [TestMethod]
    public void AnalyzeQueries_DapperQuery_DetectsDapperQuery()
    {
        // Arrange
        var code = @"
public class UserRepository
{
    private readonly IDbConnection _connection;

    public IEnumerable<User> GetAllUsers()
    {
        return _connection.Query<User>(""SELECT * FROM Users"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var queries = this.analyzer.AnalyzeQueries(tree, "UserRepository.cs");

        // Assert
        queries.Should().ContainSingle();
        queries[0].MethodName.Should().Be("Query");
        queries[0].QueryType.Should().Be(DatabaseQueryType.Dapper);
    }

    [TestMethod]
    public void AnalyzeQueries_CountAsync_DetectsAggregateQuery()
    {
        // Arrange
        var code = @"
public class StatsService
{
    private readonly DbContext _context;

    public async Task<int> GetUserCountAsync()
    {
        return await _context.Users.CountAsync();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var queries = this.analyzer.AnalyzeQueries(tree, "StatsService.cs");

        // Assert
        queries.Should().ContainSingle();
        queries[0].QueryType.Should().Be(DatabaseQueryType.Aggregate);
    }

    [TestMethod]
    public void AnalyzeQueries_AnyAsync_DetectsExistenceQuery()
    {
        // Arrange
        var code = @"
public class ValidationService
{
    private readonly DbContext _context;

    public async Task<bool> UserExistsAsync(string email)
    {
        return await _context.Users.AnyAsync(u => u.Email == email);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var queries = this.analyzer.AnalyzeQueries(tree, "ValidationService.cs");

        // Assert
        queries.Should().ContainSingle();
        queries[0].QueryType.Should().Be(DatabaseQueryType.Existence);
    }

    [TestMethod]
    public void AnalyzeQueries_AsNoTracking_DetectsQuery()
    {
        // Arrange
        var code = @"
public class ReadOnlyRepository
{
    private readonly DbContext _context;

    public List<Product> GetProducts()
    {
        return _context.Products.AsNoTracking().ToList();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var queries = this.analyzer.AnalyzeQueries(tree, "ReadOnlyRepository.cs");

        // Assert
        queries.Should().Contain(q => q.MethodName == "AsNoTracking");
    }

    #endregion

    #region Mutation Detection Tests

    [TestMethod]
    public void AnalyzeMutations_Add_DetectsInsert()
    {
        // Arrange
        var code = @"
public class UserRepository
{
    private readonly DbContext _context;

    public void CreateUser(User user)
    {
        _context.Users.Add(user);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var mutations = this.analyzer.AnalyzeMutations(tree, "UserRepository.cs");

        // Assert
        mutations.Should().ContainSingle();
        mutations[0].MethodName.Should().Be("Add");
        mutations[0].MutationType.Should().Be(DatabaseMutationType.Insert);
    }

    [TestMethod]
    public void AnalyzeMutations_AddAsync_DetectsAsyncInsert()
    {
        // Arrange
        var code = @"
public class ProductService
{
    private readonly DbContext _context;

    public async Task CreateProductAsync(Product product)
    {
        await _context.Products.AddAsync(product);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var mutations = this.analyzer.AnalyzeMutations(tree, "ProductService.cs");

        // Assert
        mutations.Should().ContainSingle();
        mutations[0].IsAsync.Should().BeTrue();
        mutations[0].MutationType.Should().Be(DatabaseMutationType.Insert);
    }

    [TestMethod]
    public void AnalyzeMutations_Update_DetectsUpdate()
    {
        // Arrange
        var code = @"
public class OrderRepository
{
    private readonly DbContext _context;

    public void UpdateOrder(Order order)
    {
        _context.Orders.Update(order);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var mutations = this.analyzer.AnalyzeMutations(tree, "OrderRepository.cs");

        // Assert
        mutations.Should().ContainSingle();
        mutations[0].MutationType.Should().Be(DatabaseMutationType.Update);
    }

    [TestMethod]
    public void AnalyzeMutations_Remove_DetectsDelete()
    {
        // Arrange
        var code = @"
public class UserRepository
{
    private readonly DbContext _context;

    public void DeleteUser(User user)
    {
        _context.Users.Remove(user);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var mutations = this.analyzer.AnalyzeMutations(tree, "UserRepository.cs");

        // Assert
        mutations.Should().ContainSingle();
        mutations[0].MutationType.Should().Be(DatabaseMutationType.Delete);
    }

    [TestMethod]
    public void AnalyzeMutations_SaveChangesAsync_DetectsSaveChanges()
    {
        // Arrange
        var code = @"
public class UnitOfWork
{
    private readonly DbContext _context;

    public async Task CommitAsync()
    {
        await _context.SaveChangesAsync();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var mutations = this.analyzer.AnalyzeMutations(tree, "UnitOfWork.cs");

        // Assert
        mutations.Should().ContainSingle();
        mutations[0].MutationType.Should().Be(DatabaseMutationType.SaveChanges);
        mutations[0].IsAsync.Should().BeTrue();
    }

    [TestMethod]
    public void AnalyzeMutations_ExecuteUpdateAsync_DetectsBulkUpdate()
    {
        // Arrange
        var code = @"
public class BulkOperations
{
    private readonly DbContext _context;

    public async Task DeactivateOldUsersAsync()
    {
        await _context.Users
            .Where(u => u.LastLogin < DateTime.Now.AddYears(-1))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.IsActive, false));
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var mutations = this.analyzer.AnalyzeMutations(tree, "BulkOperations.cs");

        // Assert
        mutations.Should().ContainSingle();
        mutations[0].MutationType.Should().Be(DatabaseMutationType.Update);
        mutations[0].IsBulkOperation.Should().BeTrue();
    }

    [TestMethod]
    public void AnalyzeMutations_ExecuteDeleteAsync_DetectsBulkDelete()
    {
        // Arrange
        var code = @"
public class CleanupService
{
    private readonly DbContext _context;

    public async Task DeleteOldLogsAsync()
    {
        await _context.Logs
            .Where(l => l.Timestamp < DateTime.Now.AddMonths(-6))
            .ExecuteDeleteAsync();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var mutations = this.analyzer.AnalyzeMutations(tree, "CleanupService.cs");

        // Assert
        mutations.Should().ContainSingle();
        mutations[0].MutationType.Should().Be(DatabaseMutationType.Delete);
        mutations[0].IsBulkOperation.Should().BeTrue();
    }

    [TestMethod]
    public void AnalyzeMutations_DapperExecute_DetectsExecute()
    {
        // Arrange
        var code = @"
public class UserRepository
{
    private readonly IDbConnection _connection;

    public int UpdateUserStatus(int userId, bool isActive)
    {
        return _connection.Execute(""UPDATE Users SET IsActive = @IsActive WHERE Id = @Id"", 
            new { Id = userId, IsActive = isActive });
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var mutations = this.analyzer.AnalyzeMutations(tree, "UserRepository.cs");

        // Assert
        mutations.Should().ContainSingle();
        mutations[0].MutationType.Should().Be(DatabaseMutationType.Execute);
    }

    #endregion
}
