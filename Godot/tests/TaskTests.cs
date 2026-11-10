#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
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
        T.Add("TxTask.NextFrame / DelayFrames / NextPhysicsFrame：按帧推进，暂停时不计数", () =>
        {
            Start();
            long f0 = 0, f1 = 0, f3 = 0, p0 = 0, p1 = 0;
            var t = Run(async () =>
            {
                f0 = TxTask.GameFrames;
                await TxTask.NextFrame();
                f1 = TxTask.GameFrames;
                await TxTask.DelayFrames(3);
                f3 = TxTask.GameFrames;
                p0 = TxTask.PhysicsFrames;
                await TxTask.NextPhysicsFrame();
                p1 = TxTask.PhysicsFrames;
            });
            Completed(t, "帧等待");
            T.Eq(1L, f1 - f0, "NextFrame 恰好一帧");
            T.Eq(3L, f3 - f1, "DelayFrames(3) 恰好三帧");
            T.True(p1 > p0, "NextPhysicsFrame 等到了物理帧");
            T.True(TxTask.DelayFrames(0).IsCompleted, "0 帧立即完成");

            bool done = false;
            t = Run(async () =>
            {
                Engine.Tree!.Paused = true;
                var w = TxTask.DelayFrames(2).ContinueWith(_ => done = true, TaskScheduler.FromCurrentSynchronizationContext());
                await TxTask.SleepIgnoringPause(0.2);
                T.True(!done, "暂停期间帧不计数");
                Engine.Tree!.Paused = false;
                await w;
            });
            Completed(t, "暂停帧");
            T.True(done, "恢复后完成");
        });

        T.Add("TxTask.WaitUntil：立即满足同步完成；每帧检查；条件抛异常则失败；可取消", () =>
        {
            Start();
            T.True(TxTask.WaitUntil(() => true).IsCompleted, "立即满足");
            int counter = 0;
            long startFrame = 0, endFrame = 0;
            var t = Run(async () =>
            {
                startFrame = TxTask.GameFrames;
                await TxTask.WaitUntil(() => ++counter >= 5);
                endFrame = TxTask.GameFrames;
            });
            Completed(t, "WaitUntil");
            T.Eq(5, counter, "检查了 5 次（第一次是同步检查）");
            T.Eq(4L, endFrame - startFrame, "之后每帧检查一次");

            Exception? caught = null;
            t = Run(async () =>
            {
                try { await TxTask.WaitUntil(() => throw new InvalidOperationException("bad")); } catch (Exception ex) { caught = ex; }
                int n = 0;
                try { await TxTask.WaitUntil(() => { n++; return n > 1 && n < 0; }, new CancellationTokenSource(30).Token); } catch (Exception ex2) { caught = ex2 is OperationCanceledException ? ex2 : caught; }
            });
            Completed(t, "WaitUntil 异常/取消");
            T.True(caught is OperationCanceledException, $"可取消（实际 {caught?.GetType().Name}）");
            T.Eq(0, TxTask.ActiveWaitCount, "无残留等待");
        });

        T.Add("TxTask.LifetimeOf：QueueFree / Free 时取消，重新挂载不取消，同一节点同一令牌", () =>
        {
            Start();
            var node = new Node();
            var token = TxTask.LifetimeOf(node);
            T.True(token.Equals(TxTask.LifetimeOf(node)), "同一节点返回同一令牌");
            node.TestExitTree();
            T.True(!token.IsCancellationRequested, "只是离开场景树不取消");

            bool cleaned = false;
            Exception? caught = null;
            var t = Run(async () =>
            {
                var loop = (Func<Task>)(async () =>
                {
                    using var _ = TxTask.Defer(() => cleaned = true);
                    await TxTask.Sleep(100, token);
                });
                var running = loop();
                await TxTask.NextFrame();
                node.QueueFree();
                T.True(!token.IsCancellationRequested, "QueueFree 当帧还没轮询");
                node.TestDeleteQueued(); // 帧末真正删除时会先发出 tree_exiting
                T.True(token.IsCancellationRequested, "删除时立即取消");
                try { await running; } catch (Exception ex) { caught = ex; }
            });
            Completed(t, "LifetimeOf");
            T.True(cleaned, "协程的 Defer 已执行");
            T.True(caught is OperationCanceledException, "协程以取消结束");

            var other = new Node();
            var token2 = TxTask.LifetimeOf(other);
            t = Run(async () =>
            {
                other.Free();
                await TxTask.NextFrame();
                await TxTask.NextFrame();
            });
            Completed(t, "Free");
            T.True(token2.IsCancellationRequested, "直接 Free 的节点下一帧取消");
            T.True(TxTask.LifetimeOf(other).IsCancellationRequested, "已释放节点返回已取消令牌");
        });

        T.Add("TxTask.Forget：发出去不管的任务异常会被报告，取消不会", () =>
        {
            Start();
            var t = Run(async () =>
            {
                Fail().Forget();
                Cancelled().Forget();
                await TxTask.NextFrame();
                await TxTask.NextFrame();
            });
            Completed(t, "Forget");
            T.True(GD.Errors.Exists(e => e.Contains("forgotten boom")), "异常已报告");
            T.Eq(1, GD.Errors.Count, "取消不报告");

            static async Task Fail() { await TxTask.NextFrame(); throw new InvalidOperationException("forgotten boom"); }
            static async Task Cancelled() { await TxTask.NextFrame(); throw new OperationCanceledException(); }
        });

        T.Add("TxTask.GetActiveWaits：列出挂起中的等待并可记录调用栈", () =>
        {
            Start();
            TxTask.TrackStackTraces = true;
            try
            {
                var cts = new CancellationTokenSource();
                IReadOnlyList<TxWaitInfo>? snapshot = null;
                var t = Run(async () =>
                {
                    var a = TxTask.Sleep(5, cts.Token);
                    var b = TxTask.DelayFrames(100, cts.Token);
                    var c = TxTask.WaitUntil(() => false, cts.Token);
                    var d = TxTask.WaitSignal(new GodotObject(), "never", cts.Token);
                    await TxTask.NextFrame();
                    snapshot = TxTask.GetActiveWaits();
                    cts.Cancel();
                    try { await Task.WhenAll(a, b, c, d); } catch (OperationCanceledException) { }
                    await TxTask.NextFrame();
                });
                Completed(t, "追踪");
                T.Eq("Sleep,Frames,Until,Signal", string.Join(",", snapshot!.Select(w => w.Kind)), "四种等待");
                T.True(snapshot!.All(w => w.StackTrace != null && w.StackTrace.Contains("TaskTests")), "记录了调用栈");
                T.True(snapshot![0].Detail.Contains("秒"), $"说明文字: {snapshot[0].Detail}");
                T.Eq(0, TxTask.ActiveWaitCount, "取消后全部清理");
            }
            finally
            {
                TxTask.TrackStackTraces = false;
            }
        });

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

        T.Add("TxTask: 在后台线程登记等待会直接报错（而不是破坏登记表）", () =>
        {
            Start();
            Exception? sleepEx = null, lifeEx = null;
            var th = new Thread(() =>
            {
                try { _ = TxTask.Sleep(1); } catch (Exception e) { sleepEx = e; }
                try { TxTask.LifetimeOf(new Node()); } catch (Exception e) { lifeEx = e; }
            });
            th.Start(); th.Join();
            T.True(sleepEx is InvalidOperationException && sleepEx.Message.Contains("主线程"), "Sleep");
            T.True(lifeEx is InvalidOperationException, "LifetimeOf");
            T.Eq(0, TxTask.ActiveWaitCount, "登记表没有被改动");
            // 立即完成的等待不需要登记，任何线程都可以
            Exception? zeroEx = null;
            var th2 = new Thread(() => { try { TxTask.Sleep(0).Wait(); } catch (Exception e) { zeroEx = e; } });
            th2.Start(); th2.Join();
            T.True(zeroEx is null, "Sleep(0)");
        });

        T.Add("TxTask.SwitchToMainThread：从后台线程回到主线程", () =>
        {
            Start();
            int mainId = Environment.CurrentManagedThreadId;
            int before = -1, after = -1;
            var t = MainThread.RunFrames(async () =>
            {
                await Task.Run(() => { }).ConfigureAwait(false);
                before = Environment.CurrentManagedThreadId;
                await TxTask.SwitchToMainThread();
                after = Environment.CurrentManagedThreadId;
                await TxTask.SwitchToMainThread(); // 已在主线程：立即继续
            }, 50);
            T.True(t.IsCompletedSuccessfully, "完成");
            T.True(before != mainId, "ConfigureAwait(false) 之后在线程池");
            T.Eq(mainId, after, "回到主线程");
        });
    }
}
