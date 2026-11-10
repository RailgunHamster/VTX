#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace VTX;

/// <summary>
/// 结构化并发作用域：允许通过 Branch 分叉出不阻塞主流程的子任务（如循环音效、持续粒子），
/// 当外层 WithScope 块结束或取消时，所有未完成的 Branch 子任务会被自动取消并等待清理完毕，彻底消灭孤儿协程。
/// </summary>
public sealed class TxScope
{
    private readonly CancellationTokenSource _cts;
    private readonly List<Task<bool>> _branches = new();
    private bool _closed;

    internal TxScope(CancellationToken parentCt)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
    }

    /// <summary>作用域的取消令牌：作用域结束或父令牌取消时触发。可传给 Rush 等 API，让后台分支随作用域一起回收。</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// 在当前作用域下启动一个非阻塞子协程（对应 Verse 的 branch:）。
    /// 当外层 WithScope 结束时，若该子协程仍未完成，会自动收到取消信号并执行其 Defer/finally 清理。
    /// 子协程抛出的非取消异常会在 WithScope 结束时抛出（若主体本身也抛了异常，以主体异常为准）。
    /// </summary>
    public void Branch(Func<CancellationToken, Task> branchTask)
    {
        ArgumentNullException.ThrowIfNull(branchTask);
        if (_closed)
            throw new InvalidOperationException("VTX: TxScope 已结束，不能再启动新的 Branch。");
        // 长生命周期的作用域可能反复 Branch：顺手丢掉已经正常结束的分支，避免列表无限增长（失败的分支保留到作用域结束时报告）
        _branches.RemoveAll(static t => t.Status == TaskStatus.RanToCompletion);
        _branches.Add(TxTask.RunBranchSafeInternal(branchTask, _cts.Token));
    }

    /// <summary>当前仍被作用域跟踪的分支数（测试用）。</summary>
    internal int TrackedBranchCount => _branches.Count;

    internal async Task<Exception?> CancelAndDrainBranchesAsync()
    {
        _closed = true;
        // 同步 Cancel：取消回调在当前（主）线程上执行。CancelAsync 会把回调丢到线程池。
        _cts.Cancel();

        Exception? firstError = null;
        foreach (var branch in _branches.ToArray())
        {
            try
            {
                await branch;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                firstError ??= ex;
            }
        }
        _cts.Dispose();
        return firstError;
    }
}

/// <summary>
/// VTX 帧驱动结构化并发运行时：
/// 1. 基于游戏逻辑时间推进（Sleep）与可取消信号等待（WaitSignal）；
/// 2. Race（赢家胜出，输家立即取消）、Sync（等待全部）、Rush（首个完成即继续，其余后台继续跑）；
/// 3. WithScope + Branch（结构化分叉子任务，出作用域自动回收）；
/// 4. Defer（协程作用域退出或被取消时自动按 LIFO 逆序执行清理）。
///
/// 线程模型：所有 API 都应在 Godot 主线程调用；内部不使用 ConfigureAwait(false)，
/// 续体通过 Godot 的 SynchronizationContext 回到主线程。从其它线程取消令牌（例如 CancelAfter）也是安全的：
/// 涉及 Godot 对象的清理会被投递回主线程执行。
/// </summary>
public static class TxTask
{
    /// <summary>
    /// 零分配的作用域退出清理守卫（配合 using var _ = TxTask.Defer(...) 使用）。
    /// </summary>
    public struct DeferGuard : IDisposable
    {
        private Action? _cleanup;

        internal DeferGuard(Action cleanup) => _cleanup = cleanup;

        public void Dispose()
        {
            var action = Interlocked.Exchange(ref _cleanup, null);
            action?.Invoke();
        }
    }

    private sealed class TimerEntry
    {
        public double RemainingSeconds;
        public bool IgnorePause;
        public TaskCompletionSource<bool> Tcs = null!;
        public CancellationTokenRegistration Registration;
    }

    private sealed class PendingWait
    {
        public GodotObject Source = null!;
        public Action<Exception> FailFreed = null!;
        public Func<bool> IsDone = null!;
    }

    private static readonly List<TimerEntry> _timers = new();
    private static readonly List<PendingWait> _pendingWaits = new();
    private static double _gameTimeSeconds;
    private static bool _autoHooked;
    private static ulong _lastTickFrame = ulong.MaxValue;

    /// <summary>当前累计的逻辑游戏时间（秒，SceneTree 暂停期间不前进）。</summary>
    public static double GameTimeSeconds
    {
        [Pure] get => _gameTimeSeconds;
    }

    /// <summary>当前挂起中的帧定时器数量（用于验证 Race/Scope 取消后零泄漏）。</summary>
    public static int ActiveTimerCount
    {
        [Pure]
        get
        {
            int n = 0;
            foreach (var t in _timers)
            {
                if (!t.Tcs.Task.IsCompleted)
                    n++;
            }
            return n;
        }
    }

    /// <summary>当前挂起中的信号等待数量（用于验证取消后零泄漏）。</summary>
    public static int ActiveSignalWaitCount
    {
        [Pure]
        get
        {
            int n = 0;
            foreach (var w in _pendingWaits)
            {
                if (!w.IsDone())
                    n++;
            }
            return n;
        }
    }

    /// <summary>
    /// 注册协程退出或被取消时的自动清理动作（对应 Verse 的 defer:）。
    /// 用法：using var _ = TxTask.Defer(() => ResetState());
    /// </summary>
    public static DeferGuard Defer(Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        return new DeferGuard(cleanup);
    }

    /// <summary>
    /// 手动推进所有按游戏时间挂起的任务（视为未暂停）。
    /// 若已在 SceneTree 环境运行，系统会自动通过 ProcessFrame 驱动；同一帧内的重复调用会被去重。
    /// </summary>
    public static void Tick(double delta) => TickInternal(delta, paused: false);

    private static void TickInternal(double delta, bool paused)
    {
        ulong frame = Engine.GetProcessFrames();
        if (frame != 0 && frame == _lastTickFrame)
            return;
        _lastTickFrame = frame;

        PollPendingWaits();

        if (!(delta > 0))
            return;

        if (!paused)
        {
            _gameTimeSeconds += delta;
        }

        for (int i = _timers.Count - 1; i >= 0; i--)
        {
            var entry = _timers[i];
            if (entry.Tcs.Task.IsCompleted)
            {
                _timers.RemoveAt(i);
                continue;
            }

            if (paused && !entry.IgnorePause)
                continue;

            entry.RemainingSeconds -= delta;
            if (entry.RemainingSeconds <= 0)
            {
                _timers.RemoveAt(i);
                entry.Registration.Dispose();
                entry.Tcs.TrySetResult(true);
            }
        }
    }

    private static void PollPendingWaits()
    {
        for (int i = _pendingWaits.Count - 1; i >= 0; i--)
        {
            var w = _pendingWaits[i];
            if (w.IsDone())
            {
                _pendingWaits.RemoveAt(i);
                continue;
            }
            if (!GodotObject.IsInstanceValid(w.Source))
            {
                _pendingWaits.RemoveAt(i);
                w.FailFreed(new ObjectDisposedException(nameof(GodotObject), "VTX: WaitSignal 的信号源对象在信号发出前已被释放。"));
            }
        }
    }

    private static void EnsureAutoHook()
    {
        if (_autoHooked)
            return;

        if (Engine.GetMainLoop() is SceneTree tree)
        {
            _autoHooked = true;
            tree.ProcessFrame += () =>
            {
                if (Engine.GetMainLoop() is SceneTree activeTree && activeTree.Root is Window root)
                {
                    TickInternal(root.GetProcessDeltaTime(), activeTree.Paused);
                }
                else
                {
                    TickInternal(1.0 / 60.0, paused: false);
                }
            };
        }
    }

    /// <summary>
    /// 按游戏帧时间挂起指定秒数（受 Engine.TimeScale 影响；SceneTree 暂停期间不计时），
    /// 若被 Race 或父作用域取消则立即中断并清理。seconds 不是正数时立即完成。
    /// </summary>
    [Suspends]
    public static Task Sleep(double seconds, CancellationToken ct = default) => SleepCore(seconds, ignorePause: false, ct);

    /// <summary>
    /// 与 <see cref="Sleep"/> 相同，但 SceneTree 暂停期间照常计时（适合暂停菜单里的动画、倒计时）。
    /// </summary>
    [Suspends]
    public static Task SleepIgnoringPause(double seconds, CancellationToken ct = default) => SleepCore(seconds, ignorePause: true, ct);

    private static Task SleepCore(double seconds, bool ignorePause, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled(ct);

        if (!(seconds > 0))
            return Task.CompletedTask;

        EnsureAutoHook();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new TimerEntry
        {
            RemainingSeconds = seconds,
            IgnorePause = ignorePause,
            Tcs = tcs
        };

        _timers.Add(entry);
        if (ct.CanBeCanceled)
        {
            // 回调可能在任意线程上执行（例如 CancelAfter），这里只完成任务；
            // 定时器条目由主线程上的 Tick 清理，避免跨线程修改 _timers。
            entry.Registration = ct.Register(static state =>
            {
                var (e, token) = ((TimerEntry, CancellationToken))state!;
                e.Tcs.TrySetCanceled(token);
            }, (entry, ct));
        }
        return tcs.Task;
    }

    private static Task<TResult> WaitSignalCore<TResult>(
        GodotObject source,
        StringName signalName,
        Func<TaskCompletionSource<TResult>, Action, Callable> makeCallable,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(signalName);

        if (ct.IsCancellationRequested)
            return Task.FromCanceled<TResult>(ct);

        if (!GodotObject.IsInstanceValid(source))
            return Task.FromException<TResult>(new ObjectDisposedException(nameof(GodotObject), "VTX: WaitSignal 的信号源对象已被释放。"));

        EnsureAutoHook();

        var tcs = new TaskCompletionSource<TResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mainContext = SynchronizationContext.Current;
        Callable callable = default;
        CancellationTokenRegistration reg = default;
        bool disconnected = false;

        void DisconnectSafe()
        {
            if (disconnected)
                return;
            disconnected = true;
            reg.Dispose();
            if (GodotObject.IsInstanceValid(source) && source.IsConnected(signalName, callable))
            {
                source.Disconnect(signalName, callable);
            }
        }

        callable = makeCallable(tcs, DisconnectSafe);
        source.Connect(signalName, callable);

        _pendingWaits.Add(new PendingWait
        {
            Source = source,
            IsDone = () => tcs.Task.IsCompleted,
            FailFreed = ex =>
            {
                disconnected = true;
                reg.Dispose();
                tcs.TrySetException(ex);
            }
        });

        if (ct.CanBeCanceled)
        {
            reg = ct.Register(() =>
            {
                tcs.TrySetCanceled(ct);
                // 退订必须在主线程执行：若取消来自其它线程，则投递回主线程。
                if (mainContext == null || SynchronizationContext.Current == mainContext)
                    DisconnectSafe();
                else
                    mainContext.Post(static s => ((Action)s!)(), (Action)DisconnectSafe);
            });
        }

        return tcs.Task;
    }

    /// <summary>
    /// 可取消的信号等待（替代 Godot 原生不可取消的 ToSignal）。
    /// 当 ct 取消（例如在 Race 中落败）时，立即从 GodotObject 退订信号回调，杜绝协程泄漏；
    /// 若信号源在信号发出前被释放，任务以 ObjectDisposedException 结束（ToSignal 会永远挂起）。
    /// </summary>
    [Suspends]
    public static Task WaitSignal(GodotObject source, StringName signalName, CancellationToken ct = default) =>
        WaitSignalCore<bool>(source, signalName, static (tcs, disconnect) => Callable.From(() =>
        {
            disconnect();
            tcs.TrySetResult(true);
        }), ct);

    /// <summary>
    /// 带 1 个强类型参数返回值的可取消信号等待（对应 Verse 的 Event.Await()）：
    /// 当信号触发时，直接将信号携带的第 1 个参数作为 Task&lt;T0&gt; 返回值解包返回。
    /// 泛型参数个数必须与信号参数个数一致。
    /// </summary>
    [Suspends]
    public static Task<T0> WaitSignal<[MustBeVariant] T0>(
        GodotObject source,
        StringName signalName,
        CancellationToken ct = default) =>
        WaitSignalCore<T0>(source, signalName, static (tcs, disconnect) => Callable.From<T0>(arg0 =>
        {
            disconnect();
            tcs.TrySetResult(arg0);
        }), ct);

    /// <summary>
    /// 带 2 个强类型参数返回值的可取消信号等待：信号触发时直接返回元组 (T0, T1)。
    /// </summary>
    [Suspends]
    public static Task<(T0, T1)> WaitSignal<[MustBeVariant] T0, [MustBeVariant] T1>(
        GodotObject source,
        StringName signalName,
        CancellationToken ct = default) =>
        WaitSignalCore<(T0, T1)>(source, signalName, static (tcs, disconnect) => Callable.From<T0, T1>((arg0, arg1) =>
        {
            disconnect();
            tcs.TrySetResult((arg0, arg1));
        }), ct);

    /// <summary>
    /// 带 3 个强类型参数返回值的可取消信号等待：信号触发时直接返回元组 (T0, T1, T2)。
    /// </summary>
    [Suspends]
    public static Task<(T0, T1, T2)> WaitSignal<[MustBeVariant] T0, [MustBeVariant] T1, [MustBeVariant] T2>(
        GodotObject source,
        StringName signalName,
        CancellationToken ct = default) =>
        WaitSignalCore<(T0, T1, T2)>(source, signalName, static (tcs, disconnect) => Callable.From<T0, T1, T2>((arg0, arg1, arg2) =>
        {
            disconnect();
            tcs.TrySetResult((arg0, arg1, arg2));
        }), ct);

    /// <summary>
    /// 结构化并发 Race：所有分支同时启动，第一个完成的分支获胜，
    /// 其余落败分支立即收到 CancellationToken 取消信号并等待其 Defer/finally 块清理完毕后返回获胜分支下标。
    /// 若在决出胜负前父令牌被取消，则取消所有分支并抛出 OperationCanceledException。
    /// 胜者抛出的异常会原样抛出；落败分支在清理时抛出的非取消异常也会抛出。
    /// </summary>
    [Suspends]
    public static Task<int> Race(params Func<CancellationToken, Task>[] branches) =>
        Race(CancellationToken.None, branches);

    [Suspends]
    public static async Task<int> Race(CancellationToken parentCt, params Func<CancellationToken, Task>[] branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        if (branches.Length == 0)
            return -1;
        parentCt.ThrowIfCancellationRequested();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        var ct = linkedCts.Token;

        var tasks = new Task<bool>[branches.Length];
        for (int i = 0; i < branches.Length; i++)
        {
            tasks[i] = RunBranchSafeInternal(branches[i], ct);
        }

        Task<bool> winner = await Task.WhenAny(tasks);
        int winnerIndex = Array.IndexOf(tasks, winner);

        // 同步取消：落败分支的取消回调在主线程执行。
        linkedCts.Cancel();

        Exception? loserError = null;
        for (int i = 0; i < tasks.Length; i++)
        {
            if (i == winnerIndex)
                continue;
            try
            {
                await tasks[i];
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                loserError ??= ex;
            }
        }

        bool completedNaturally = await winner; // 胜者若抛异常，这里原样抛出
        if (!completedNaturally)
        {
            // 胜者是“因取消而结束”的分支：只可能是父令牌在决出胜负前被取消。
            throw new OperationCanceledException(parentCt);
        }

        if (loserError != null)
        {
            ExceptionDispatchInfo.Capture(loserError).Throw();
        }
        return winnerIndex;
    }

    /// <summary>
    /// 结构化并发 Sync：等待所有分支全部完成；任意分支异常或取消时，立即取消其余同级分支，
    /// 等它们清理完毕后抛出最先失败分支的异常。
    /// </summary>
    [Suspends]
    public static Task Sync(params Func<CancellationToken, Task>[] branches) =>
        Sync(CancellationToken.None, branches);

    [Suspends]
    public static async Task Sync(CancellationToken parentCt, params Func<CancellationToken, Task>[] branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        if (branches.Length == 0)
            return;
        parentCt.ThrowIfCancellationRequested();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        var ct = linkedCts.Token;
        Exception? firstError = null;

        async Task Watch(Func<CancellationToken, Task> branch)
        {
            try
            {
                await branch(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                firstError ??= ex;
                linkedCts.Cancel();
                throw;
            }
        }

        var tasks = new Task[branches.Length];
        for (int i = 0; i < branches.Length; i++)
        {
            tasks[i] = Watch(branches[i]);
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            if (firstError != null)
                ExceptionDispatchInfo.Capture(firstError).Throw();
            throw;
        }
    }

    /// <summary>
    /// 结构化并发 Rush（对应 Verse 的 rush:）：
    /// 所有分支同时启动；当第一个分支完成时，Rush 立即返回该获胜分支下标，主流程继续往下走；
    /// 其余未完成的分支继续在后台运行，直到它们自然完成或 parentCt 被取消。
    /// 建议传入 WithScope 的 scope.Token 作为 parentCt，使后台分支随作用域一起回收。
    /// 后台分支的异常不会被吞掉：会通过 GD.PushError 报告。
    /// </summary>
    [Suspends]
    public static Task<int> Rush(params Func<CancellationToken, Task>[] branches) =>
        Rush(CancellationToken.None, branches);

    [Suspends]
    public static async Task<int> Rush(CancellationToken parentCt, params Func<CancellationToken, Task>[] branches)
    {
        ArgumentNullException.ThrowIfNull(branches);
        if (branches.Length == 0)
            return -1;
        parentCt.ThrowIfCancellationRequested();

        var tasks = new Task<bool>[branches.Length];
        for (int i = 0; i < branches.Length; i++)
        {
            tasks[i] = RunBranchSafeInternal(branches[i], parentCt);
        }

        Task<bool> winner = await Task.WhenAny(tasks);
        int winnerIndex = Array.IndexOf(tasks, winner);

        for (int i = 0; i < tasks.Length; i++)
        {
            if (i != winnerIndex)
                ObserveBackgroundBranch(tasks[i]);
        }

        bool completedNaturally = await winner;
        if (!completedNaturally)
        {
            throw new OperationCanceledException(parentCt);
        }
        return winnerIndex;
    }

    private static async void ObserveBackgroundBranch(Task branch)
    {
        try
        {
            await branch;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            GD.PushError($"VTX: Rush 后台分支抛出了未处理的异常: {ex}");
        }
    }

    /// <summary>
    /// 开启一个结构化并发作用域（支持在内部调用 scope.Branch(...) 启动伴随子任务）。
    /// 当主逻辑退出该作用域时，所有由 scope.Branch 启动但尚未结束的子任务会自动被取消并等待清理完成。
    /// 主体的异常优先抛出；主体正常结束时，才抛出子任务的非取消异常。
    /// </summary>
    [Suspends]
    public static async Task WithScope(Func<TxScope, Task> body, CancellationToken parentCt = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var scope = new TxScope(parentCt);
        ExceptionDispatchInfo? bodyError = null;
        try
        {
            await body(scope);
        }
        catch (Exception ex)
        {
            bodyError = ExceptionDispatchInfo.Capture(ex);
        }

        Exception? branchError = await scope.CancelAndDrainBranchesAsync();
        bodyError?.Throw();
        if (branchError != null)
        {
            ExceptionDispatchInfo.Capture(branchError).Throw();
        }
    }

    /// <summary>
    /// 运行一个分支：正常完成返回 true；因 ct 取消而结束返回 false；其它异常原样抛出。
    /// 分支同步抛出的异常同样转为失败的任务，而不是在启动循环里直接抛出。
    /// </summary>
    internal static async Task<bool> RunBranchSafeInternal(Func<CancellationToken, Task> branch, CancellationToken ct)
    {
        try
        {
            await branch(ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
