#nullable enable
// 测试专用：Godot API 的最小桩实现。只覆盖 VTX 运行时与测试用例用到的成员，签名与 GodotSharp 保持一致。
// 不会被编进 GodotSharp.dll（GodotSharp.csproj 只包含 runtime/*.cs）。
using System;
using System.Collections.Generic;

namespace Godot
{
    public enum Error { Ok = 0, Failed = 1 }

    [AttributeUsage(AttributeTargets.GenericParameter)]
    public sealed class MustBeVariantAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Delegate)]
    public sealed class SignalAttribute : Attribute { }

    public sealed class StringName : IEquatable<StringName>
    {
        public readonly string Name;
        public StringName(string name) => Name = name;
        public static implicit operator StringName(string s) => new(s);
        public bool Equals(StringName? other) => other is not null && other.Name == Name;
        public override bool Equals(object? obj) => obj is StringName s && Equals(s);
        public override int GetHashCode() => Name.GetHashCode();
        public override string ToString() => Name;
    }

    public readonly struct Variant
    {
        public readonly object? Obj;
        public Variant(object? o) => Obj = o;
        public static implicit operator Variant(int v) => new(v);
        public static implicit operator Variant(string v) => new(v);
        public static implicit operator Variant(bool v) => new(v);
        public static implicit operator Variant(float v) => new(v);
    }

    public readonly struct Callable
    {
        public readonly Delegate? Delegate;
        private Callable(Delegate d) => Delegate = d;
        public static Callable From(Action action) => new(action);
        public static Callable From<[MustBeVariant] T0>(Action<T0> action) => new(action);
        public static Callable From<[MustBeVariant] T0, [MustBeVariant] T1>(Action<T0, T1> action) => new(action);
        public static Callable From<[MustBeVariant] T0, [MustBeVariant] T1, [MustBeVariant] T2>(Action<T0, T1, T2> action) => new(action);
        public void CallDeferred(params Variant[] args) { }
    }

    public static class WorkerThreadPool
    {
        public static long AddTask(Callable action, bool highPriority = false, string description = "") => 0;
    }

    public struct Vector2 : IEquatable<Vector2>
    {
        public float X, Y;
        public Vector2(float x, float y) { X = x; Y = y; }
        public bool Equals(Vector2 o) => X == o.X && Y == o.Y;
        public override bool Equals(object? obj) => obj is Vector2 v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(X, Y);
        public static bool operator ==(Vector2 a, Vector2 b) => a.Equals(b);
        public static bool operator !=(Vector2 a, Vector2 b) => !a.Equals(b);
        public Vector2 Normalized() => this;
    }

    public struct Vector3 : IEquatable<Vector3>
    {
        public float X, Y, Z;
        public Vector3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public bool Equals(Vector3 o) => X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object? obj) => obj is Vector3 v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public static bool operator ==(Vector3 a, Vector3 b) => a.Equals(b);
        public static bool operator !=(Vector3 a, Vector3 b) => !a.Equals(b);
    }

    public class GodotObject
    {
        // ---- 测试辅助 ----
        public static int MainThreadId = -1;
        public static readonly List<string> OffThreadCalls = new();
        protected static void AssertMainThread(string api)
        {
            if (MainThreadId >= 0 && Environment.CurrentManagedThreadId != MainThreadId)
            {
                lock (OffThreadCalls) OffThreadCalls.Add(api);
            }
        }
        private bool _freed;
        private readonly Dictionary<string, List<Callable>> _connections = new();
        private readonly Dictionary<string, Variant> _props = new();
        public int ConnectionCount(string signal) => _connections.TryGetValue(signal, out var l) ? l.Count : 0;
        public void TestEmit(string signal, params object?[] args)
        {
            AssertMainThread("Emit");
            if (!_connections.TryGetValue(signal, out var list)) return;
            foreach (var c in list.ToArray()) c.Delegate!.DynamicInvoke(args);
        }

        // ---- GodotSharp API 形状 ----
        public static bool IsInstanceValid(GodotObject? instance) => instance is not null && !instance._freed;
        public void CallDeferred(StringName method, params Variant[] args) { }
        public ulong GetInstanceId() => 0;
        public void Free() { AssertMainThread("Free"); _freed = true; }
        public Variant Get(StringName property) { AssertMainThread("Get"); return _props.TryGetValue(property.Name, out var v) ? v : default; }
        public void Set(StringName property, Variant value) { AssertMainThread("Set"); _props[property.Name] = value; }
        public Error EmitSignal(StringName signal, params Variant[] args)
        {
            AssertMainThread("EmitSignal");
            Emitted.Add((signal.Name, args));
            return Error.Ok;
        }
        public readonly List<(string, Variant[])> Emitted = new();
        public Error Connect(StringName signal, Callable callable, uint flags = 0)
        {
            AssertMainThread("Connect");
            if (!_connections.TryGetValue(signal.Name, out var l)) _connections[signal.Name] = l = new();
            l.Add(callable);
            return Error.Ok;
        }
        public void Disconnect(StringName signal, Callable callable)
        {
            AssertMainThread("Disconnect");
            if (_connections.TryGetValue(signal.Name, out var l)) l.RemoveAll(c => c.Delegate == callable.Delegate);
        }
        public bool IsConnected(StringName signal, Callable callable)
        {
            AssertMainThread("IsConnected");
            return _connections.TryGetValue(signal.Name, out var l) && l.Exists(c => c.Delegate == callable.Delegate);
        }
    }

    public class RefCounted : GodotObject { }
    public class Resource : RefCounted { }
    public class RandomNumberGenerator : RefCounted { public float Randf() => 0.5f; public uint Randi() => 4; }

    public class Node : GodotObject
    {
        private Node? _parent;
        public readonly List<Node> Children = new();
        public bool QueuedForDeletion;
        public event Action? TreeExiting;
        public bool IsQueuedForDeletion() => QueuedForDeletion;
        public Node? GetParent() => _parent;
        // 测试辅助：模拟“离开场景树”（例如被重新挂载）与“QueueFree 后在帧末被删除”
        public void TestExitTree() => TreeExiting?.Invoke();
        public void TestDeleteQueued() { TreeExiting?.Invoke(); Free(); }
        public void AddChild(Node node, bool forceReadableName = false) { AssertMainThread("AddChild"); Children.Add(node); node._parent = this; }
        public void RemoveChild(Node node) { AssertMainThread("RemoveChild"); Children.Remove(node); node._parent = null; }
        public void QueueFree() { AssertMainThread("QueueFree"); QueuedForDeletion = true; }
        public double GetProcessDeltaTime() => Engine.ProcessDelta;
    }

    public class CanvasItem : Node { public bool Visible { get; set; } = true; }
    public class Node2D : CanvasItem
    {
        public Vector2 Position { get; set; }
        public float Rotation { get; set; }
        public Vector2 Scale { get; set; } = new(1, 1);
    }
    public class Node3D : Node
    {
        public Vector3 Position { get; set; }
        public Vector3 Rotation { get; set; }
        public Vector3 Scale { get; set; } = new(1, 1, 1);
        public bool Visible { get; set; } = true;
    }
    public class Window : Node { }

    public class MainLoop : GodotObject { }
    public class SceneTree : MainLoop
    {
        public event Action? ProcessFrame;
        public event Action? PhysicsFrame;
        public Window Root { get; } = new();
        public bool Paused { get; set; }
        // 测试辅助：推进一帧（先物理帧，再处理帧，与 Godot 的默认 60Hz 一致时每帧各一次）
        public void TestStep() { Engine.PhysicsFrameCount++; PhysicsFrame?.Invoke(); Engine.Frames++; ProcessFrame?.Invoke(); }
    }

    public static class Engine
    {
        public static ulong Frames = 1;
        public static ulong PhysicsFrameCount = 1;
        public static ulong GetPhysicsFrames() => PhysicsFrameCount;
        public static double ProcessDelta = 1.0 / 60.0;
        public static SceneTree? Tree = new();
        public static ulong GetProcessFrames() => Frames;
        public static MainLoop? GetMainLoop() => Tree;
    }

    public static class Dispatcher
    {
        // 测试主线程的同步上下文由 Program.Main 设置
        public static System.Threading.SynchronizationContext? TestContext;
        public static System.Threading.SynchronizationContext? SynchronizationContext => TestContext;
    }

    public static class OS
    {
        // 测试主线程由 Program.Main 设置
        public static ulong TestMainThreadId;
        public static ulong GetMainThreadId() => TestMainThreadId;
        public static ulong GetThreadCallerId() => (ulong)System.Environment.CurrentManagedThreadId;
    }

    public static class GD
    {
        public static readonly List<string> Printed = new();
        public static readonly List<string> Errors = new();
        public static void Print(string what) => Printed.Add(what);
        public static void PushError(string message) => Errors.Add(message);
        public static float Randf() => 0.5f;
        public static uint Randi() => 4;
    }

    public static class Mathf
    {
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }

    public static class Time
    {
        public static ulong GetTicksMsec() => 0;
        public static ulong GetTicksUsec() => 0;
        public static double GetUnixTimeFromSystem() => 0;
    }
}

namespace Godot.Collections
{
    public sealed class Array : System.Collections.Generic.List<Godot.Variant> { }
    public sealed class Dictionary : System.Collections.Generic.Dictionary<Godot.Variant, Godot.Variant> { }
}
