#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace VTX;

/// <summary>
/// VTX 单线程内存事务运行时：
/// 1. Savepoint 嵌套栈 + 零闭包对象池 Undo Log；
/// 2. 失败 = 返回 false：[Decides] 方法一律返回 bool，用 Tx.Require / Tx.Failed 记录原因并返回 false，
///    调用方 `if (!X()) return false;` 一路传播到 Tx.Try，那里回滚整个事务。失败路径不抛异常、不分配内存。
///    异常只表示 bug：事务里抛出的任何异常都会先回滚，再原样抛给调用方。
/// 3. [CallerArgumentExpression] 自动失败原因捕获（LastFailReason）；
/// 4. FirstOf / WhereCan 多候选回溯搜索；
/// 5. [TxInvariant] 提交前类不变量契约自动校验（返回 false 即回滚）；
/// 6. 只能在主线程使用；后台线程推演独立数据时用 <see cref="EnterWorkerScope"/>。
/// </summary>
public static class Tx
{
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
    [ThreadStatic] private static int _workerScopeDepth;

    // 失败原因延迟格式化：失败时只记下原因和条件表达式，读取 LastFailReason 时才拼字符串，
    // 这样失败路径本身不分配内存。
    private enum FailKind : byte
    {
        None,
        Message,
        Condition,
        BodyReturnedFalse,
    }

    [ThreadStatic] private static FailKind _failKind;
    [ThreadStatic] private static string? _failReason;
    [ThreadStatic] private static string? _failExpr;
    [ThreadStatic] private static string? _failText;
    [ThreadStatic] private static int _failSeq;

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
    /// 最近一次事务失败的原因描述或触发失败的源码条件表达式（由 Tx.Require / Tx.Failed 自动记录）。
    /// </summary>
    public static string? LastFailReason
    {
        [Pure] get => _failText ??= FormatFail(_failKind, _failReason, _failExpr);
    }

    private static void SetFail(FailKind kind, string? reason, string? expr)
    {
        _failKind = kind;
        _failReason = reason;
        _failExpr = expr;
        _failText = null;
        _failSeq++;
    }

    private static string? FormatFail(FailKind kind, string? reason, string? expr) => kind switch
    {
        FailKind.Message => reason,
        FailKind.Condition => string.IsNullOrEmpty(reason) ? expr : $"{reason} ({expr})",
        FailKind.BodyReturnedFalse => "Transaction body returned false",
        _ => null,
    };

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

    private struct StateResultBody<TState, TResult> : IBody
    {
        public TxFunc<TState, TResult> Func;
        public TState State;
        public TResult Result;
        public bool Invoke() => Func(State, out Result);
    }

    private struct PredicateBody : IBody
    {
        public Func<bool> Func;
        public bool Invoke() => Func();
    }

    private struct StatePredicateBody<TState> : IBody
    {
        public Func<TState, bool> Func;
        public TState State;
        public bool Invoke() => Func(State);
    }

    private static bool Run<TBody>(ref TBody body, bool simulate) where TBody : struct, IBody
    {
        if (_workerScopeDepth == 0 && !VtxThread.IsMainThread)
        {
            throw new InvalidOperationException(
                "VTX: 事务只能在主线程执行（当前在后台线程）。" +
                "如果是在后台线程对只属于这个线程的数据做推演，先 using var scope = Tx.EnterWorkerScope();");
        }

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
            _failKind = FailKind.None;
            _failReason = null;
            _failExpr = null;
            _failText = null;
        }
        int failSeqAtEntry = _failSeq;
        _depth = entryDepth + 1;

        bool ok;
        try
        {
            ok = body.Invoke() && ValidateTouchedInvariants(touched, touchedSavepoint);
            if (!ok && _failSeq == failSeqAtEntry)
            {
                SetFail(FailKind.BodyReturnedFalse, null, null);
            }
        }
        catch
        {
            // 异常 = bug（不是失败）：先回滚，再把异常原样抛给调用方
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
    /// 执行一段不会失败的可回滚修改：只有提交前违反 [TxInvariant] 时回滚并返回 false；抛出异常时回滚并重新抛出。
    /// 会失败的逻辑写成返回 bool 的 [Decides] 方法，用 Try(Func&lt;bool&gt;) 或 Try(state, static ...) 调用。
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
        failReason = ok ? null : LastFailReason;
        return ok;
    }

    /// <summary>
    /// 执行可回滚事务块：body 返回 false（或提交前违反 [TxInvariant]）时，所有修改按逆序回滚并返回 false。支持任意层级嵌套；
    /// 嵌套在另一个事务里时就是一个保存点：失败只回滚这一段。
    /// 失败原因可以从 <see cref="LastFailReason"/> 读取（需要 out 参数时用 Try(state, body, out reason)）。
    /// </summary>
    [Transacts]
    public static bool Try([Transacts] Func<bool> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var b = new PredicateBody { Func = body };
        return Run(ref b, simulate: false);
    }

    /// <summary>执行返回布尔值的可回滚事务块，并在失败时输出原因。</summary>
    [Transacts]
    public static bool Try([Transacts] Func<bool> body, out string? failReason)
    {
        ArgumentNullException.ThrowIfNull(body);
        var b = new PredicateBody { Func = body };
        bool ok = Run(ref b, simulate: false);
        failReason = ok ? null : LastFailReason;
        return ok;
    }

    /// <summary>
    /// 带结果的事务：body 是一个返回 bool、通过 out 带回结果的委托（通常直接转发给 bool TryX(..., out T result) 的 [Decides] 方法）。
    /// 成功提交时 result 为 body 给出的值；失败时为 default。lambda 带 out 参数时需要写出参数类型：
    /// <code>Tx.Try(this, static (Forge s, out int bonus) => s.TryRoll(20, out bonus), out int bonus)</code>
    /// </summary>
    [Transacts]
    public static bool Try<TState, TResult>(TState state, [Transacts] TxFunc<TState, TResult> body, [MaybeNullWhen(false)] out TResult result)
    {
        ArgumentNullException.ThrowIfNull(body);
        var b = new StateResultBody<TState, TResult> { Func = body, State = state };
        bool ok = Run(ref b, simulate: false);
        result = ok ? b.Result : default;
        return ok;
    }

    /// <summary>
    /// 零分配版本：把需要的对象作为 state 传进来，body 写成 static lambda，就不会为闭包分配内存。
    /// body 返回 false（例如 <c>return Tx.Failed("金币不足")</c>）即失败回滚，不抛异常。
    /// 在事务内部调用时就是一个“保存点”：失败只回滚这一段，适合实现 Verse 的 <c>A[] or B[]</c>。
    /// <code>if (!Tx.Try(this, static s => s.TryBuy(10))) { ... }</code>
    /// </summary>
    [Transacts]
    public static bool Try<TState>(TState state, [Transacts] Func<TState, bool> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var b = new StatePredicateBody<TState> { Func = body, State = state };
        return Run(ref b, simulate: false);
    }

    /// <summary>零分配版本的 Try，并在失败时输出原因。</summary>
    [Transacts]
    public static bool Try<TState>(TState state, [Transacts] Func<TState, bool> body, out string? failReason)
    {
        ArgumentNullException.ThrowIfNull(body);
        var b = new StatePredicateBody<TState> { Func = body, State = state };
        bool ok = Run(ref b, simulate: false);
        failReason = ok ? null : LastFailReason;
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
        failReason = ok ? null : LastFailReason;
        return ok;
    }

    /// <summary>Dry-Run：试跑返回 bool 的事务块后 100% 回滚。body 返回 false 即判定失败。</summary>
    [Pure]
    public static bool Simulate([Transacts] Func<bool> body) => Simulate(body, out _);

    /// <summary>Dry-Run：试跑返回 bool 的事务块后 100% 回滚，并在失败时输出原因（适合 UI 禁用提示）。</summary>
    [Pure]
    public static bool Simulate([Transacts] Func<bool> body, out string? failReason)
    {
        ArgumentNullException.ThrowIfNull(body);
        var b = new PredicateBody { Func = body };
        bool ok = Run(ref b, simulate: true);
        failReason = ok ? null : LastFailReason;
        return ok;
    }

    /// <summary>
    /// 带结果的 Dry-Run：试跑后 100% 回滚，但把 body 给出的结果带出来（例如预览这次强化会掷出多少点）。
    /// <code>Tx.Simulate(this, static (Forge s, out int bonus) => s.TryRoll(20, out bonus), out int preview)</code>
    /// </summary>
    [Pure]
    public static bool Simulate<TState, TResult>(TState state, [Transacts] TxFunc<TState, TResult> body, [MaybeNullWhen(false)] out TResult result)
    {
        ArgumentNullException.ThrowIfNull(body);
        var b = new StateResultBody<TState, TResult> { Func = body, State = state };
        bool ok = Run(ref b, simulate: true);
        result = ok ? b.Result : default;
        return ok;
    }

    /// <summary>零分配版本的 Simulate：试跑后 100% 回滚。body 返回 false 即判定失败。</summary>
    [Pure]
    public static bool Simulate<TState>(TState state, [Transacts] Func<TState, bool> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var b = new StatePredicateBody<TState> { Func = body, State = state };
        return Run(ref b, simulate: true);
    }

    /// <summary>零分配版本的 Simulate，并在失败时输出原因。</summary>
    [Pure]
    public static bool Simulate<TState>(TState state, [Transacts] Func<TState, bool> body, out string? failReason)
    {
        ArgumentNullException.ThrowIfNull(body);
        var b = new StatePredicateBody<TState> { Func = body, State = state };
        bool ok = Run(ref b, simulate: true);
        failReason = ok ? null : LastFailReason;
        return ok;
    }

    /// <summary>
    /// 多候选回溯执行（对应 Verse 的 Choice 选择器）：按顺序对每个候选执行 action(candidate)；
    /// 返回 false 的候选回滚它的修改并尝试下一个，第一个返回 true 的候选提交并通过 chosen 返回。
    /// </summary>
    [Transacts]
    public static bool FirstOf<T>(
        IEnumerable<T> candidates,
        [Transacts] Func<T, bool> action,
        [MaybeNullWhen(false)] out T chosen)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(action);

        foreach (T candidate in candidates)
        {
            var b = new StatePredicateBody<T> { Func = action, State = candidate };
            if (Run(ref b, simulate: false))
            {
                chosen = candidate;
                return true;
            }
        }

        chosen = default;
        return false;
    }

    /// <summary>
    /// 多候选无损筛选（基于 Simulate）：在真实状态上依次试跑每个候选，每次试跑后 100% 回滚；
    /// action 返回 true（且通过 [TxInvariant]）的合法候选追加到 results（不会先清空），返回追加的个数。
    /// 适合战棋合法落点高亮、麻将可打出牌筛选、AI 搜索。
    /// </summary>
    [Pure]
    public static int WhereCan<T>(IEnumerable<T> candidates, [Transacts] Func<T, bool> action, List<T> results)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(results);

        int added = 0;
        foreach (T candidate in candidates)
        {
            var b = new StatePredicateBody<T> { Func = action, State = candidate };
            if (Run(ref b, simulate: true))
            {
                results.Add(candidate);
                added++;
            }
        }
        return added;
    }

    /// <summary>
    /// 条件检查：条件成立返回 true；不成立时记录失败原因（或条件源码表达式）并返回 false。
    /// <c>if (!Tx.Require(Gold >= price, "金币不足")) return false;</c>
    /// 失败靠返回值一路传播到 Tx.Try，那里会回滚整个事务。
    /// </summary>
    [Decides]
    public static bool Require(
        bool condition,
        string? reason = null,
        [CallerArgumentExpression(nameof(condition))] string? expr = null)
    {
        if (condition)
            return true;
        SetFail(FailKind.Condition, reason, expr);
        return false;
    }

    /// <summary>
    /// 失败：记录原因并返回 false。用法：<c>if (target is not { } t) return Tx.Failed("没有目标");</c>
    /// </summary>
    [Decides]
    public static bool Failed(string? reason = null)
    {
        SetFail(FailKind.Message, reason ?? "Tx.Failed()", null);
        return false;
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
            if (_workerScopeDepth > 0)
                EnsureNotGodotObject(owner);
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
            if (_workerScopeDepth > 0)
                EnsureNotGodotObject(owner);
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

    /// <summary>
    /// 声明“当前后台线程要对只属于它自己的数据开事务”（例如 AI 在一份棋盘拷贝上做 Simulate 搜索）。
    /// 在作用域内允许 Tx.Try / Simulate；但事务里碰到 Godot 对象（节点、资源）仍然会抛异常。
    /// 每个线程有自己的 Undo Log，互不干扰；但被多个线程同时修改的 [TxField] 对象仍然是数据竞争，需要你保证数据只属于这个线程。
    /// 在主线程调用时什么也不做。
    /// <code>await Task.Run(() => { using var scope = Tx.EnterWorkerScope(); return Search(boardCopy); });</code>
    /// </summary>
    public static WorkerScope EnterWorkerScope()
    {
        if (VtxThread.IsMainThread)
            return default;
        _workerScopeDepth++;
        return new WorkerScope(active: true);
    }

    /// <summary><see cref="EnterWorkerScope"/> 返回的作用域，离开 using 时结束。不能跨 await 使用。</summary>
    public ref struct WorkerScope
    {
        private bool _active;

        internal WorkerScope(bool active) => _active = active;

        public void Dispose()
        {
            if (_active)
            {
                _active = false;
                _workerScopeDepth--;
            }
        }
    }

    private static void EnsureNotGodotObject<TOwner>(TOwner owner)
    {
        if (owner is Godot.GodotObject)
        {
            throw new InvalidOperationException(
                "VTX: 后台线程的事务里不能修改 Godot 对象（" + owner.GetType().Name + "）。" +
                "Godot 对象只能在主线程修改：把计算结果带回主线程，再在主线程的事务里写入。");
        }
    }

    private static bool ValidateTouchedInvariants(List<ITxObject> touched, int touchedSavepoint)
    {
        // 校验过程中可能继续 Touch 新对象（例如不变量方法读取了其它事务对象），按索引遍历即可覆盖新增项。
        for (int i = touchedSavepoint; i < touched.Count; i++)
        {
            int seq = _failSeq;
            if (!touched[i].ValidateTxInvariants())
            {
                if (_failSeq == seq)
                    SetFail(FailKind.Message, "[TxInvariant] 校验未通过: " + touched[i].GetType().Name, null);
                return false;
            }
        }
        return true;
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
/// 带结果的事务体：返回 false 表示失败，成功时通过 out 给出结果。见 <see cref="Tx.Try{TState, TResult}"/>。
/// </summary>
public delegate bool TxFunc<in TState, TResult>(TState state, out TResult result);

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
