#nullable enable
using System;
using Godot;

namespace VTX;

/// <summary>
/// Godot 引擎对象的事务安全桥接层：
/// 1. 常用空间/可视属性修改立即生效，并在事务回滚时自动恢复旧值；
/// 2. 节点树增删、释放、信号分发、日志打印推迟到最外层事务成功 Commit 时统一执行。
///
/// 语义边界（重要）：
/// - 立即生效的属性修改会同步触发引擎侧的副作用（例如 visibility_changed 信号、变换通知），
///   回滚只能把属性值改回去，无法撤回这些已经发出的通知；Tx.Simulate 中同样会触发。
///   对这类属性有强依赖的监听逻辑，请改为在 Tx.Defer 中修改。
/// - 延迟执行的节点树操作在当前事务里不可见：同一事务内 AddChild 之后立刻 GetParent() 仍为 null。
/// - 回滚时若对象已被释放（IsInstanceValid 为 false），对应的恢复步骤会被跳过。
/// </summary>
public static class TxGodot
{
    [Transacts]
    public static void SetPosition(Node3D node, Vector3 newPosition)
    {
        ArgumentNullException.ThrowIfNull(node);
        Vector3 prev = node.Position;
        if (prev != newPosition)
        {
            if (Tx.IsActive)
            {
                Tx.Record(node, prev, static (n, oldPos) =>
                {
                    if (GodotObject.IsInstanceValid(n))
                        n.Position = oldPos;
                });
            }
            node.Position = newPosition;
        }
    }

    [Transacts]
    public static void SetRotation(Node3D node, Vector3 newRotation)
    {
        ArgumentNullException.ThrowIfNull(node);
        Vector3 prev = node.Rotation;
        if (prev != newRotation)
        {
            if (Tx.IsActive)
            {
                Tx.Record(node, prev, static (n, oldRot) =>
                {
                    if (GodotObject.IsInstanceValid(n))
                        n.Rotation = oldRot;
                });
            }
            node.Rotation = newRotation;
        }
    }

    [Transacts]
    public static void SetScale(Node3D node, Vector3 newScale)
    {
        ArgumentNullException.ThrowIfNull(node);
        Vector3 prev = node.Scale;
        if (prev != newScale)
        {
            if (Tx.IsActive)
            {
                Tx.Record(node, prev, static (n, oldScale) =>
                {
                    if (GodotObject.IsInstanceValid(n))
                        n.Scale = oldScale;
                });
            }
            node.Scale = newScale;
        }
    }

    [Transacts]
    public static void SetVisible(Node3D node, bool visible)
    {
        ArgumentNullException.ThrowIfNull(node);
        bool prev = node.Visible;
        if (prev != visible)
        {
            if (Tx.IsActive)
            {
                Tx.Record(node, prev, static (n, oldVis) =>
                {
                    if (GodotObject.IsInstanceValid(n))
                        n.Visible = oldVis;
                });
            }
            node.Visible = visible;
        }
    }

    [Transacts]
    public static void SetPosition2D(Node2D node, Vector2 newPosition)
    {
        ArgumentNullException.ThrowIfNull(node);
        Vector2 prev = node.Position;
        if (prev != newPosition)
        {
            if (Tx.IsActive)
            {
                Tx.Record(node, prev, static (n, oldPos) =>
                {
                    if (GodotObject.IsInstanceValid(n))
                        n.Position = oldPos;
                });
            }
            node.Position = newPosition;
        }
    }

    [Transacts]
    public static void SetRotation2D(Node2D node, float newRotation)
    {
        ArgumentNullException.ThrowIfNull(node);
        float prev = node.Rotation;
        if (prev != newRotation)
        {
            if (Tx.IsActive)
            {
                Tx.Record(node, prev, static (n, oldRot) =>
                {
                    if (GodotObject.IsInstanceValid(n))
                        n.Rotation = oldRot;
                });
            }
            node.Rotation = newRotation;
        }
    }

    [Transacts]
    public static void SetScale2D(Node2D node, Vector2 newScale)
    {
        ArgumentNullException.ThrowIfNull(node);
        Vector2 prev = node.Scale;
        if (prev != newScale)
        {
            if (Tx.IsActive)
            {
                Tx.Record(node, prev, static (n, oldScale) =>
                {
                    if (GodotObject.IsInstanceValid(n))
                        n.Scale = oldScale;
                });
            }
            node.Scale = newScale;
        }
    }

    /// <summary>CanvasItem（Node2D / Control）的可见性。</summary>
    [Transacts]
    public static void SetVisible(CanvasItem item, bool visible)
    {
        ArgumentNullException.ThrowIfNull(item);
        bool prev = item.Visible;
        if (prev != visible)
        {
            if (Tx.IsActive)
            {
                Tx.Record(item, prev, static (n, oldVis) =>
                {
                    if (GodotObject.IsInstanceValid(n))
                        n.Visible = oldVis;
                });
            }
            item.Visible = visible;
        }
    }

    /// <summary>
    /// 通用属性写入：记录旧值，回滚时用 Set 写回。属性 setter 自身的副作用不会被撤回。
    /// </summary>
    [Transacts]
    public static void SetProperty(GodotObject obj, StringName property, Variant value)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(property);
        Variant prev = obj.Get(property);
        if (Tx.IsActive)
        {
            Tx.Record(obj, property, prev, static (o, prop, oldVal) =>
            {
                if (GodotObject.IsInstanceValid(o))
                    o.Set(prop, oldVal);
            });
        }
        obj.Set(property, value);
    }

    /// <summary>延迟到最外层事务提交时执行 AddChild（当前事务内不可见）。</summary>
    [Transacts]
    public static void AddChild(Node parent, Node child)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);
        Tx.Defer(() => parent.AddChild(child));
    }

    /// <summary>延迟到最外层事务提交时执行 RemoveChild（当前事务内不可见）。</summary>
    [Transacts]
    public static void RemoveChild(Node parent, Node child)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);
        Tx.Defer(() => parent.RemoveChild(child));
    }

    /// <summary>延迟到最外层事务提交时执行 QueueFree；若事务回滚则节点保持存活。</summary>
    [Transacts]
    public static void QueueFree(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        Tx.Defer(() =>
        {
            if (GodotObject.IsInstanceValid(node))
                node.QueueFree();
        });
    }

    /// <summary>延迟到最外层事务提交时发出信号（参数在调用时拷贝）。</summary>
    [Transacts]
    public static void EmitSignal(GodotObject obj, StringName signal, params Variant[] args)
    {
        ArgumentNullException.ThrowIfNull(obj);
        ArgumentNullException.ThrowIfNull(signal);
        Variant[] captured = args is null || args.Length == 0 ? Array.Empty<Variant>() : (Variant[])args.Clone();
        Tx.Defer(() =>
        {
            if (GodotObject.IsInstanceValid(obj))
                obj.EmitSignal(signal, captured);
        });
    }

    /// <summary>延迟到最外层事务提交时打印日志；回滚或 Simulate 中不会打印。</summary>
    [Transacts]
    public static void Log(string message)
    {
        Tx.Defer(() => GD.Print(message));
    }
}
