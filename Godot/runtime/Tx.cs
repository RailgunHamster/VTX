#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace VTX;

/// <summary>
/// VTX 单线程内存事务运行时（Savepoint 嵌套栈 + 零闭包对象池 Undo Log）。
/// </summary>
public static class Tx
{
    private sealed class TxFailSignal : Exception
    {
        public static readonly TxFailSignal Instance = new();
        private TxFailSignal() : base("VTX transaction failed.") { }
    }

    internal interface IUndoStep
    {
        void UndoAndRecycle();
        void RecycleWithoutUndo();
    }

    private sealed class UndoStep<TOwner, TValue> : IUndoStep
    {
        [ThreadStatic]
        private static Stack<UndoStep<TOwner, TValue>>? _pool;

        private TOwner _owner = default!;
        private TValue _oldValue = default!;
        private Action<TOwner, TValue> _restore = null!;

        public static UndoStep<TOwner, TValue> Rent(TOwner owner, TValue oldValue, Action<TOwner, TValue> restore)
        {
            var pool = _pool ??= new Stack<UndoStep<TOwner, TValue>>(32);
            var step = pool.Count > 0 ? pool.Pop() : new UndoStep<TOwner, TValue>();
            step._owner = owner;
            step._oldValue = oldValue;
            step._restore = restore;
            return step;
        }

        public void UndoAndRecycle()
        {
            _restore(_owner, _oldValue);
            RecycleWithoutUndo();
        }

        public void RecycleWithoutUndo()
        {
            _owner = default!;
            _oldValue = default!;
            _restore = null!;
            (_pool ??= new Stack<UndoStep<TOwner, TValue>>(32)).Push(this);
        }
    }

    private sealed class UndoStep<TOwner, TArg1, TArg2> : IUndoStep
    {
        [ThreadStatic]
        private static Stack<UndoStep<TOwner, TArg1, TArg2>>? _pool;

        private TOwner _owner = default!;
        private TArg1 _arg1 = default!;
        private TArg2 _arg2 = default!;
        private Action<TOwner, TArg1, TArg2> _restore = null!;

        public static UndoStep<TOwner, TArg1, TArg2> Rent(
            TOwner owner,
            TArg1 arg1,
            TArg2 arg2,
            Action<TOwner, TArg1, TArg2> restore)
        {
            var pool = _pool ??= new Stack<UndoStep<TOwner, TArg1, TArg2>>(32);
            var step = pool.Count > 0 ? pool.Pop() : new UndoStep<TOwner, TArg1, TArg2>();
            step._owner = owner;
            step._arg1 = arg1;
            step._arg2 = arg2;
            step._restore = restore;
            return step;
        }

        public void UndoAndRecycle()
        {
            _restore(_owner, _arg1, _arg2);
            RecycleWithoutUndo();
        }

        public void RecycleWithoutUndo()
        {
            _owner = default!;
            _arg1 = default!;
            _arg2 = default!;
            _restore = null!;
            (_pool ??= new Stack<UndoStep<TOwner, TArg1, TArg2>>(32)).Push(this);
        }
    }

    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static List<IUndoStep>? _undoLog;
    [ThreadStatic] private static List<Action>? _deferredCommits;

    /// <summary>当前是否处于事务上下文中。</summary>
    public static bool IsActive
    {
        [Pure] get => _depth > 0;
    }

    /// <summary>当前事务嵌套深度。</summary>
    public static int Depth
    {
        [Pure] get => _depth;
    }

    /// <summary>当前已记录的 Undo 步数（用于调试或压测统计）。</summary>
    public static int UndoLogCount
    {
        [Pure] get => _undoLog?.Count ?? 0;
    }

    /// <summary>
    /// 执行可回滚事务块。若内部调用 Tx.Check(false) 或 Tx.Fail()，所有已发生的修改按逆序自动回滚并返回 false。
    /// 支持任意层级嵌套。
    /// </summary>
    [Transacts]
    public static bool Try(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var undoLog = _undoLog ??= new List<IUndoStep>(64);
        var deferred = _deferredCommits ??= new List<Action>(16);

        int undoSavepoint = undoLog.Count;
        int deferSavepoint = deferred.Count;
        _depth++;

        try
        {
            body();
            _depth--;
            if (_depth == 0)
            {
                CommitTopLevel(undoLog, deferred);
            }
            return true;
        }
        catch (TxFailSignal)
        {
            _depth--;
            RollbackToSavepoint(undoLog, deferred, undoSavepoint, deferSavepoint);
            return false;
        }
        catch
        {
            _depth--;
            RollbackToSavepoint(undoLog, deferred, undoSavepoint, deferSavepoint);
            throw;
        }
    }

    /// <summary>
    /// 执行返回布尔值的可回滚事务块（无异常快速路径：返回 false 或调用 Tx.Fail() 均会自动回滚）。
    /// </summary>
    [Transacts]
    public static bool Try(Func<bool> body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var undoLog = _undoLog ??= new List<IUndoStep>(64);
        var deferred = _deferredCommits ??= new List<Action>(16);

        int undoSavepoint = undoLog.Count;
        int deferSavepoint = deferred.Count;
        _depth++;

        try
        {
            bool ok = body();
            _depth--;
            if (ok)
            {
                if (_depth == 0)
                {
                    CommitTopLevel(undoLog, deferred);
                }
                return true;
            }
            else
            {
                RollbackToSavepoint(undoLog, deferred, undoSavepoint, deferSavepoint);
                return false;
            }
        }
        catch (TxFailSignal)
        {
            _depth--;
            RollbackToSavepoint(undoLog, deferred, undoSavepoint, deferSavepoint);
            return false;
        }
        catch
        {
            _depth--;
            RollbackToSavepoint(undoLog, deferred, undoSavepoint, deferSavepoint);
            throw;
        }
    }

    /// <summary>
    /// Dry-Run 沙盒推演：在真实状态上试跑 body（测试是否能成功走完），然后强制 100% 回滚所有状态与延迟副作用。
    /// 适用于 UI CanExecute 判定、AI 出牌/走位前向推演、以及 aidbg 在线无损探测。
    /// </summary>
    [Pure]
    public static bool Simulate(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var undoLog = _undoLog ??= new List<IUndoStep>(64);
        var deferred = _deferredCommits ??= new List<Action>(16);

        int undoSavepoint = undoLog.Count;
        int deferSavepoint = deferred.Count;
        _depth++;

        try
        {
            body();
            return true;
        }
        catch (TxFailSignal)
        {
            return false;
        }
        finally
        {
            _depth--;
            RollbackToSavepoint(undoLog, deferred, undoSavepoint, deferSavepoint);
        }
    }

    /// <summary>
    /// 带返回值的 Dry-Run 沙盒推演：在真实状态上试跑计算并提取返回值，随后强制 100% 回滚所有状态变化。
    /// </summary>
    [Pure]
    public static bool Simulate<TResult>(Func<TResult> body, [MaybeNullWhen(false)] out TResult result)
    {
        ArgumentNullException.ThrowIfNull(body);

        var undoLog = _undoLog ??= new List<IUndoStep>(64);
        var deferred = _deferredCommits ??= new List<Action>(16);

        int undoSavepoint = undoLog.Count;
        int deferSavepoint = deferred.Count;
        _depth++;

        try
        {
            result = body();
            return true;
        }
        catch (TxFailSignal)
        {
            result = default;
            return false;
        }
        finally
        {
            _depth--;
            RollbackToSavepoint(undoLog, deferred, undoSavepoint, deferSavepoint);
        }
    }

    /// <summary>
    /// 条件断言：若 condition 为 false，立即中断当前事务并回滚所有已发生修改。
    /// </summary>
    [Decides]
    public static void Check([DoesNotReturnIf(false)] bool condition)
    {
        if (!condition)
            throw TxFailSignal.Instance;
    }

    /// <summary>
    /// 立即触发当前事务失败并回滚。
    /// </summary>
    [Decides, DoesNotReturn]
    public static void Fail() => throw TxFailSignal.Instance;

    /// <summary>
    /// 记录单参数零闭包恢复动作（由 [TxField] 生成器、事务容器或自定义数据结构内部调用）。
    /// </summary>
    [Transacts]
    public static void Record<TOwner, TValue>(TOwner owner, TValue oldValue, Action<TOwner, TValue> restore)
    {
        ArgumentNullException.ThrowIfNull(restore);
        if (_depth > 0)
        {
            (_undoLog ??= new List<IUndoStep>(64)).Add(UndoStep<TOwner, TValue>.Rent(owner, oldValue, restore));
        }
    }

    /// <summary>
    /// 记录双参数零闭包恢复动作（由列表索引、字典键值或自定义网格结构恢复调用）。
    /// </summary>
    [Transacts]
    public static void Record<TOwner, TArg1, TArg2>(
        TOwner owner,
        TArg1 arg1,
        TArg2 arg2,
        Action<TOwner, TArg1, TArg2> restore)
    {
        ArgumentNullException.ThrowIfNull(restore);
        if (_depth > 0)
        {
            (_undoLog ??= new List<IUndoStep>(64)).Add(UndoStep<TOwner, TArg1, TArg2>.Rent(owner, arg1, arg2, restore));
        }
    }

    /// <summary>
    /// 将不可逆副作用（如信号发送、节点释放、日志打印）推迟到最外层事务成功 Commit 时执行；
    /// 若事务回滚或处于 Simulate 沙盒中，该操作将被自动丢弃。
    /// </summary>
    [Transacts]
    public static void Defer(Action commitAction)
    {
        ArgumentNullException.ThrowIfNull(commitAction);
        if (_depth > 0)
        {
            (_deferredCommits ??= new List<Action>(16)).Add(commitAction);
        }
        else
        {
            commitAction();
        }
    }

    private static void RollbackToSavepoint(
        List<IUndoStep> undoLog,
        List<Action> deferred,
        int undoSavepoint,
        int deferSavepoint)
    {
        for (int i = undoLog.Count - 1; i >= undoSavepoint; i--)
        {
            undoLog[i].UndoAndRecycle();
        }
        int removeCount = undoLog.Count - undoSavepoint;
        if (removeCount > 0)
        {
            undoLog.RemoveRange(undoSavepoint, removeCount);
        }

        int deferRemoveCount = deferred.Count - deferSavepoint;
        if (deferRemoveCount > 0)
        {
            deferred.RemoveRange(deferSavepoint, deferRemoveCount);
        }
    }

    private static void CommitTopLevel(List<IUndoStep> undoLog, List<Action> deferred)
    {
        for (int i = 0; i < undoLog.Count; i++)
        {
            undoLog[i].RecycleWithoutUndo();
        }
        undoLog.Clear();

        if (deferred.Count > 0)
        {
            var toRun = deferred.ToArray();
            deferred.Clear();
            for (int i = 0; i < toRun.Length; i++)
            {
                toRun[i]();
            }
        }
    }
}
