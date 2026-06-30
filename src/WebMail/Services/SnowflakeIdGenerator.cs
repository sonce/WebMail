namespace WebMail.Services;

/// <summary>
/// Twitter 风格雪花ID：[时间戳(42bit) | workerId(10bit) | sequence(12bit)]。
/// 时间有序、可解出毫秒时间戳。线程安全。
/// </summary>
public sealed class SnowflakeIdGenerator
{
    private const int WorkerBits = 10;
    private const int SequenceBits = 12;
    private const long MaxSequence = (1L << SequenceBits) - 1;

    private readonly long _epochMs;
    private readonly long _workerId;
    private readonly object _lock = new();
    private long _lastMs = -1L;
    private long _sequence;

    public SnowflakeIdGenerator(DateTimeOffset? epoch = null, int workerId = 1)
    {
        var e = epoch ?? new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _epochMs = e.ToUnixTimeMilliseconds();
        if (workerId < 0 || workerId >= (1 << WorkerBits))
            throw new ArgumentOutOfRangeException(nameof(workerId));
        _workerId = workerId;
    }

    public long NextId()
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now < _lastMs) now = _lastMs; // 时钟回拨：钳到上次，避免倒退

            if (now == _lastMs)
            {
                _sequence = (_sequence + 1) & MaxSequence;
                if (_sequence == 0) // 当前毫秒序列用尽，自旋到下一毫秒
                {
                    do { now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
                    while (now <= _lastMs);
                }
            }
            else
            {
                _sequence = 0;
            }

            _lastMs = now;
            return ((now - _epochMs) << (WorkerBits + SequenceBits))
                 | (_workerId << SequenceBits)
                 | _sequence;
        }
    }
}
