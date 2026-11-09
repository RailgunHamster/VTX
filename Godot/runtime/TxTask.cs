#nullable enable
using System;
using System.Collections.Generic;
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
    private readonly List<Task> _branches = new();

    internal TxScope(CancellationToken parentCt)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
    }

    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// 在当前作用域下启动一个非阻塞子协程（对应 Verse 的 branch:）。
    /// 当外层 WithScope 结束时，若该子协程仍未完成，会自动收到取消信号并执行其 Defer/finally 清理。
    /// </summary>
    public void Branch(Func<CancellationToken, Task> branchTask)
    {
        ArgumentNullException.ThrowIfNull(branchTask);
        _branches.Add(TxTask.RunBranchSafeInternal(branchTask, _cts.Token));
    }

    internal async Task CancelAndDrainBranchesAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_branches.Count > 0)
        {
            try
            {
                await Task.WhenAll(_branches).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
        _cts.Dispose();
    }
}

/// <summary>
/// VTX 帧驱动结构化并发运行时：
/// 1. 基于游戏逻辑时间推进（Sleep）与可取消信号等待（WaitSignal）；
/// 2. Race（赢家胜出，输家立即取消）、Sync（等待全部）、Rush（首个完成即继续，其余后台继续跑）；
/// 3. WithScope + Branch（结构化分叉子任务，出作用域自动回收）；
/// 4. Defer（协程作用域退出或被取消时自动按 LIFO 逆序执行清理）。
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
        public TaskCompletionSource<bool> Tcs = null!;
        public CancellationTokenRegistration Registration;
    }

    private static readonly List<TimerEntry> _timers = new();
    private static double _gameTimeSeconds;
    private static bool _autoHooked;
    private static ulong _lastTickFrame = ulong.MaxValue;

    /// <summary>当前累计的逻辑游戏时间（秒）。</summary>
    public static double GameTimeSeconds
    {
        [Pure] get => _gameTimeSeconds;
    }

    /// <summary>当前挂起中的帧定时器数量（用于验证 Race/Scope 取消后零泄漏）。</summary>
    public static int ActiveTimerCount
    {
        [Pure] get => _timers.Count;
    }

    /// <summary>
    /// 注册协程退出或被取消时的自动清理动作（对应 Verse 的 defer:）。
    /// 用法：using var _ = TxTask.Defer(() => ResetState());
    /// </summary>
    [Pure]
    public static DeferGuard Defer(Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        return new DeferGuard(cleanup);
    }

    /// <summary>
    /// 推进所有按游戏时间挂起的任务。
    /// 若已在 SceneTree 环境运行，系统会自动通过 ProcessFrame 驱动；手动调用也会按帧号自动去重。
    /// </summary>
    public static void Tick(double delta)
    {
        if (delta <= 0)
            return;

        ulong frame = Engine.GetProcessFrames();
        if (frame != 0 && frame == _lastTickFrame)
            return;
        _lastTickFrame = frame;

        _gameTimeSeconds += delta;

        for (int i = _timers.Count - 1; i >= 0; i--)
        {
            var entry = _timers[i];
            if (entry.Tcs.Task.IsCompleted)
            {
                _timers.RemoveAt(i);
                continue;
            }

            entry.RemainingSeconds -= delta;
            if (entry.RemainingSeconds <= 0)
            {
                _timers.RemoveAt(i);
                entry.Registration.Dispose();
                entry.Tcs.TrySetResult(true);
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
                    Tick(root.GetProcessDeltaTime());
                }
                else
                {
                    Tick(1.0 / 60.0);
                }
            };
        }
    }

    /// <summary>
    /// 按游戏帧时间挂起指定秒数（受暂停、慢放控制），若被 Race 或父作用域取消则立即中断并清理。
    /// </summary>
    [Suspends]
    public static Task Sleep(double seconds, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled(ct);

        if (seconds <= 0)
            return Task.CompletedTask;

        EnsureAutoHook();

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new TimerEntry
        {
            RemainingSeconds = seconds,
            Tcs = tcs
        };

        if (ct.CanBeCanceled)
        {
            entry.Registration = ct.Register(() =>
            {
                _timers.Remove(entry);
                tcs.TrySetCanceled(ct);
            });
        }

        _timers.Add(entry);
        return tcs.Task;
    }

    /// <summary>
    /// 可取消的信号等待（替代 Godot 原生不可取消的 ToSignal）。
    /// 当 ct 取消（例如在 Race 中落败）时，立即从 GodotObject 退订信号回调，杜绝协程泄漏。
    /// </summary>
    [Suspends]
    public static Task WaitSignal(GodotObject source, StringName signalName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(signalName);

        if (ct.IsCancellationRequested)
            return Task.FromCanceled(ct);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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

        callable = Callable.From(() =>
        {
            DisconnectSafe();
            tcs.TrySetResult(true);
        });

        source.Connect(signalName, callable);

        if (ct.CanBeCanceled)
        {
            reg = ct.Register(() =>
            {
                DisconnectSafe();
                tcs.TrySetCanceled(ct);
            });
        }

        return tcs.Task;
    }

    /// <summary>
    /// 带 1 个强类型参数返回值的可取消信号等待（对应 Verse 的 Event.Await()）：
    /// 当信号触发时，直接将信号携带的第 1 个参数作为 Task&lt;T0&gt; 返回值解包返回；
    /// 若被 Race 或父作用域取消，自动从 GodotObject 退订信号。
    /// </summary>
    [Suspends]
    public static Task<T0> WaitSignal<[MustBeVariant] T0>(
        GodotObject source,
        StringName signalName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(signalName);

        if (ct.IsCancellationRequested)
            return Task.FromCanceled<T0>(ct);

        var tcs = new TaskCompletionSource<T0>(TaskCreationOptions.RunContinuationsAsynchronously);
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

        callable = Callable.From<T0>(arg0 =>
        {
            DisconnectSafe();
            tcs.TrySetResult(arg0);
        });

        source.Connect(signalName, callable);

        if (ct.CanBeCanceled)
        {
            reg = ct.Register(() =>
            {
                DisconnectSafe();
                tcs.TrySetCanceled(ct);
            });
        }

        return tcs.Task;
    }

    /// <summary>
    /// 带 2 个强类型参数返回值的可取消信号等待：信号触发时直接返回元组 (T0, T1)。
    /// </summary>
    [Suspends]
    public static Task<(T0, T1)> WaitSignal<[MustBeVariant] T0, [MustBeVariant] T1>(
        GodotObject source,
        StringName signalName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(signalName);

        if (ct.IsCancellationRequested)
            return Task.FromCanceled<(T0, T1)>(ct);

        var tcs = new TaskCompletionSource<(T0, T1)>(TaskCreationOptions.RunContinuationsAsynchronously);
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

        callable = Callable.From<T0, T1>((arg0, arg1) =>
        {
            DisconnectSafe();
            tcs.TrySetResult((arg0, arg1));
        });

        source.Connect(signalName, callable);

        if (ct.CanBeCanceled)
        {
            reg = ct.Register(() =>
            {
                DisconnectSafe();
                tcs.TrySetCanceled(ct);
            });
        }

        return tcs.Task;
    }

    /// <summary>
    /// 带 3 个强类型参数返回值的可取消信号等待：信号触发时直接返回元组 (T0, T1, T2)。
    /// </summary>
    [Suspends]
    public static Task<(T0, T1, T2)> WaitSignal<[MustBeVariant] T0, [MustBeVariant] T1, [MustBeVariant] T2>(
        GodotObject source,
        StringName signalName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(signalName);

        if (ct.IsCancellationRequested)
            return Task.FromCanceled<(T0, T1, T2)>(ct);

        var tcs = new TaskCompletionSource<(T0, T1, T2)>(TaskCreationOptions.RunContinuationsAsynchronously);
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

        callable = Callable.From<T0, T1, T2>((arg0, arg1, arg2) =>
        {
            DisconnectSafe();
            tcs.TrySetResult((arg0, arg1, arg2));
        });

        source.Connect(signalName, callable);

        if (ct.CanBeCanceled)
        {
            reg = ct.Register(() =>
            {
                DisconnectSafe();
                tcs.TrySetCanceled(ct);
            });
        }

        return tcs.Task;
    }

    /// <summary>
    /// 结构化并发 Race：所有分支同时启动，第一个完成的分支获胜，
    /// 其余落败分支立即收到 CancellationToken 取消信号并等待其 Defer/finally 块清理完毕后返回获胜分支下标。
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

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        var ct = linkedCts.Token;

        var tasks = new Task[branches.Length];
        for (int i = 0; i < branches.Length; i++)
        {
            tasks[i] = RunBranchSafeInternal(branches[i], ct);
        }

        Task winner = await Task.WhenAny(tasks).ConfigureAwait(false);
        int winnerIndex = System.Array.IndexOf(tasks, winner);

        // Cancel all losing branches immediately
        await linkedCts.CancelAsync().ConfigureAwait(false);

        // Wait for all losing branches to run their finally blocks cleanly
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected for losing branches
        }

        // Propagate exception if the winning branch faulted
        await winner.ConfigureAwait(false);
        return winnerIndex;
    }

    /// <summary>
    /// 结构化并发 Sync：等待所有分支全部完成；若任意分支异常或取消，自动取消其余同级分支。
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

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        var ct = linkedCts.Token;

        var tasks = new Task[branches.Length];
        for (int i = 0; i < branches.Length; i++)
        {
            tasks[i] = branches[i](ct);
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 结构化并发 Rush（对应 Verse 的 rush:）：
    /// 所有分支同时启动；当第一个分支完成时，Rush 立即返回该获胜分支下标，主流程继续往下走；
    /// 其余未完成的分支继续在后台运行（不会被立即取消），直到它们自然完成或外层 parentCt 被取消。
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

        var tasks = new Task[branches.Length];
        for (int i = 0; i < branches.Length; i++)
        {
            tasks[i] = RunBranchSafeInternal(branches[i], parentCt);
        }

        Task winner = await Task.WhenAny(tasks).ConfigureAwait(false);
        int winnerIndex = System.Array.IndexOf(tasks, winner);
        await winner.ConfigureAwait(false);
        return winnerIndex;
    }

    /// <summary>
    /// 开启一个结构化并发作用域（支持在内部调用 scope.Branch(...) 启动伴随子任务）。
    /// 当主逻辑退出该作用域时，所有由 scope.Branch 启动但尚未结束的子任务会自动被取消并等待清理完成。
    /// </summary>
    [Suspends]
    public static async Task WithScope(Func<TxScope, Task> body, CancellationToken parentCt = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var scope = new TxScope(parentCt);
        try
        {
            await body(scope).ConfigureAwait(false);
        }
        finally
        {
            await scope.CancelAndDrainBranchesAsync().ConfigureAwait(false);
        }
    }

    internal static async Task RunBranchSafeInternal(Func<CancellationToken, Task> branch, CancellationToken ct)
    {
        try
        {
            await branch(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Clean structured cancellation
        }
    }
}
