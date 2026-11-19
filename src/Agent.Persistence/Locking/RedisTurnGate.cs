using System.Collections.Concurrent;
using System.Security.Cryptography;
using Agent.Abstractions.Config;
using Agent.Abstractions.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Agent.Persistence.Locking;

/// <summary>
/// Cross-process turn lock. The lease is a Redis key set with <c>SET key token NX PX lease</c>; the random token proves
/// ownership. A live turn renews the lease every third of its lifetime with a compare-and-pexpire script, and release
/// is a compare-and-delete script, so a holder whose lease expired can neither extend nor delete the next holder's
/// lease. A crashed holder blocks its session for at most <see cref="TurnOptions.LeaseSeconds"/>.
/// If Redis cannot be reached the call degrades to a process-local lock and logs a warning: chat stays available, but
/// exclusivity then only holds within one process until Redis is back.
/// </summary>
internal sealed class RedisTurnGate : ITurnGate
{
    private const string ReleaseScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    private const string RenewScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('pexpire', KEYS[1], ARGV[2]) else return 0 end";

    private readonly ConcurrentDictionary<string, byte> _local = new(StringComparer.Ordinal);
    private readonly IOptions<TurnOptions> _options;

    public RedisTurnGate(IConnectionMultiplexer redis, IOptions<TurnOptions> options, TimeProvider clock, ILogger<RedisTurnGate> logger)
    {
        Redis = redis;
        _options = options;
        Clock = clock;
        Logger = logger;
    }

    private IConnectionMultiplexer Redis { get; }

    private TimeProvider Clock { get; }

    private ILogger<RedisTurnGate> Logger { get; }

    public async ValueTask<ITurnLease?> TryEnterAsync(string sessionId, CancellationToken cancellationToken)
    {
        var settings = _options.Value;
        RedisKey key = $"turn:session:{sessionId}";
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        try
        {
            var acquired = await Redis.GetDatabase()
                .StringSetAsync(key, token, settings.Lease, When.NotExists)
                .WaitAsync(cancellationToken);
            return acquired ? new RedisLease(this, key, token, settings) : null;
        }
        catch (RedisException ex)
        {
            Logger.LogWarning(ex, "Redis turn lock unavailable; using the process-local lock for this request");
            return _local.TryAdd(sessionId, 0) ? new LocalLease(_local, sessionId) : null;
        }
    }

    private sealed class RedisLease : ITurnLease
    {
        private readonly RedisTurnGate _gate;
        private readonly RedisKey _key;
        private readonly string _token;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _renewal;
        private int _disposed;

        public RedisLease(RedisTurnGate gate, RedisKey key, string token, TurnOptions settings)
        {
            _gate = gate;
            _key = key;
            _token = token;
            _renewal = RenewAsync(settings, _stop.Token);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            await _stop.CancelAsync();
            await _renewal;
            _stop.Dispose();

            try
            {
                await _gate.Redis.GetDatabase().ScriptEvaluateAsync(ReleaseScript, [_key], [_token]);
            }
            catch (RedisException ex)
            {
                _gate.Logger.LogWarning(ex, "Could not release the turn lock; it will expire on its own");
            }
        }

        private async Task RenewAsync(TurnOptions settings, CancellationToken stopToken)
        {
            using var timer = new PeriodicTimer(settings.RenewInterval, _gate.Clock);
            try
            {
                while (await timer.WaitForNextTickAsync(stopToken))
                {
                    try
                    {
                        var renewed = (long)await _gate.Redis.GetDatabase()
                            .ScriptEvaluateAsync(RenewScript, [_key], [_token, (long)settings.Lease.TotalMilliseconds]);
                        if (renewed == 0)
                        {
                            _gate.Logger.LogWarning("The turn lock lease was lost (it expired and may be held by another request)");
                            return;
                        }
                    }
                    catch (RedisException ex)
                    {
                        _gate.Logger.LogWarning(ex, "Renewing the turn lock failed; retrying on the next tick");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Disposal stops the renewal loop; that is the normal way for it to end.
            }
        }
    }

    private sealed class LocalLease(ConcurrentDictionary<string, byte> active, string sessionId) : ITurnLease
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                active.TryRemove(sessionId, out _);
            }

            return ValueTask.CompletedTask;
        }
    }
}

internal static class TurnGateRegistration
{
    /// <summary>Selects the Redis turn gate and registers the options the gate and the API contention policy use.</summary>
    public static IServiceCollection AddRedisTurnGate(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TurnOptions>()
            .Bind(configuration.GetSection(TurnOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.Replace(ServiceDescriptor.Singleton<ITurnGate, RedisTurnGate>());
        return services;
    }
}
