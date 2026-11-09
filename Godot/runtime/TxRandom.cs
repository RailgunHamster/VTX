#nullable enable
using System;

namespace VTX;

/// <summary>
/// 确定性且支持事务自动回滚的伪随机数生成器（xorshift64*）。
/// 1. 在 Tx.Simulate（沙盒推演）中使用时，推演结束后随机种子会自动回滚到推演前，
///    保证紧接着执行的 Tx.Try 掷出 100% 完全一致的随机结果（消除“预览成功、真跑失败”的分裂）；
/// 2. 在 Tx.Try 中若后续步骤失败回滚，随机种子同样自动回退，防止玩家通过故意触发失败来“垫刀/刷随机数”。
/// </summary>
public sealed class TxRandom
{
    private ulong _state;

    public TxRandom(ulong seed = 0x853C49E6748FEA9BUL)
    {
        _state = seed == 0 ? 0x853C49E6748FEA9BUL : seed;
    }

    /// <summary>当前内部 64 位随机状态种子（支持事务回滚）。</summary>
    public ulong State
    {
        [Pure] get => _state;
        [Transacts]
        set
        {
            ulong normalized = value == 0 ? 0x853C49E6748FEA9BUL : value;
            if (_state != normalized)
            {
                if (Tx.IsActive)
                {
                    Tx.Record(this, _state, static (self, oldState) => self._state = oldState);
                }
                _state = normalized;
            }
        }
    }

    /// <summary>生成下一个确定性 32 位无符号整数，并在事务中自动记录旧种子。</summary>
    [Transacts]
    public uint NextUInt()
    {
        ulong prev = _state;
        if (Tx.IsActive)
        {
            Tx.Record(this, prev, static (self, oldState) => self._state = oldState);
        }

        ulong x = prev;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        _state = x;
        return (uint)((x * 0x2545F4914F6CDD1DUL) >> 32);
    }

    /// <summary>生成 [0, maxExclusive) 范围内的确定性随机整数。</summary>
    [Transacts]
    public int NextInt(int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);
        return (int)(NextUInt() % (uint)maxExclusive);
    }

    /// <summary>生成 [minInclusive, maxExclusive) 范围内的确定性随机整数。</summary>
    [Transacts]
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (minInclusive >= maxExclusive)
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "maxExclusive must be greater than minInclusive.");

        uint range = (uint)(maxExclusive - minInclusive);
        return minInclusive + (int)(NextUInt() % range);
    }

    /// <summary>生成 [0.0f, 1.0f) 范围内的确定性随机浮点数。</summary>
    [Transacts]
    public float NextFloat()
    {
        return (NextUInt() >> 8) * (1.0f / 16777216.0f);
    }

    /// <summary>以 probability (0.0 ~ 1.0) 的概率返回 true。</summary>
    [Transacts]
    public bool Chance(float probability)
    {
        if (probability <= 0f) return false;
        if (probability >= 1f) return true;
        return NextFloat() < probability;
    }

    /// <summary>
    /// 事务内概率检定（Decides）：若未命中概率，直接中断并回滚当前事务。
    /// </summary>
    [Decides]
    public void CheckChance(float probability, string? reason = null)
    {
        bool hit = Chance(probability);
        Tx.Check(hit, reason ?? $"概率检定未通过 (p={probability:P0})");
    }
}
