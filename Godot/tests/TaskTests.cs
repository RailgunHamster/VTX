#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace VTX.Tests;

internal static class TaskTests
{
    private static void Start()
    {
        GodotObject.MainThreadId = Environment.CurrentManagedThreadId;
        lock (GodotObject.OffThreadCalls) GodotObject.OffThreadCalls.Clear();
        Engine.Tree!.Paused = false;
        GD.Errors.Clear();
    }

    private static void NoOffThreadGodotCalls()
    {
        lock (GodotObject.OffThreadCalls)
            T.Eq("", string.Join(",", GodotObject.OffThreadCalls), "所有 Godot API 调用都在主线程");
    }

    private static Task Run(Func<Task> test, int maxFrames = 600) => MainThread.RunFrames(test, maxFrames);

    private static void Completed(Task t, string what)
    {
        T.True(t.IsCompleted, $"{what} 在帧预算内完成（状态 {t.Status}）");
        if (t.IsFaulted) T.Fail($"{what} 失败: {t.Exception!.InnerException}");
    }

    public static void Register()
    {
        T.Add("TxTask.Sleep: 按游戏时间完成；非正数/NaN 立即完成", () =>
        {
            Start();
            double start = TxTask.GameTimeSeconds;
            double end = 0;
            var t = Run(async () => { await TxTask.Sleep(0.5); end = TxTask.GameTimeSeconds; });
            Completed(t, "Sleep");
            T.True(end - start >= 0.5 && end - start < 0.6, $"耗时 {end - start:F3}s");
            T.True(TxTask.Sleep(0).IsCompleted && TxTask.Sleep(-1).IsCompleted && TxTask.Sleep(double.NaN).IsCompleted, "立即完成");
        });

        T.Add("TxTask.Sleep: SceneTree 暂停时不计时，SleepIgnoringPause 照常计时", () =>
        {
            Start();
            bool normalDone = false, unpausedDone = false;
            var t = Run(async () =>
            {
                Engine.Tree!.Paused = true;
                var a = TxTask.Sleep(0.2).ContinueWith(_ => normalDone = true, TaskScheduler.FromCurrentSynchronizationContext());
                var b = TxTask.SleepIgnoringPause(0.2).ContinueWith(_ => unpausedDone = true, TaskScheduler.FromCurrentSynchronizationContext());
                await b;
                T.True(!normalDone, "暂停期间普通 Sleep 未完成");
                Engine.Tree!.Paused = false;
                await a;
            });
            Completed(t, "暂停测试");
            T.True(normalDone && unpausedDone, "两者最终都完成");
        });

        T.Add("TxTask.Race: 取消与清理都在主线程（回归 #2）", () =>
        {
            Start();
            var src = new GodotObject();
            var loserCallbackThreads = new List<int>();
            int winner = -1;
            var t = Run(async () =>
            {
                winner = await TxTask.Race(
                    ct => TxTask.Sleep(0.1, ct),
                    ct => { ct.Register(() => loserCallbackThreads.Add(Environment.CurrentManagedThreadId)); return TxTask.Sleep(10, ct); },
                    ct => TxTask.WaitSignal(src, "never", ct));
                T.Eq(GodotObject.MainThreadId, Environment.CurrentManagedThreadId, "Race 之后的代码在主线程");
            });
            Completed(t, "Race");
            T.Eq(0, winner, "胜者下标");
            T.Eq(GodotObject.MainThreadId.ToString(), string.Join(",", loserCallbackThreads), "落败分支的取消回调在主线程");
            T.Eq(0, src.ConnectionCount("never"), "信号已退订");
            T.Eq(0, TxTask.ActiveTimerCount, "无残留定时器");
            T.Eq(0, TxTask.ActiveSignalWaitCount, "无残留信号等待");
            NoOffThreadGodotCalls();
        });

        T.Add("TxTask.Race: 父令牌取消时抛 OperationCanceledException（回归 #4）", () =>
        {
            Start();
            var parent = new CancellationTokenSource();
            Exception? caught = null;
            var t = Run(async () =>
            {
                var race = TxTask.Race(parent.Token, ct => TxTask.Sleep(5, ct), ct => TxTask.Sleep(6, ct));
                await TxTask.Sleep(0.1);
                parent.Cancel();
                try { await race; } catch (Exception ex) { caught = ex; }
            });
            Completed(t, "Race");
            T.True(caught is OperationCanceledException, $"抛出取消异常（实际 {caught?.GetType().Name ?? "无"}）");
            T.Throws<OperationCanceledException>(() => TxTask.Race(new CancellationToken(true), ct => Task.CompletedTask).GetAwaiter().GetResult(), "已取消的令牌");
        });

        T.Add("TxTask.Race: 胜者异常原样抛出，同步抛异常的分支不会打断启动", () =>
        {
            Start();
            Exception? caught = null;
            bool otherCancelled = false;
            var t = Run(async () =>
            {
                try
                {
                    await TxTask.Race(
                        ct => throw new InvalidOperationException("sync boom"),
                        async ct => { try { await TxTask.Sleep(5, ct); } finally { otherCancelled = ct.IsCancellationRequested; } });
                }
                catch (Exception ex) { caught = ex; }
            });
            Completed(t, "Race");
            T.True(caught is InvalidOperationException, $"胜者异常（实际 {caught?.GetType().Name ?? "无"}）");
            T.True(otherCancelled, "另一个分支已取消并清理");
        });

        T.Add("TxTask.Sync: 一个分支失败时立即取消其余分支（回归 #4）", () =>
        {
            Start();
            double start = TxTask.GameTimeSeconds, failedAt = 0;
            Exception? caught = null;
            bool siblingCleaned = false;
            var t = Run(async () =>
            {
                try
                {
                    await TxTask.Sync(
                        async ct => { await TxTask.Sleep(0.1, ct); throw new InvalidOperationException("fail"); },
                        async ct => { try { await TxTask.Sleep(3, ct); } finally { siblingCleaned = true; } });
                }
                catch (Exception ex) { caught = ex; failedAt = TxTask.GameTimeSeconds; }
            });
            Completed(t, "Sync");
            T.True(caught is InvalidOperationException, $"抛出原始异常（实际 {caught?.GetType().Name ?? "无"}）");
            T.True(failedAt - start < 0.5, $"在 {failedAt - start:F2}s 时就结束了");
            T.True(siblingCleaned, "兄弟分支已清理");
            NoOffThreadGodotCalls();
        });

        T.Add("TxTask.Sync: 全部完成；父令牌取消时抛取消异常", () =>
        {
            Start();
            int done = 0;
            var t = Run(async () => await TxTask.Sync(async ct => { await TxTask.Sleep(0.1, ct); done++; }, async ct => { await TxTask.Sleep(0.2, ct); done++; }));
            Completed(t, "Sync");
            T.Eq(2, done, "两个分支都完成");
            var parent = new CancellationTokenSource();
            Exception? caught = null;
            t = Run(async () =>
            {
                var s = TxTask.Sync(parent.Token, ct => TxTask.Sleep(5, ct));
                await TxTask.Sleep(0.05); parent.Cancel();
                try { await s; } catch (Exception ex) { caught = ex; }
            });
            Completed(t, "Sync cancel");
            T.True(caught is OperationCanceledException, $"取消异常（实际 {caught?.GetType().Name ?? "无"}）");
        });

        T.Add("TxTask.Rush: 先完成者返回，其余后台继续；后台异常会被报告", () =>
        {
            Start();
            bool slowFinished = false;
            int w = -1;
            var t = Run(async () =>
            {
                w = await TxTask.Rush(
                    ct => TxTask.Sleep(0.05, ct),
                    async ct => { await TxTask.Sleep(0.1, ct); slowFinished = true; },
                    async ct => { await TxTask.Sleep(0.1, ct); throw new InvalidOperationException("bg"); });
                T.True(!slowFinished, "Rush 返回时慢分支未完成");
                await TxTask.Sleep(0.2);
            });
            Completed(t, "Rush");
            T.Eq(0, w, "胜者");
            T.True(slowFinished, "慢分支在后台完成");
            T.True(GD.Errors.Exists(e => e.Contains("bg")), "后台异常已报告");
        });

        T.Add("TxTask.WithScope: 出作用域取消 Branch；主体异常优先；Branch 异常会抛出", () =>
        {
            Start();
            bool cleaned = false;
            var t = Run(async () =>
            {
                await TxTask.WithScope(async scope =>
                {
                    scope.Branch(async ct => { using var _ = TxTask.Defer(() => cleaned = true); await TxTask.Sleep(10, ct); });
                    await TxTask.Sleep(0.05);
                });
            });
            Completed(t, "WithScope");
            T.True(cleaned, "Branch 已清理");
            T.Eq(0, TxTask.ActiveTimerCount, "无残留定时器");

            Exception? caught = null;
            t = Run(async () =>
            {
                try
                {
                    await TxTask.WithScope(async scope =>
                    {
                        scope.Branch(async ct => { try { await TxTask.Sleep(10, ct); } finally { throw new ArgumentException("branch"); } });
                        await TxTask.Sleep(0.05);
                        throw new InvalidOperationException("body");
                    });
                }
                catch (Exception ex) { caught = ex; }
            });
            Completed(t, "WithScope body ex");
            T.True(caught is InvalidOperationException, $"主体异常优先（实际 {caught?.GetType().Name}）");

            caught = null;
            TxScope? leaked = null;
            t = Run(async () =>
            {
                try
                {
                    await TxTask.WithScope(async scope =>
                    {
                        leaked = scope;
                        scope.Branch(async ct => { await TxTask.Sleep(0.01, ct); throw new ArgumentException("branch"); });
                        await TxTask.Sleep(0.1);
                    });
                }
                catch (Exception ex) { caught = ex; }
            });
            Completed(t, "WithScope branch ex");
            T.True(caught is ArgumentException, $"Branch 异常（实际 {caught?.GetType().Name ?? "无"}）");
            T.Throws<InvalidOperationException>(() => leaked!.Branch(_ => Task.CompletedTask), "结束后不能再 Branch");

            int tracked = -1;
            t = Run(async () =>
            {
                await TxTask.WithScope(async scope =>
                {
                    for (int i = 0; i < 50; i++)
                    {
                        scope.Branch(ct => TxTask.Sleep(0.01, ct));
                        await TxTask.Sleep(0.02);
                    }
                    tracked = scope.TrackedBranchCount;
                });
            }, maxFrames: 2000);
            Completed(t, "长生命周期作用域");
            T.True(tracked >= 0 && tracked <= 2, $"已完成的 Branch 不再被跟踪（仍跟踪 {tracked} 个）");
        });

        T.Add("TxTask.WaitSignal: 强类型解包、取消退订（含跨线程取消）、信号源释放", () =>
        {
            Start();
            var src = new GodotObject();
            (string, int) got = default;
            var t = Run(async () =>
            {
                var w = TxTask.WaitSignal<string, int>(src, "loot", default);
                src.TestEmit("loot", "gem", 3);
                got = await w;
            });
            Completed(t, "WaitSignal<T0,T1>");
            T.Eq(("gem", 3), got, "参数解包");
            T.Eq(0, src.ConnectionCount("loot"), "触发后自动退订");

            var cts = new CancellationTokenSource();
            Exception? caught = null;
            t = Run(async () =>
            {
                var w = TxTask.WaitSignal(src, "never", cts.Token);
                await TxTask.Sleep(0.05);
                await Task.Run(() => cts.Cancel()); // 从线程池取消
                try { await w; } catch (Exception ex) { caught = ex; }
                await TxTask.Sleep(0.05);
            });
            Completed(t, "跨线程取消");
            T.True(caught is OperationCanceledException, "取消");
            T.Eq(0, src.ConnectionCount("never"), "已退订");
            NoOffThreadGodotCalls();

            var doomed = new GodotObject();
            caught = null;
            t = Run(async () =>
            {
                var w = TxTask.WaitSignal(doomed, "never");
                await TxTask.Sleep(0.05);
                doomed.Free();
                try { await w; } catch (Exception ex) { caught = ex; }
            }, maxFrames: 100);
            Completed(t, "信号源释放");
            T.True(caught is ObjectDisposedException, $"ObjectDisposedException（实际 {caught?.GetType().Name ?? "无"}）");
            T.Eq(0, TxTask.ActiveSignalWaitCount, "无残留等待");
        });

        T.Add("TxTask.Sleep: 跨线程取消安全，定时器被清理", () =>
        {
            Start();
            Exception? caught = null;
            var t = Run(async () =>
            {
                var cts = new CancellationTokenSource();
                cts.CancelAfter(20);
                try { await TxTask.Sleep(100, cts.Token); } catch (Exception ex) { caught = ex; }
                await TxTask.Sleep(0.02);
            });
            Completed(t, "CancelAfter");
            T.True(caught is OperationCanceledException, "取消");
            T.Eq(0, TxTask.ActiveTimerCount, "定时器已清理");
            NoOffThreadGodotCalls();
        });
    }
}
