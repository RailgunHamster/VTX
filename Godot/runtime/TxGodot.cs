#nullable enable
using System;
using Godot;

namespace VTX;

/// <summary>
/// Godot 引擎对象的事务安全桥接层：
/// 1. 常用空间/可视属性修改立即生效，并在事务回滚时自动恢复旧值；
/// 2. 节点树增删、释放、信号分发、日志打印推迟到事务成功 Commit 时统一执行。
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
                Tx.Record(node, prev, static (n, oldPos) => n.Position = oldPos);
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
                Tx.Record(node, prev, static (n, oldRot) => n.Rotation = oldRot);
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
                Tx.Record(node, prev, static (n, oldScale) => n.Scale = oldScale);
            }
            node.Scale = newScale;
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
                Tx.Record(node, prev, static (n, oldPos) => n.Position = oldPos);
            }
            node.Position = newPosition;
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
                Tx.Record(node, prev, static (n, oldVis) => n.Visible = oldVis);
            }
            node.Visible = visible;
        }
    }

    [Transacts]
    public static void SetProperty(GodotObject obj, StringName property, Variant value)
    {
        ArgumentNullException.ThrowIfNull(obj);
        Variant prev = obj.Get(property);
        if (Tx.IsActive)
        {
            Tx.Record(obj, property, prev, static (o, prop, oldVal) => o.Set(prop, oldVal));
        }
        obj.Set(property, value);
    }

    [Transacts]
    public static void AddChild(Node parent, Node child)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);
        Tx.Defer(() => parent.AddChild(child));
    }

    [Transacts]
    public static void RemoveChild(Node parent, Node child)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(child);
        Tx.Defer(() => parent.RemoveChild(child));
    }

    [Transacts]
    public static void QueueFree(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        Tx.Defer(node.QueueFree);
    }

    [Transacts]
    public static void EmitSignal(GodotObject obj, StringName signal, params Variant[] args)
    {
        ArgumentNullException.ThrowIfNull(obj);
        Tx.Defer(() => obj.EmitSignal(signal, args));
    }

    [Transacts]
    public static void Log(string message)
    {
        Tx.Defer(() => GD.Print(message));
    }
}
