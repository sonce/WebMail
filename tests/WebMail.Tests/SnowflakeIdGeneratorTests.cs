using System;
using System.Collections.Generic;
using System.Linq;
using WebMail.Services;
using Xunit;

namespace WebMail.Tests;

public sealed class SnowflakeIdGeneratorTests
{
    [Fact]
    public void GeneratesUniqueMonotonicIds()
    {
        var gen = new SnowflakeIdGenerator();
        var ids = new List<long>();
        for (var i = 0; i < 5000; i++) ids.Add(gen.NextId());

        Assert.Equal(ids.Count, ids.Distinct().Count());          // 全部唯一
        for (var i = 1; i < ids.Count; i++) Assert.True(ids[i] > ids[i - 1]); // 单调递增
        Assert.All(ids, id => Assert.True(id > 0));
    }

    [Fact]
    public void EmbeddedTimestampIsRecent()
    {
        var epoch = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var gen = new SnowflakeIdGenerator(epoch, workerId: 1);
        var before = DateTimeOffset.UtcNow;

        var id = gen.NextId();

        var ms = (id >> 22) + epoch.ToUnixTimeMilliseconds();
        var decoded = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        Assert.True(decoded >= before.AddSeconds(-2));
        Assert.True(decoded <= DateTimeOffset.UtcNow.AddSeconds(2));
    }
}
