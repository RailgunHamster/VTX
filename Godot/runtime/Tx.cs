#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace VTX;

/// <summary>
/// VTX 单线程内存事务运行时：
/// 1. Savepoint 嵌套栈 + 零闭包对象池 Undo Log；
/// 2. [CallerArgumentExpression] 自动失败原因捕获（LastFailReason）；
/// 3. FirstOf / WhereCan 多候选回溯搜索；
/// 4. [TxInvariant] 提交前类不变量契约自动校验。
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
    [ThreadStatic] private static List<ITxObject>? _touchedObjects;
    [ThreadStatic] private static string? _lastFailReason;

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
    /// 最近一次事务失败的原因描述或触发失败的源码条件表达式（由 Tx.Check / Tx.Fail 自动记录）。
    /// </summary>
    public static string? LastFailReason
    {
        [Pure] get => _lastFailReason;
    }

    /// <summary>
    /// 执行可回滚事务块。若内部调用 Tx.Check(false) 或 Tx.Fail()，或提交前违反 [TxInvariant]，
    /// 所有已发生的修改按逆序自动回滚并返回 false。支持任意层级嵌套。
    /// </summary>
    [Transacts]
    public static bool Try(Action body) => Try(body, out _);

    /// <summary>
    /// 执行可回滚事务块，并在失败时输出触发回滚的原因或条件表达式（failReason）。
    /// </summary>
    [Transacts]
    public static bool Try(Action body, out string? failReason)
    {
        ArgumentNullException.ThrowIfNull(body);

        var undoLog = _undoLog ??= new List<IUndoStep>(64);
        var deferred = _deferredCommits ??= new List<Action>(16);
        var touched = _touchedObjects ??= new List<ITxObject>(16);

        int undoSavepoint = undoLog.Count;
        int deferSavepoint = deferred.Count;
        int touchedSavepoint = touched.Count;
        if (_depth == 0)
        {
            _lastFailReason = null;
        }
        _depth++;

        try
        {
            body();
            ValidateTouchedInvariants(touched, touchedSavepoint);
            _depth--;
            if (_depth == 0)
            {
                CommitTopLevel(undoLog, deferred, touched);
            }
            failReason = null;
            return true;
        }
        catch (TxFailSignal)
        {
            _depth--;
            RollbackToSavepoint(undoLog, deferred, touched, undoSavepoint, deferSavepoint, touchedSavepoint);
            failReason = _lastFailReason;
            return false;
        }
        catch
        {
            _depth--;
            RollbackToSavepoint(undoLog, deferred, touched, undoSavepoint, deferSavepoint, touchedSavepoint);
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
        var touched = _touchedObjects ??= new List<ITxObject>(16);

        int undoSavepoint = undoLog.Count;
        int deferSavepoint = deferred.Count;
        int touchedSavepoint = touched.Count;
        if (_depth == 0)
        {
            _lastFailReason = null;
        }
        _depth++;

        try
        {
            bool ok = body();
            if (ok)
            {
                ValidateTouchedInvariants(touched, touchedSavepoint);
            }
            _depth--;
            if (ok)
            {
                if (_depth == 0)
                {
                    CommitTopLevel(undoLog, deferred, touched);
                }
                return true;
            }
            else
            {
                _lastFailReason ??= "Transaction body returned false";
                RollbackToSavepoint(undoLog, deferred, touched, undoSavepoint, deferSavepoint, touchedSavepoint);
                return false;
            }
        }
        catch (TxFailSignal)
        {
            _depth--;
            RollbackToSavepoint(undoLog, deferred, touched, undoSavepoint, deferSavepoint, touchedSavepoint);
            return false;
        }
        catch
        {
            _depth--;
            RollbackToSavepoint(undoLog, deferred, touched, undoSavepoint, deferSavepoint, touchedSavepoint);
            throw;
        }
    }

    /// <summary>
    /// 带返回值的可回滚事务块：若成功通过所有校验并提交，将返回值赋给 result 并返回 true；
    /// 若中途失败回滚，result 置为 default 并返回 false（避免在事务内直接修改外部局部变量触发 VTX001）。
    /// </summary>
    [Transacts]
    public static bool Try<TResult>(Func<TResult> body, [MaybeNullWhen(false)] out TResult result)
    {
        ArgumentNullException.ThrowIfNull(body);

        var undoLog = _undoLog ??= new List<IUndoStep>(64);
        var deferred = _deferredCommits ??= new List<Action>(16);
        var touched = _touchedObjects ??= new List<ITxObject>(16);

        int undoSavepoint = undoLog.Count;
        int deferSavepoint = deferred.Count;
        int touchedSavepoint = touched.Count;
        if (_depth == 0)
        {
            _lastFailReason = null;
        }
        _depth++;

        try
        {
            TResult temp = body();
            ValidateTouchedInvariants(touched, touchedSavepoint);
            _depth--;
            if (_depth == 0)
            {
                CommitTopLevel(undoLog, deferred, touched);
            }
            result = temp;
            return true;
        }
        catch (TxFailSignal)
        {
            _depth--;
            RollbackToSavepoint(undoLog, deferred, touched, undoSavepoint, deferSavepoint, touchedSavepoint);
            result = default;
            return false;
        }
        catch
        {
            _depth--;
            RollbackToSavepoint(undoLog, deferred, touched, undoSavepoint, deferSavepoint, touchedSavepoint);
            throw;
        }
    }

    /// <summary>
    /// Dry-Run 沙盒推演：在真实状态上试跑 body（含 [TxInvariant] 校验），随后强制 100% 回滚所有状态与延迟副作用。
    /// 适用于 UI CanExecute 判定、AI 出牌/走位前向推演、以及 aidbg 在线无损探测。
    /// </summary>
    [Pure]
    public static bool Simulate(Action body) => Simulate(body, out _);

    /// <summary>
    /// Dry-Run 沙盒推演，并在失败时输出具体的失败原因或条件表达式（failReason），适合 UI 禁用提示与 AI 调试诊断。
    /// </summary>
    [Pure]
    public static bool Simulate(Action body, out string? failReason)
    {
        ArgumentNullException.ThrowIfNull(body);

        var undoLog = _undoLog ??= new List<IUndoStep>(64);
        var deferred = _deferredCommits ??= new List<Action>(16);
        var touched = _touchedObjects ??= new List<ITxObject>(16);

        int undoSavepoint = undoLog.Count;
        int deferSavepoint = deferred.Count;
        int touchedSavepoint = touched.Count;
        if (_depth == 0)
        {
            _lastFailReason = null;
        }
        _depth++;

        try
        {
            body();
            ValidateTouchedInvariants(touched, touchedSavepoint);
            failReason = null;
            return true;
        }
        catch (TxFailSignal)
        {
            failReason = _lastFailReason;
            return false;
        }
        finally
        {
            _depth--;
            RollbackToSavepoint(undoLog, deferred, touched, undoSavepoint, deferSavepoint, touchedSavepoint);
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
        var touched = _touchedObjects ??= new List<ITxObject>(16);

        int undoSavepoint = undoLog.Count;
        int deferSavepoint = deferred.Count;
        int touchedSavepoint = touched.Count;
        if (_depth == 0)
        {
            _lastFailReason = null;
        }
        _depth++;

        try
        {
            TResult temp = body();
            ValidateTouchedInvariants(touched, touchedSavepoint);
            result = temp;
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
            RollbackToSavepoint(undoLog, deferred, touched, undoSavepoint, deferSavepoint, touchedSavepoint);
        }
    }

    /// <summary>
    /// 多候选回溯执行（对应 Verse 的 Choice 选择器）：
    /// 按顺序对 candidates 中的每个候选尝试执行 action(candidate)；
    /// 若某个候选失败则自动回滚其产生的副作用并尝试下一个；
    /// 第一个成功的候选直接提交保留其修改，并通过 chosen 返回该候选。
    /// </summary>
    [Transacts]
    public static bool FirstOf<T>(
        IEnumerable<T> candidates,
        Action<T> action,
        [MaybeNullWhen(false)] out T chosen)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(action);

        foreach (T candidate in candidates)
        {
            T current = candidate;
            if (Try(() => action(current)))
            {
                chosen = current;
                return true;
            }
        }

        chosen = default;
        return false;
    }

    /// <summary>
    /// 多候选无损筛选（基于 Simulate）：
    /// 在真实状态上依次试跑每个 candidate，每次试跑后 100% 回滚状态，返回所有能成功通过事务与不变量校验的合法候选列表。
    /// 非常适合战棋合法落点高亮、麻将可打出牌筛选、卡牌可用目标枚举。
    /// </summary>
    [Pure]
    public static List<T> WhereCan<T>(IEnumerable<T> candidates, Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(action);

        var validList = new List<T>();
        foreach (T candidate in candidates)
        {
            T current = candidate;
            if (Simulate(() => action(current)))
            {
                validList.Add(current);
            }
        }
        return validList;
    }

    /// <summary>
    /// 条件断言：若 condition 为 false，自动记录失败原因（或条件源码表达式）并立即中断回滚当前事务。
    /// </summary>
    [Decides]
    public static void Check(
        [DoesNotReturnIf(false)] bool condition,
        string? reason = null,
        [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (!condition)
        {
            _lastFailReason = string.IsNullOrEmpty(reason) ? expr : $"{reason} ({expr})";
            throw TxFailSignal.Instance;
        }
    }

    /// <summary>
    /// 立即触发当前事务失败并回滚，可附带失败原因说明。
    /// </summary>
    [Decides, DoesNotReturn]
    public static void Fail(string? reason = null)
    {
        _lastFailReason = reason ?? "Tx.Fail()";
        throw TxFailSignal.Instance;
    }

    /// <summary>
    /// 可空引用解包断言（对应 Verse 的 ?Option 解包）：
    /// 若 value 为 null 则记录失败表达式并立即中断回滚当前事务；否则返回非空值。
    /// </summary>
    [Decides]
    [return: NotNull]
    public static T NotNull<T>(
        [NotNull] T? value,
        string? reason = null,
        [CallerArgumentExpression(nameof(value))] string? expr = null)
        where T : class
    {
        if (value is null)
        {
            _lastFailReason = string.IsNullOrEmpty(reason) ? $"{expr} is not null" : $"{reason} ({expr} is not null)";
            throw TxFailSignal.Instance;
        }
        return value;
    }

    /// <summary>
    /// 可空值类型解包断言：若 value.HasValue 为 false 则立即中断回滚当前事务；否则返回其 Value。
    /// </summary>
    [Decides]
    public static T NotNull<T>(
        [NotNull] T? value,
        string? reason = null,
        [CallerArgumentExpression(nameof(value))] string? expr = null)
        where T : struct
    {
        if (!value.HasValue)
        {
            _lastFailReason = string.IsNullOrEmpty(reason) ? $"{expr}.HasValue" : $"{reason} ({expr}.HasValue)";
            throw TxFailSignal.Instance;
        }
        return value.Value;
    }

    /// <summary>
    /// 显式将对象标记为本次事务已修改，确保提交前触发其 [TxInvariant] 校验。
    /// （注：[TxField] 生成的属性 setter 会自动调用，无需手动调用）。
    /// </summary>
    [Transacts]
    public static void Touch(ITxObject obj)
    {
        if (_depth <= 0 || obj is null)
            return;

        var touched = _touchedObjects ??= new List<ITxObject>(16);
        for (int i = touched.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(touched[i], obj))
                return;
        }
        touched.Add(obj);
    }

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
            if (owner is ITxObject txObj)
            {
                Touch(txObj);
            }
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
            if (owner is ITxObject txObj)
            {
                Touch(txObj);
            }
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

    private static void ValidateTouchedInvariants(List<ITxObject> touched, int touchedSavepoint)
    {
        for (int i = touchedSavepoint; i < touched.Count; i++)
        {
            touched[i].ValidateTxInvariants();
        }
    }

    private static void RollbackToSavepoint(
        List<IUndoStep> undoLog,
        List<Action> deferred,
        List<ITxObject> touched,
        int undoSavepoint,
        int deferSavepoint,
        int touchedSavepoint)
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

        int touchedRemoveCount = touched.Count - touchedSavepoint;
        if (touchedRemoveCount > 0)
        {
            touched.RemoveRange(touchedSavepoint, touchedRemoveCount);
        }
    }

    private static void CommitTopLevel(
        List<IUndoStep> undoLog,
        List<Action> deferred,
        List<ITxObject> touched)
    {
        for (int i = 0; i < undoLog.Count; i++)
        {
            undoLog[i].RecycleWithoutUndo();
        }
        undoLog.Clear();
        touched.Clear();

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
