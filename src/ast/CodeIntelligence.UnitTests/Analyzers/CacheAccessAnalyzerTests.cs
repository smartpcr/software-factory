// -----------------------------------------------------------------------
// <copyright file="CacheAccessAnalyzerTests.cs" company="Microsoft">
// </copyright>
// -----------------------------------------------------------------------

namespace CodeIntelligence.UnitTests.Analyzers;

using CodeIntelligence.Analyzers;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class CacheAccessAnalyzerTests
{
    private CacheAccessAnalyzer analyzer = null!;

    [TestInitialize]
    public void Setup()
    {
        this.analyzer = new CacheAccessAnalyzer();
    }

    #region IMemoryCache Tests

    [TestMethod]
    public void Analyze_MemoryCacheGet_DetectsReadOperation()
    {
        // Arrange
        var code = @"
public class UserService
{
    private readonly IMemoryCache _memoryCache;

    public User? GetUser(string userId)
    {
        return _memoryCache.Get<User>(userId);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "UserService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].CacheType.Should().Be(CacheType.MemoryCache);
        accesses[0].OperationType.Should().Be(CacheOperationType.Read);
        accesses[0].MethodName.Should().Be("Get");
        accesses[0].CacheKey.Should().Be("userId");
    }

    [TestMethod]
    public void Analyze_MemoryCacheTryGetValue_DetectsReadOperation()
    {
        // Arrange
        var code = @"
public class CacheService
{
    private readonly IMemoryCache _memoryCache;

    public bool TryGetFromCache(string key, out object? value)
    {
        return _memoryCache.TryGetValue(key, out value);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "CacheService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].CacheType.Should().Be(CacheType.MemoryCache);
        accesses[0].OperationType.Should().Be(CacheOperationType.Read);
        accesses[0].MethodName.Should().Be("TryGetValue");
    }

    [TestMethod]
    public void Analyze_MemoryCacheSet_DetectsWriteOperation()
    {
        // Arrange
        var code = @"
public class UserService
{
    private readonly IMemoryCache _memoryCache;

    public void CacheUser(string userId, User user)
    {
        _memoryCache.Set(userId, user, TimeSpan.FromMinutes(30));
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "UserService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].CacheType.Should().Be(CacheType.MemoryCache);
        accesses[0].OperationType.Should().Be(CacheOperationType.Write);
        accesses[0].MethodName.Should().Be("Set");
        accesses[0].CacheKey.Should().Be("userId");
    }

    [TestMethod]
    public void Analyze_MemoryCacheGetOrCreateAsync_DetectsReadOperation()
    {
        // Arrange
        var code = @"
public class ProductService
{
    private readonly IMemoryCache _memoryCache;

    public async Task<Product> GetProductAsync(int productId)
    {
        return await _memoryCache.GetOrCreateAsync($""product:{productId}"", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            return await LoadProductFromDatabaseAsync(productId);
        });
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "ProductService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].CacheType.Should().Be(CacheType.MemoryCache);
        accesses[0].OperationType.Should().Be(CacheOperationType.Read);
        accesses[0].MethodName.Should().Be("GetOrCreateAsync");
        accesses[0].IsAsync.Should().BeTrue();
    }

    #endregion

    #region IDistributedCache Tests

    [TestMethod]
    public void Analyze_DistributedCacheGetAsync_DetectsReadOperation()
    {
        // Arrange
        var code = @"
public class SessionService
{
    private readonly IDistributedCache _distributedCache;

    public async Task<byte[]?> GetSessionAsync(string sessionId)
    {
        return await _distributedCache.GetAsync(sessionId);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "SessionService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].CacheType.Should().Be(CacheType.DistributedCache);
        accesses[0].OperationType.Should().Be(CacheOperationType.Read);
        accesses[0].MethodName.Should().Be("GetAsync");
        accesses[0].IsAsync.Should().BeTrue();
    }

    [TestMethod]
    public void Analyze_DistributedCacheSetStringAsync_DetectsWriteOperation()
    {
        // Arrange
        var code = @"
public class TokenService
{
    private readonly IDistributedCache _distributedCache;

    public async Task StoreTokenAsync(string tokenId, string token)
    {
        await _distributedCache.SetStringAsync(tokenId, token);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "TokenService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].CacheType.Should().Be(CacheType.DistributedCache);
        accesses[0].OperationType.Should().Be(CacheOperationType.Write);
        accesses[0].MethodName.Should().Be("SetStringAsync");
    }

    [TestMethod]
    public void Analyze_DistributedCacheRemoveAsync_DetectsDeleteOperation()
    {
        // Arrange
        var code = @"
public class CacheService
{
    private readonly IDistributedCache _distributedCache;

    public async Task InvalidateCacheAsync(string key)
    {
        await _distributedCache.RemoveAsync(key);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "CacheService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].CacheType.Should().Be(CacheType.DistributedCache);
        accesses[0].OperationType.Should().Be(CacheOperationType.Delete);
        accesses[0].MethodName.Should().Be("RemoveAsync");
    }

    #endregion

    #region Redis Tests

    [TestMethod]
    public void Analyze_RedisStringGet_DetectsReadOperation()
    {
        // Arrange
        var code = @"
public class RedisService
{
    private readonly IDatabase _database;

    public string? GetValue(string key)
    {
        return _database.StringGet(key);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "RedisService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].CacheType.Should().Be(CacheType.Redis);
        accesses[0].OperationType.Should().Be(CacheOperationType.Read);
        accesses[0].MethodName.Should().Be("StringGet");
    }

    [TestMethod]
    public void Analyze_RedisStringSetAsync_DetectsWriteOperation()
    {
        // Arrange
        var code = @"
public class RedisService
{
    private readonly IDatabase _redis;

    public async Task SetValueAsync(string key, string value)
    {
        await _redis.StringSetAsync(key, value, TimeSpan.FromMinutes(30));
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "RedisService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].CacheType.Should().Be(CacheType.Redis);
        accesses[0].OperationType.Should().Be(CacheOperationType.Write);
        accesses[0].MethodName.Should().Be("StringSetAsync");
        accesses[0].IsAsync.Should().BeTrue();
    }

    [TestMethod]
    public void Analyze_RedisHashGet_DetectsReadOperation()
    {
        // Arrange
        var code = @"
public class UserCacheService
{
    private readonly IDatabase _db;

    public string? GetUserField(string userId, string field)
    {
        return _db.HashGet($""user:{userId}"", field);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "UserCacheService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].CacheType.Should().Be(CacheType.Redis);
        accesses[0].OperationType.Should().Be(CacheOperationType.Read);
        accesses[0].MethodName.Should().Be("HashGet");
    }

    [TestMethod]
    public void Analyze_RedisKeyDelete_DetectsDeleteOperation()
    {
        // Arrange
        var code = @"
public class CacheManager
{
    private readonly IDatabase _database;

    public bool DeleteKey(string key)
    {
        return _database.KeyDelete(key);
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "CacheManager.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].CacheType.Should().Be(CacheType.Redis);
        accesses[0].OperationType.Should().Be(CacheOperationType.Delete);
        accesses[0].MethodName.Should().Be("KeyDelete");
    }

    [TestMethod]
    public void Analyze_RedisListOperations_DetectsCorrectOperations()
    {
        // Arrange
        var code = @"
public class QueueService
{
    private readonly IDatabase _redis;

    public async Task EnqueueAsync(string queue, string item)
    {
        await _redis.ListRightPushAsync(queue, item);
    }

    public async Task<string[]> GetAllAsync(string queue)
    {
        var items = await _redis.ListRangeAsync(queue);
        return items.Select(x => x.ToString()).ToArray();
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "QueueService.cs");

        // Assert
        accesses.Should().HaveCount(2);
        accesses.Should().Contain(a => a.MethodName == "ListRightPushAsync" && a.OperationType == CacheOperationType.Write);
        accesses.Should().Contain(a => a.MethodName == "ListRangeAsync" && a.OperationType == CacheOperationType.Read);
    }

    #endregion

    #region Wrapper Chain Tests

    [TestMethod]
    public void BuildCacheWrapperChain_DetectsCustomCacheWrapper()
    {
        // Arrange
        var code = @"
public class CustomCacheWrapper
{
    private readonly IMemoryCache _memoryCache;

    public CustomCacheWrapper(IMemoryCache memoryCache)
    {
        _memoryCache = memoryCache;
    }

    public T? Get<T>(string key) => _memoryCache.Get<T>(key);
}

public class UserService
{
    private readonly CustomCacheWrapper _cache;

    public UserService(CustomCacheWrapper cache)
    {
        _cache = cache;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var wrappers = this.analyzer.BuildCacheWrapperChain(new[] { tree });

        // Assert
        wrappers.Should().Contain("IMemoryCache");
        wrappers.Should().Contain("CustomCacheWrapper");
    }

    [TestMethod]
    public void BuildCacheWrapperChain_DetectsInterfaceWrapper()
    {
        // Arrange
        var code = @"
public interface ICacheService
{
    T? Get<T>(string key);
}

public class RedisCacheService : ICacheService
{
    private readonly IDatabase _database;

    public RedisCacheService(IDatabase database)
    {
        _database = database;
    }

    public T? Get<T>(string key)
    {
        var value = _database.StringGet(key);
        return value.HasValue ? JsonSerializer.Deserialize<T>(value!) : default;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var wrappers = this.analyzer.BuildCacheWrapperChain(new[] { tree });

        // Assert
        wrappers.Should().Contain("IDatabase");
        wrappers.Should().Contain("RedisCacheService");
        wrappers.Should().Contain("ICacheService");
    }

    #endregion

    #region Cache Key Extraction Tests

    [TestMethod]
    public void Analyze_LiteralCacheKey_ExtractsKey()
    {
        // Arrange
        var code = @"
public class ConfigService
{
    private readonly IMemoryCache _memoryCache;

    public Config? GetConfig()
    {
        return _memoryCache.Get<Config>(""app:config"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "ConfigService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].CacheKey.Should().Be("app:config");
    }

    [TestMethod]
    public void Analyze_InterpolatedCacheKey_ExtractsPattern()
    {
        // Arrange
        var code = @"
public class UserService
{
    private readonly IDistributedCache _cache;

    public async Task<User?> GetUserAsync(int userId)
    {
        var data = await _cache.GetAsync($""user:{userId}"");
        return data != null ? JsonSerializer.Deserialize<User>(data) : null;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "UserService.cs");

        // Assert
        accesses.Should().ContainSingle();
        accesses[0].CacheKey.Should().Contain("user:");
        accesses[0].CacheKey.Should().Contain("userId");
    }

    #endregion

    #region Multiple Operations Tests

    [TestMethod]
    public void Analyze_MultipleCacheOperations_DetectsAll()
    {
        // Arrange
        var code = @"
public class ProductCacheService
{
    private readonly IMemoryCache _cache;

    public Product? GetProduct(int id)
    {
        if (_cache.TryGetValue($""product:{id}"", out Product? product))
        {
            return product;
        }

        product = LoadFromDatabase(id);
        if (product != null)
        {
            _cache.Set($""product:{id}"", product, TimeSpan.FromMinutes(30));
        }

        return product;
    }
}";
        var tree = CSharpSyntaxTree.ParseText(code);

        // Act
        var accesses = this.analyzer.Analyze(tree, "ProductCacheService.cs");

        // Assert
        accesses.Should().HaveCount(2);
        accesses.Should().Contain(a => a.OperationType == CacheOperationType.Read);
        accesses.Should().Contain(a => a.OperationType == CacheOperationType.Write);
    }

    #endregion
}
