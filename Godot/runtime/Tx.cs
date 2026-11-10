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
    [ThreadStatic] private static HashSet<ITxObject>? _touchedSet;
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

    // ---------------------------------------------------------------------
    // 事务核心：所有 Try / Simulate 重载都走同一条路径。
    // 关键不变量：
    // 1. 无论 body、[TxInvariant]、回滚还是提交阶段抛出什么异常，_depth 都恢复为进入时的值；
    // 2. 最外层提交时，Undo Log 先清空，再在事务之外（_depth == 0）执行延迟动作，
    //    延迟动作抛出的异常不会触发回滚，也不会破坏事务状态，而是在全部执行完后统一抛出。
    // ---------------------------------------------------------------------

    private interface IBody
    {
        bool Invoke();
    }

    private struct ActionBody : IBody
    {
        public Action Action;
        public bool Invoke()
        {
            Action();
            return true;
        }
    }

    private struct PredicateBody : IBody
    {
        public Func<bool> Func;
        public bool Invoke() => Func();
    }

    private struct ResultBody<TResult> : IBody
    {
        public Func<TResult> Func;
        public TResult Result;
        public bool Invoke()
        {
            Result = Func();
            return true;
        }
    }

    private static bool Run<TBody>(ref TBody body, bool simulate) where TBody : struct, IBody
    {
        var undoLog = _undoLog ??= new List<IUndoStep>(64);
        var deferred = _deferredCommits ??= new List<Action>(16);
        var touched = _touchedObjects ??= new List<ITxObject>(16);

        int entryDepth = _depth;
        if (entryDepth < 0)
        {
            // 防御：理论上不会出现，出现说明有人通过反射篡改了状态。
            entryDepth = 0;
        }

        int undoSavepoint = undoLog.Count;
        int deferSavepoint = deferred.Count;
        int touchedSavepoint = touched.Count;
        if (entryDepth == 0)
        {
            _lastFailReason = null;
        }
        string? reasonAtEntry = _lastFailReason;
        _depth = entryDepth + 1;

        bool ok;
        try
        {
            ok = body.Invoke();
            if (ok)
            {
                ValidateTouchedInvariants(touched, touchedSavepoint);
            }
            else if (ReferenceEquals(_lastFailReason, reasonAtEntry))
            {
                _lastFailReason = "Transaction body returned false";
            }
        }
        catch (TxFailSignal)
        {
            ok = false;
        }
        catch
        {
            _depth = entryDepth;
            RollbackToSavepoint(undoLog, deferred, touched, undoSavepoint, deferSavepoint, touchedSavepoint);
            throw;
        }

        _depth = entryDepth;

        if (!ok || simulate)
        {
            RollbackToSavepoint(undoLog, deferred, touched, undoSavepoint, deferSavepoint, touchedSavepoint);
            return ok;
        }

        if (entryDepth == 0)
        {
            CommitTopLevel(undoLog, deferred, touched);
        }
        return true;
    }

    /// <summary>
    /// 执行可回滚事务块。若内部调用 Tx.Check(false) 或 Tx.Fail()，或提交前违反 [TxInvariant]，
    /// 所有已发生的修改按逆序自动回滚并返回 false。支持任意层级嵌套。
    /// 最外层提交后才执行 Tx.Defer 登记的动作；若这些动作抛出异常，事务仍然已经提交，
    /// 异常会在全部延迟动作执行完毕后从 Try 抛出（多个时为 AggregateException）。
    /// </summary>
    [Transacts]
    public static bool Try([Transacts] Action body) => Try(body, out _);

    /// <summary>
    /// 执行可回滚事务块，并在失败时输出触发回滚的原因或条件表达式（failReason）。
    /// </summary>
    [Transacts]
    public static bool Try([Transacts] Action body, out string? failReason)
    {
        ArgumentNullException.ThrowIfNull(body);
        var b = new ActionBody { Action = body };
        bool ok = Run(ref b, simulate: false);
        failReason = ok ? null : _lastFailReason;
        return ok;
    }

    /// <summary>
    /// 执行返回布尔值的可回滚事务块（无异常快速路径：返回 false 或调用 Tx.Fail() 均会自动回滚）。
    /// </summary>
    [Transacts]
    public static bool Try([Transacts] Func<bool> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var b = new PredicateBody { Func = body };
        return Run(ref b, simulate: false);
    }

    /// <summary>
    /// 带返回值的可回滚事务块：若成功通过所有校验并提交，将返回值赋给 result 并返回 true；
    /// 若中途失败回滚，result 置为 default 并返回 false（避免在事务内直接修改外部局部变量触发 VTX001）。
    /// </summary>
    [Transacts]
    public static bool Try<TResult>([Transacts] Func<TResult> body, [MaybeNullWhen(false)] out TResult result)
    {
        ArgumentNullException.ThrowIfNull(body);
        var b = new ResultBody<TResult> { Func = body };
        bool ok = Run(ref b, simulate: false);
        result = ok ? b.Result : default;
        return ok;
    }

    /// <summary>
    /// Dry-Run 沙盒推演：在真实状态上试跑 body（含 [TxInvariant] 校验），随后强制 100% 回滚所有状态与延迟副作用。
    /// 适用于 UI CanExecute 判定、AI 出牌/走位前向推演、以及 aidbg 在线无损探测。
    /// 注意：只有经过 Undo Log 的状态（[TxField]、Tx 容器、TxRandom、TxGodot）会被回滚。
    /// </summary>
    [Pure]
    public static bool Simulate([Transacts] Action body) => Simulate(body, out _);

    /// <summary>
    /// Dry-Run 沙盒推演，并在失败时输出具体的失败原因或条件表达式（failReason），适合 UI 禁用提示与 AI 调试诊断。
    /// </summary>
    [Pure]
    public static bool Simulate([Transacts] Action body, out string? failReason)
    {
        ArgumentNullException.ThrowIfNull(body);
        var b = new ActionBody { Action = body };
        bool ok = Run(ref b, simulate: true);
        failReason = ok ? null : _lastFailReason;
        return ok;
    }

    /// <summary>
    /// 带返回值的 Dry-Run 沙盒推演：在真实状态上试跑计算并提取返回值，随后强制 100% 回滚所有状态变化。
    /// </summary>
    [Pure]
    public static bool Simulate<TResult>([Transacts] Func<TResult> body, [MaybeNullWhen(false)] out TResult result)
    {
        ArgumentNullException.ThrowIfNull(body);
        var b = new ResultBody<TResult> { Func = body };
        bool ok = Run(ref b, simulate: true);
        result = ok ? b.Result : default;
        return ok;
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
        [Transacts] Action<T> action,
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
    public static List<T> WhereCan<T>(IEnumerable<T> candidates, [Transacts] Action<T> action)
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
            FailCore(string.IsNullOrEmpty(reason) ? expr : $"{reason} ({expr})");
        }
    }

    /// <summary>
    /// 立即触发当前事务失败并回滚，可附带失败原因说明。
    /// </summary>
    [Decides, DoesNotReturn]
    public static void Fail(string? reason = null)
    {
        FailCore(reason ?? "Tx.Fail()");
    }

    /// <summary>
    /// 失败的统一出口：事务内抛出内部回滚信号（由 Try/Simulate 捕获）；
    /// 事务外（例如在普通代码里直接调用了 [Decides] 方法）抛出公开的 <see cref="TxFailedException"/>，
    /// 而不是让内部信号类型泄漏给调用方。
    /// </summary>
    [DoesNotReturn]
    private static void FailCore(string? reason)
    {
        _lastFailReason = reason;
        if (_depth > 0)
        {
            throw TxFailSignal.Instance;
        }
        throw new TxFailedException(reason);
    }

    /// <summary>
    /// 供事务容器使用：事务内按 Verse 语义失败回滚；事务外抛出与 BCL 容器一致的标准异常。
    /// </summary>
    [DoesNotReturn]
    internal static void FailOrThrow(string reason, Exception outsideTransaction)
    {
        if (_depth > 0)
        {
            _lastFailReason = reason;
            throw TxFailSignal.Instance;
        }
        throw outsideTransaction;
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
            FailCore(string.IsNullOrEmpty(reason) ? $"{expr} is not null" : $"{reason} ({expr} is not null)");
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
            FailCore(string.IsNullOrEmpty(reason) ? $"{expr}.HasValue" : $"{reason} ({expr}.HasValue)");
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

        var set = _touchedSet ??= new HashSet<ITxObject>(ReferenceEqualityComparer.Instance);
        if (set.Add(obj))
        {
            (_touchedObjects ??= new List<ITxObject>(16)).Add(obj);
        }
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
        // 校验过程中可能继续 Touch 新对象（例如不变量方法读取了其它事务对象），按索引遍历即可覆盖新增项。
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
        List<Exception>? errors = null;
        for (int i = undoLog.Count - 1; i >= undoSavepoint; i--)
        {
            try
            {
                undoLog[i].UndoAndRecycle();
            }
            catch (Exception ex)
            {
                // 某一步恢复失败时继续恢复其余步骤，尽量让状态回到事务前；最后统一报告。
                (errors ??= new List<Exception>()).Add(ex);
            }
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
            var set = _touchedSet;
            if (set != null)
            {
                for (int i = touchedSavepoint; i < touched.Count; i++)
                {
                    set.Remove(touched[i]);
                }
            }
            touched.RemoveRange(touchedSavepoint, touchedRemoveCount);
        }

        if (errors != null)
        {
            throw new TxRollbackException(errors);
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
        _touchedSet?.Clear();

        if (deferred.Count == 0)
            return;

        // 先拷贝并清空，再在事务外逐个执行：延迟动作内部可以安全地开启新的顶层事务。
        var toRun = deferred.ToArray();
        deferred.Clear();
        List<Exception>? errors = null;
        for (int i = 0; i < toRun.Length; i++)
        {
            try
            {
                toRun[i]();
            }
            catch (Exception ex)
            {
                (errors ??= new List<Exception>()).Add(ex);
            }
        }

        if (errors != null)
        {
            if (errors.Count == 1)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            }
            throw new AggregateException("VTX: 事务已提交，但部分 Tx.Defer 延迟动作抛出了异常。", errors);
        }
    }
}

/// <summary>
/// 在事务之外调用了 Tx.Check / Tx.Fail / Tx.NotNull（或调用了内部使用它们的 [Decides] 方法）且条件不成立时抛出。
/// [Decides] 方法应当在 Tx.Try / Tx.Simulate / Tx.FirstOf / Tx.WhereCan 或另一个事务方法内部调用。
/// </summary>
public sealed class TxFailedException : InvalidOperationException
{
    public string? Reason { get; }

    public TxFailedException(string? reason)
        : base($"VTX: 在事务之外发生了失败（{reason ?? "未提供原因"}）。[Decides] 方法应在 Tx.Try / Tx.Simulate 内调用。")
    {
        Reason = reason;
    }
}

/// <summary>
/// 回滚过程中有 Undo 步骤自身抛出了异常。其余步骤仍已尽量恢复；状态可能不完整。
/// </summary>
public sealed class TxRollbackException : AggregateException
{
    public TxRollbackException(IEnumerable<Exception> errors)
        : base("VTX: 回滚时部分 Undo 步骤抛出了异常，状态可能未完全恢复。", errors)
    {
    }
}
