#nullable enable
using System;
using Godot;

namespace VTX;

/// <summary>
/// VTX 的线程规则：事务（Tx）、TxTask 的等待登记表和所有 Godot 对象都只能在主线程上使用。
/// 主线程的判断每个线程只向引擎查询一次，之后是一次线程本地变量读取。
/// </summary>
internal static class VtxThread
{
    // 0 = 还没查过，1 = 主线程，-1 = 其它线程
    [ThreadStatic] private static sbyte _kind;

    public static bool IsMainThread
    {
        get
        {
            sbyte k = _kind;
            if (k == 0)
            {
                k = OS.GetThreadCallerId() == OS.GetMainThreadId() ? (sbyte)1 : (sbyte)-1;
                _kind = k;
            }
            return k > 0;
        }
    }

    public static void EnsureMainThread(string api)
    {
        if (!IsMainThread)
        {
            throw new InvalidOperationException(
                $"VTX: {api} 只能在主线程调用（当前在后台线程）。" +
                "后台线程只做纯数据计算；需要回到主线程时 await 它（Godot 的同步上下文会把续体送回主线程），或使用 CallDeferred。");
        }
    }
}
