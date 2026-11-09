#nullable enable
using System;

namespace VTX;

/// <summary>
/// 标记在 partial class 的私有字段上（如 [TxField] private int _gold;），
/// 编译器 Source Generator 会自动生成带 Undo Log 记录的同名公开属性（如 public int Gold）。
/// </summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
public sealed class TxFieldAttribute : Attribute
{
    public string? PropertyName { get; }
    public TxFieldAttribute(string? propertyName = null) => PropertyName = propertyName;
}

/// <summary>
/// 标记方法或属性访问器在事务中可安全回滚。
/// 编译器分析器会强制检查其内部所有修改与调用均具备可回滚性，且严禁跨帧挂起（await）。
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = true)]
public sealed class TransactsAttribute : Attribute { }

/// <summary>
/// 标记方法可能失败并触发事务自动回滚。
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class DecidesAttribute : Attribute { }

/// <summary>
/// 标记方法为跨帧结构化并发协程。
/// 严禁在事务上下文中调用，且内部禁止使用不可取消的原生 ToSignal。
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class SuspendsAttribute : Attribute { }

/// <summary>
/// 标记方法或属性为无副作用纯函数，允许在事务内外安全调用。
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = true)]
public sealed class PureAttribute : Attribute { }

/// <summary>
/// 标记方法具有不可逆副作用，编译器严禁在事务上下文中直接调用。
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = true)]
public sealed class NoRollbackAttribute : Attribute { }
