﻿using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace OrderLunch.Helper
{
    public class RedisHandler
    {
        RedisConnectorHelper _connectionHelper;
        private readonly string ACCESS_KEY;
        public RedisHandler(IConfiguration _configuration)
        {
            _connectionHelper = new RedisConnectorHelper(_configuration);
        }

        public async Task<string> ReadAccessToken()
        {
            RedisKey key = new(nameof(ACCESS_KEY));
            var cache = _connectionHelper.Connection.GetDatabase();
            return await cache.StringGetAsync(key);
        }

        public async Task<bool> WriteAccessToken(string accessToken, int expiredTime)
        {
            RedisKey key = new(nameof(ACCESS_KEY));
            var cache = _connectionHelper.Connection.GetDatabase();
            var expirationTime = TimeSpan.FromSeconds(expiredTime - 100);
            return await cache.StringSetAsync(key, accessToken, expirationTime);
        }
    }
}
