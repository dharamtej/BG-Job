using System.Text.Json;
using CareerPanda.Framework.Configuration;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CareerPanda.Framework.Cache;

public class RedisCacheService : ICacheService
{
    private readonly string _instanceName;
    private readonly bool _enabled;
    private readonly ILogger<RedisCacheService> _logger;
    private readonly Lazy<IConnectionMultiplexer?> _redis;

    public RedisCacheService(Config config, ILogger<RedisCacheService> logger)
    {
        _instanceName = config.RedisConfig.InstanceName;
        _enabled = config.RedisConfig.Enabled;
        _logger = logger;
        _redis = new Lazy<IConnectionMultiplexer?>(() => TryConnect(config.RedisConfig.Connection));
    }

    private IConnectionMultiplexer? TryConnect(string connection)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(connection))
            return null;

        try
        {
            return ConnectionMultiplexer.Connect(connection);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redis unavailable; cache operations will no-op.");
            return null;
        }
    }

    private IDatabase? Database => _redis.Value?.GetDatabase();

    private string PrefixKey(string key) => $"{_instanceName}{key}";

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (Database == null)
            return default;

        var value = await Database.StringGetAsync(PrefixKey(key));
        if (value.IsNullOrEmpty)
            return default;

        return JsonSerializer.Deserialize<T>(value!);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken cancellationToken = default)
    {
        if (Database == null)
            return;

        var json = JsonSerializer.Serialize(value);
        await Database.StringSetAsync(PrefixKey(key), json, expiry);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (Database == null)
            return;

        await Database.KeyDeleteAsync(PrefixKey(key));
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        if (Database == null)
            return false;

        return await Database.KeyExistsAsync(PrefixKey(key));
    }
}
