﻿using Maomi.MQ.Retry;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Maomi.MQ.RedisRetry
{
    public class RedisRetryPolicyFactory : IPolicyFactory
    {
        private const int MaxLength = 5;

        private readonly ILogger<DefaultPolicyFactory> _logger;
        private readonly IDatabase _redis;
        private readonly MqOptions _mqOptions;

        public RedisRetryPolicyFactory(ILogger<DefaultPolicyFactory> logger, IDatabase redis, MqOptions mqOptions)
        {
            _logger = logger;
            _redis = redis;
            _mqOptions = mqOptions;
        }

        public virtual async Task<AsyncRetryPolicy> CreatePolicy(string queue)
        {
            var queueName = _mqOptions.QueuePrefix + queue;
            var existRetry = await _redis.StringGetAsync(queueName);
            var currentRetryCount = 0;

            if (existRetry.HasValue)
            {
                currentRetryCount = (int)existRetry;
            }

            var retryCount = MaxLength - currentRetryCount;
            if (retryCount < 0)
            {
                retryCount = 0;
            }

            // 创建异步重试策略
            var retryPolicy = Policy
                .Handle<Exception>()
                .WaitAndRetryAsync(
                    retryCount: retryCount,
                    sleepDurationProvider: retryAttempt =>
                    {
                        var attempt = retryAttempt;
                        if (currentRetryCount != 0)
                        {
                            attempt += currentRetryCount;
                        }
                        return TimeSpan.FromSeconds(Math.Pow(2, attempt));
                    },
                    onRetry: async (Exception exception, TimeSpan timeSpan, int retryCount, Context context) =>
                    {
                        _logger.LogDebug("重试");
                        await FaildAsync(queue, exception, timeSpan, retryCount, context);
                    });
            return retryPolicy;
        }

        // 每次失败重试，重新放到 redis
        public virtual async Task FaildAsync(string queue, Exception ex, TimeSpan timeSpan, int retryCount, Context context)
        {
            var queueName = _mqOptions.QueuePrefix + queue;
            long value = await _redis.StringIncrementAsync(queueName, retryCount);
        }
    }
}