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

/// <summary>挂起中的等待属于哪一类（用于 <see cref="TxTask.GetActiveWaits"/> 调试）。</summary>
public enum TxWaitKind
{
    /// <summary>TxTask.Sleep / SleepIgnoringPause：按游戏时间等待。</summary>
    Sleep,
    /// <summary>TxTask.NextFrame / DelayFrames：按处理帧等待。</summary>
    Frames,
    /// <summary>TxTask.NextPhysicsFrame / DelayPhysicsFrames：按物理帧等待。</summary>
    PhysicsFrames,
    /// <summary>TxTask.WaitUntil：每帧检查一次条件。</summary>
    Until,
    /// <summary>TxTask.WaitSignal：等待 Godot 信号。</summary>
    Signal,
}

/// <summary>一条挂起中的等待的快照（<see cref="TxTask.GetActiveWaits"/> 返回）。</summary>
/// <param name="Kind">等待的类型。</param>
/// <param name="Detail">人类可读的说明，例如“还剩 0.42 秒”。</param>
/// <param name="StartedAtGameTime">开始等待时的游戏时间（秒）。</param>
/// <param name="StackTrace">开始等待时的调用栈；只有 <see cref="TxTask.TrackStackTraces"/> 为 true 时才记录。</param>
public readonly record struct TxWaitInfo(TxWaitKind Kind, string Detail, double StartedAtGameTime, string? StackTrace);

/// <summary>
/// VTX 帧驱动结构化并发运行时：
/// 1. 基于游戏逻辑时间 / 帧推进的等待（Sleep、NextFrame、DelayFrames、NextPhysicsFrame、WaitUntil）与可取消信号等待（WaitSignal）；
/// 2. Race（赢家胜出，输家立即取消）、Sync（等待全部）、Rush（首个完成即继续，其余后台继续跑）；
/// 3. WithScope + Branch（结构化分叉子任务，出作用域自动回收）；
/// 4. Defer（协程作用域退出或被取消时自动按 LIFO 逆序执行清理）；
/// 5. LifetimeOf(node)（节点被释放 / QueueFree 时自动取消的令牌）与 Forget()（发出去不管的任务也不会吞掉异常）；
/// 6. GetActiveWaits()（列出所有挂起中的等待，排查协程泄漏）。
///
/// 线程模型：所有 API 都应在 Godot 主线程调用；内部不使用 ConfigureAwait(false)，
/// 续体通过 Godot 的 SynchronizationContext 回到主线程。从其它线程取消令牌（例如 CancelAfter）也是安全的：
/// 涉及 Godot 对象的清理会被投递回主线程执行。
///
/// 暂停：SceneTree.Paused 为 true 时游戏时间、处理帧计数、物理帧计数都不前进，WaitUntil 也不检查条件；
/// 只有 SleepIgnoringPause 照常计时。生命周期检测（LifetimeOf / 信号源释放）不受暂停影响。
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

    // ---------------------------------------------------------------------
    // 统一的等待登记表：每一个挂起中的等待都是一个 WaitEntry，由主线程上的帧回调推进。
    // 取消回调可能在任意线程触发，因此它们只负责完成 Task；条目本身只在主线程上被移除。
    // ---------------------------------------------------------------------

    private abstract class WaitEntry
    {
        public TxWaitKind Kind;
        public double StartedAt;
        public string? Stack;
        public abstract bool IsDone { get; }
        public abstract string Describe();
        public virtual void OnProcess(double delta, bool paused) { }
        public virtual void OnPhysics(bool paused) { }
    }

    private sealed class TimerWait : WaitEntry
    {
        public double RemainingSeconds;
        public bool IgnorePause;
        public readonly TaskCompletionSource<bool> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;
        public override bool IsDone => Tcs.Task.IsCompleted;
        public override string Describe() => $"还剩 {Math.Max(0, RemainingSeconds):0.###} 秒{(IgnorePause ? "（忽略暂停）" : string.Empty)}";

        public override void OnProcess(double delta, bool paused)
        {
            if (paused && !IgnorePause)
                return;
            RemainingSeconds -= delta;
            if (RemainingSeconds <= 0)
            {
                Registration.Dispose();
                Tcs.TrySetResult(true);
            }
        }
    }

    private sealed class FrameWait : WaitEntry
    {
        public long TargetFrame;
        public bool Physics;
        public readonly TaskCompletionSource<bool> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;
        public override bool IsDone => Tcs.Task.IsCompleted;
        public override string Describe() =>
            $"还要等 {Math.Max(0, TargetFrame - (Physics ? _physicsFrames : _gameFrames))} 个{(Physics ? "物理帧" : "帧")}";

        public override void OnProcess(double delta, bool paused)
        {
            if (!Physics && _gameFrames >= TargetFrame)
                Complete();
        }

        public override void OnPhysics(bool paused)
        {
            if (Physics && _physicsFrames >= TargetFrame)
                Complete();
        }

        private void Complete()
        {
            Registration.Dispose();
            Tcs.TrySetResult(true);
        }
    }

    private sealed class UntilWait : WaitEntry
    {
        public Func<bool> Condition = null!;
        public readonly TaskCompletionSource<bool> Tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;
        public int Checks;
        public override bool IsDone => Tcs.Task.IsCompleted;
        public override string Describe() => $"条件已检查 {Checks} 次，仍未满足";

        public override void OnProcess(double delta, bool paused)
        {
            if (paused)
                return;
            Checks++;
            try
            {
                if (Condition())
                {
                    Registration.Dispose();
                    Tcs.TrySetResult(true);
                }
            }
            catch (Exception ex)
            {
                Registration.Dispose();
                Tcs.TrySetException(ex);
            }
        }
    }

    private sealed class SignalWait : WaitEntry
    {
        public GodotObject Source = null!;
        public string SignalName = string.Empty;
        public Func<bool> Done = null!;
        public Action<Exception> FailFreed = null!;
        public override bool IsDone => Done();
        public override string Describe() => $"等待信号 '{SignalName}'";

        public override void OnProcess(double delta, bool paused)
        {
            if (!GodotObject.IsInstanceValid(Source))
            {
                FailFreed(new ObjectDisposedException(nameof(GodotObject), $"VTX: WaitSignal('{SignalName}') 的信号源对象在信号发出前已被释放。"));
            }
        }
    }

    private static readonly List<WaitEntry> _waits = new();
    private static readonly Dictionary<Node, CancellationTokenSource> _lifetimes = new(ReferenceEqualityComparer.Instance);
    private static double _gameTimeSeconds;
    private static long _gameFrames;
    private static long _physicsFrames;
    private static bool _autoHooked;
    private static ulong _lastTickFrame = ulong.MaxValue;
    private static ulong _lastPhysicsFrame = ulong.MaxValue;

    /// <summary>当前累计的逻辑游戏时间（秒，SceneTree 暂停期间不前进）。</summary>
    public static double GameTimeSeconds
    {
        [Pure] get => _gameTimeSeconds;
    }

    /// <summary>累计的未暂停处理帧数（NextFrame / DelayFrames 以它计数）。</summary>
    public static long GameFrames
    {
        [Pure] get => _gameFrames;
    }

    /// <summary>累计的未暂停物理帧数（NextPhysicsFrame / DelayPhysicsFrames 以它计数）。</summary>
    public static long PhysicsFrames
    {
        [Pure] get => _physicsFrames;
    }

    /// <summary>
    /// 为 true 时，每次开始等待都会记录调用栈，<see cref="GetActiveWaits"/> 能告诉你“是谁在等”。
    /// 有性能开销，只建议在排查协程泄漏时打开。
    /// </summary>
    public static bool TrackStackTraces { get; set; }

    /// <summary>当前挂起中的游戏时间定时器数量（用于验证 Race/Scope 取消后零泄漏）。</summary>
    public static int ActiveTimerCount
    {
        [Pure] get => CountActive(TxWaitKind.Sleep);
    }

    /// <summary>当前挂起中的信号等待数量（用于验证取消后零泄漏）。</summary>
    public static int ActiveSignalWaitCount
    {
        [Pure] get => CountActive(TxWaitKind.Signal);
    }

    /// <summary>当前挂起中的所有等待（任意类型）的数量。</summary>
    public static int ActiveWaitCount
    {
        [Pure]
        get
        {
            int n = 0;
            foreach (var w in _waits)
            {
                if (!w.IsDone)
                    n++;
            }
            return n;
        }
    }

    private static int CountActive(TxWaitKind kind)
    {
        int n = 0;
        foreach (var w in _waits)
        {
            if (w.Kind == kind && !w.IsDone)
                n++;
        }
        return n;
    }

    /// <summary>
    /// 列出所有挂起中的等待（快照）。配合 <see cref="TrackStackTraces"/> 可以定位泄漏的协程是从哪里开始等的。
    /// </summary>
    [Pure]
    public static IReadOnlyList<TxWaitInfo> GetActiveWaits()
    {
        var result = new List<TxWaitInfo>();
        foreach (var w in _waits)
        {
            if (!w.IsDone)
                result.Add(new TxWaitInfo(w.Kind, w.Describe(), w.StartedAt, w.Stack));
        }
        return result;
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
    /// 手动推进一帧（视为未暂停）：游戏时间 + delta、处理帧数 + 1。
    /// 若已在 SceneTree 环境运行，系统会自动通过 ProcessFrame 驱动；同一帧内的重复调用会被去重。
    /// </summary>
    public static void Tick(double delta)
    {
        VtxThread.EnsureMainThread("TxTask.Tick");
        TickInternal(delta, paused: false);
    }

    /// <summary>手动推进一个物理帧（视为未暂停）。同一物理帧内的重复调用会被去重。</summary>
    public static void TickPhysics()
    {
        VtxThread.EnsureMainThread("TxTask.TickPhysics");
        TickPhysicsInternal(paused: false);
    }

    private static void TickInternal(double delta, bool paused)
    {
        ulong frame = Engine.GetProcessFrames();
        if (frame != 0 && frame == _lastTickFrame)
            return;
        _lastTickFrame = frame;

        if (!(delta > 0))
            delta = 0;

        if (!paused)
        {
            _gameTimeSeconds += delta;
            _gameFrames++;
        }

        for (int i = _waits.Count - 1; i >= 0; i--)
        {
            // 列表可能在回调中被修改（例如完成时同步登记了新的等待），先做边界检查
            if (i >= _waits.Count)
                continue;
            var w = _waits[i];
            if (w.IsDone)
            {
                _waits.RemoveAt(i);
                continue;
            }
            w.OnProcess(delta, paused);
            if (w.IsDone && i < _waits.Count && ReferenceEquals(_waits[i], w))
                _waits.RemoveAt(i);
        }

        PollLifetimes();
    }

    private static void TickPhysicsInternal(bool paused)
    {
        ulong frame = Engine.GetPhysicsFrames();
        if (frame != 0 && frame == _lastPhysicsFrame)
            return;
        _lastPhysicsFrame = frame;

        if (paused)
            return;
        _physicsFrames++;

        for (int i = _waits.Count - 1; i >= 0; i--)
        {
            if (i >= _waits.Count)
                continue;
            var w = _waits[i];
            if (!w.IsDone)
                w.OnPhysics(paused);
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
            tree.PhysicsFrame += () =>
            {
                bool paused = Engine.GetMainLoop() is SceneTree activeTree && activeTree.Paused;
                TickPhysicsInternal(paused);
            };
        }
    }

    private static void Register(WaitEntry entry)
    {
        // 登记表不是线程安全的：在后台线程（例如 ConfigureAwait(false) 之后）登记会破坏它，直接报错。
        VtxThread.EnsureMainThread("TxTask 的等待（Sleep / NextFrame / DelayFrames / WaitUntil）");
        entry.StartedAt = _gameTimeSeconds;
        if (TrackStackTraces)
            entry.Stack = new System.Diagnostics.StackTrace(2, true).ToString();
        _waits.Add(entry);
        EnsureAutoHook();
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

        var entry = new TimerWait { Kind = TxWaitKind.Sleep, RemainingSeconds = seconds, IgnorePause = ignorePause };
        Register(entry);
        if (ct.CanBeCanceled)
            entry.Registration = ct.Register(static s => ((TimerWait)s!).Tcs.TrySetCanceled(), entry);
        return entry.Tcs.Task;
    }

    /// <summary>等到下一帧（下一次未暂停的 ProcessFrame）。注意：Sleep(0) 会立即完成，不会等一帧。</summary>
    [Suspends]
    public static Task NextFrame(CancellationToken ct = default) => DelayFramesCore(1, physics: false, ct);

    /// <summary>等待指定数量的处理帧（暂停期间不计数）。frames 不是正数时立即完成。</summary>
    [Suspends]
    public static Task DelayFrames(int frames, CancellationToken ct = default) => DelayFramesCore(frames, physics: false, ct);

    /// <summary>等到下一个物理帧（下一次未暂停的 PhysicsFrame），适合在物理步进之后读取碰撞结果。</summary>
    [Suspends]
    public static Task NextPhysicsFrame(CancellationToken ct = default) => DelayFramesCore(1, physics: true, ct);

    /// <summary>等待指定数量的物理帧（暂停期间不计数）。frames 不是正数时立即完成。</summary>
    [Suspends]
    public static Task DelayPhysicsFrames(int frames, CancellationToken ct = default) => DelayFramesCore(frames, physics: true, ct);

    private static Task DelayFramesCore(int frames, bool physics, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled(ct);
        if (frames <= 0)
            return Task.CompletedTask;

        var entry = new FrameWait
        {
            Kind = physics ? TxWaitKind.PhysicsFrames : TxWaitKind.Frames,
            Physics = physics,
            // 以“当前计数 + n”为目标：即使在本帧的回调里登记，也至少要等到下一次计数。
            TargetFrame = (physics ? _physicsFrames : _gameFrames) + frames,
        };
        Register(entry);
        if (ct.CanBeCanceled)
            entry.Registration = ct.Register(static s => ((FrameWait)s!).Tcs.TrySetCanceled(), entry);
        return entry.Tcs.Task;
    }

    /// <summary>
    /// 每帧检查一次 condition，直到它返回 true（立即满足时同步完成）。暂停期间不检查。
    /// condition 应当只读、没有副作用；它抛出的异常会让等待以该异常失败。
    /// </summary>
    [Suspends]
    public static Task WaitUntil(Func<bool> condition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (ct.IsCancellationRequested)
            return Task.FromCanceled(ct);
        try
        {
            if (condition())
                return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }

        var entry = new UntilWait { Kind = TxWaitKind.Until, Condition = condition };
        Register(entry);
        if (ct.CanBeCanceled)
            entry.Registration = ct.Register(static s => ((UntilWait)s!).Tcs.TrySetCanceled(), entry);
        return entry.Tcs.Task;
    }

    /// <summary>
    /// 返回一个跟随节点生命周期的取消令牌：节点被 QueueFree（进入待删除状态）或被释放时自动取消。
    /// 同一个节点多次调用返回同一个令牌。只是移出场景树（例如重新挂到别的父节点下）不会取消。
    /// 典型用法：<c>await TxTask.Sleep(3, TxTask.LifetimeOf(this));</c> 或作为 Race / WithScope 的父令牌。
    /// 检测在主线程上进行：QueueFree 的节点在离开场景树时立即取消，直接 Free 的节点最迟在下一帧取消。
    /// </summary>
    public static CancellationToken LifetimeOf(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        VtxThread.EnsureMainThread("TxTask.LifetimeOf");
        if (!GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion())
            return new CancellationToken(canceled: true);

        if (_lifetimes.TryGetValue(node, out var existing))
            return existing.Token;

        var cts = new CancellationTokenSource();
        _lifetimes[node] = cts;
        node.TreeExiting += () =>
        {
            if (!GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion())
                EndLifetime(node);
        };
        EnsureAutoHook();
        return cts.Token;
    }

    private static void EndLifetime(Node node)
    {
        if (_lifetimes.Remove(node, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private static List<Node>? _endedScratch;

    private static void PollLifetimes()
    {
        if (_lifetimes.Count == 0)
            return;
        var ended = _endedScratch ??= new List<Node>();
        ended.Clear();
        foreach (var node in _lifetimes.Keys)
        {
            if (!GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion())
                ended.Add(node);
        }
        foreach (var node in ended)
            EndLifetime(node);
        ended.Clear();
    }

    /// <summary>
    /// “发出去不管”的任务：不等待它，但它抛出的异常（取消除外）会通过 GD.PushError 报告，而不是悄悄丢失。
    /// 用法：<c>PlayHitEffect(ct).Forget();</c>
    /// </summary>
    public static void Forget(this Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ObserveForgotten(task, "未等待的任务");
    }

    private static async void ObserveForgotten(Task task, string what)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            GD.PushError($"VTX: {what}抛出了未处理的异常: {ex}");
        }
    }

    private static Task<TResult> WaitSignalCore<TResult>(
        GodotObject source,
        StringName signalName,
        Func<TaskCompletionSource<TResult>, Action, Callable> makeCallable,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(signalName);
        VtxThread.EnsureMainThread("TxTask.WaitSignal");

        if (ct.IsCancellationRequested)
            return Task.FromCanceled<TResult>(ct);

        if (!GodotObject.IsInstanceValid(source))
            return Task.FromException<TResult>(new ObjectDisposedException(nameof(GodotObject), "VTX: WaitSignal 的信号源对象已被释放。"));

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

        Register(new SignalWait
        {
            Kind = TxWaitKind.Signal,
            Source = source,
            SignalName = signalName.ToString(),
            Done = () => tcs.Task.IsCompleted,
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
                ObserveForgotten(tasks[i], "Rush 后台分支");
        }

        bool completedNaturally = await winner;
        if (!completedNaturally)
        {
            throw new OperationCanceledException(parentCt);
        }
        return winnerIndex;
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
