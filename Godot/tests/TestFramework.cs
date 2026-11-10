#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace VTX.Tests;

/// <summary>极简测试框架（不依赖 NuGet 包，方便在任意环境 dotnet run）。</summary>
public static class T
{
    private static readonly List<(string Name, Action Body)> _tests = new();
    public static int Failed;
    public static int Passed;
    private static string _current = "";

    public static void Add(string name, Action body) => _tests.Add((name, body));

    public static void RunAll(string? filter)
    {
        foreach (var (name, body) in _tests)
        {
            if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            _current = name;
            int before = Failed;
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Fail($"未预期的异常: {ex.GetType().Name}: {ex.Message}\n        {ex.StackTrace?.Split('\n')[0].Trim()}");
            }
            if (Failed == before)
            {
                Passed++;
                Console.WriteLine($"  PASS  {name}");
            }
        }
    }

    public static void Fail(string message)
    {
        Failed++;
        Console.WriteLine($"  FAIL  {_current}\n        {message}");
    }

    public static void True(bool cond, string what, [CallerArgumentExpression(nameof(cond))] string? expr = null)
    {
        if (!cond) Fail($"{what}: 期望 ({expr}) 为 true");
    }

    public static void Eq<TV>(TV expected, TV actual, string what)
    {
        if (!EqualityComparer<TV>.Default.Equals(expected, actual))
            Fail($"{what}: 期望 <{expected}>，实际 <{actual}>");
    }

    public static TEx Throws<TEx>(Action a, string what) where TEx : Exception
    {
        try
        {
            a();
        }
        catch (TEx ex)
        {
            return ex;
        }
        catch (Exception ex)
        {
            Fail($"{what}: 期望抛出 {typeof(TEx).Name}，实际抛出 {ex.GetType().FullName}: {ex.Message}");
            return null!;
        }
        Fail($"{what}: 期望抛出 {typeof(TEx).Name}，实际没有抛出");
        return null!;
    }
}

/// <summary>模拟 Godot 主线程：单线程 SynchronizationContext + 按帧推进 SceneTree。</summary>
public sealed class MainThread : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback, object?)> _queue = new();
    public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));
    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    public void Pump()
    {
        while (_queue.TryDequeue(out var item))
            item.Item1(item.Item2);
    }

    public static readonly MainThread Instance = new();

    /// <summary>在模拟主线程上运行一个异步测试，每帧：处理投递的续体 → 推进 SceneTree 一帧 → 再处理续体。</summary>
    public static Task RunFrames(Func<Task> test, int maxFrames, Action? perFrame = null)
    {
        var prev = Current;
        SetSynchronizationContext(Instance);
        try
        {
            Task t = test();
            for (int i = 0; i < maxFrames && !t.IsCompleted; i++)
            {
                Instance.Pump();
                perFrame?.Invoke();
                Godot.Engine.Tree!.TestStep();
                Instance.Pump();
                // 给线程池上的续体（如果有）一点时间暴露出来
                if (!t.IsCompleted) Thread.Sleep(1);
            }
            Instance.Pump();
            return t;
        }
        finally
        {
            SetSynchronizationContext(prev);
        }
    }
}
