#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace VTX;

/// <summary>
/// VTX 帧驱动结构化并发运行时（基于游戏逻辑时间推进 + Race/Sync 自动取消清理）。
/// 支持通过 SceneTree.ProcessFrame 自动挂载帧驱动，无需手动在 _Process 中调用 Tick。
/// </summary>
public static class TxTask
{
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

    /// <summary>当前挂起中的帧定时器数量（用于验证 Race 取消后零泄漏）。</summary>
    public static int ActiveTimerCount
    {
        [Pure] get => _timers.Count;
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
    /// 结构化并发 Race：所有分支同时启动，第一个完成的分支获胜，
    /// 其余落败分支立即收到 CancellationToken 取消信号并等待其 finally 块清理完毕后返回获胜分支下标。
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
            tasks[i] = RunBranchSafe(branches[i], ct);
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

    private static async Task RunBranchSafe(Func<CancellationToken, Task> branch, CancellationToken ct)
    {
        try
        {
            await branch(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Clean structured cancellation of a losing branch
        }
    }
}
