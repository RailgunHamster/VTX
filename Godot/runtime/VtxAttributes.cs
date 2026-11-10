#nullable enable
using System;

namespace VTX;

/// <summary>
/// 由 [TxField] 源码生成器自动实现的接口，用于支持提交前类不变量（[TxInvariant]）自动校验与持久化。
/// </summary>
public interface ITxObject
{
    /// <summary>校验当前对象的所有 [TxInvariant] 契约，不满足时调用 Tx.Check / Tx.Fail 触发回滚。</summary>
    [Decides]
    void ValidateTxInvariants();
}

/// <summary>
/// 标记在 partial class 的私有字段上（如 [TxField] private int _gold;），
/// 编译器 Source Generator 会自动生成：
/// 1. 带 Undo Log 记录的同名公开属性（如 public int Gold）；
/// 2. 强类型快照结构体 TxSnapshot 及 CaptureTxSnapshot() / RestoreTxSnapshot() / DiffTxState() 方法；
/// 3. JSON 持久化方法 ToTxJson() / FromTxJson(json)；
/// 4. ITxObject 接口实现（联动 [TxInvariant] 在事务提交前自动校验）。
/// </summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false, AllowMultiple = false)]
public sealed class TxFieldAttribute : Attribute
{
    public string? PropertyName { get; }
    public TxFieldAttribute(string? propertyName = null) => PropertyName = propertyName;
}

/// <summary>
/// 标记在 partial class 的无参校验方法上（如 [TxInvariant] private void CheckValid() => Tx.Check(Gold >= 0);）。
/// 当事务修改了该对象的任何 [TxField] 属性并准备 Commit 时，运行时会自动调用此方法；
/// 若校验失败，整个事务将自动回滚，防止非法脏状态提交。
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class TxInvariantAttribute : Attribute { }

/// <summary>
/// 标记 partial class 为跨会话持久化对象（对应 Verse 的 &lt;persistable&gt;）。
/// 配合项目根目录的 vtx.schema.txt（每行格式：ClassName|Prop1:Type1|Prop2:Type2）或类上的 [TxBaselineField] 契约，
/// 编译器会静态检查该类的 [TxField] 属性是否破坏了旧存档向后兼容性（删除已持久化字段或篡改字段类型将触发 VTX009 编译错误）。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class TxPersistableAttribute : Attribute
{
    public int Version { get; }
    public TxPersistableAttribute(int version = 1) => Version = version;
}

/// <summary>
/// 声明 [TxPersistable] 类在历史已发布版本中的基线字段名与类型契约（如 [TxBaselineField("Gold", "int")]）。
/// 若后续 AI 或开发者在迭代时误删了该 [TxField] 或将其类型改为不兼容类型，编译器将立即报 VTX009 错误。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class TxBaselineFieldAttribute : Attribute
{
    public string PropertyName { get; }
    public string TypeName { get; }

    public TxBaselineFieldAttribute(string propertyName, string typeName)
    {
        PropertyName = propertyName;
        TypeName = typeName;
    }
}

/// <summary>
/// 标记方法或属性访问器在事务中可安全回滚。
/// 编译器分析器会强制检查其内部所有修改与调用均具备可回滚性，且严禁跨帧挂起（await）。
/// 标在属性上等价于标在它的所有访问器上；重写（override）或实现接口成员时自动继承该效果。
/// 标在委托类型的参数上（如 <c>void ForEach([Transacts] Action&lt;T&gt; f)</c>）表示该委托会在事务内被调用：
/// 分析器会把调用方传入的 lambda 当作事务上下文检查，并拒绝传入无法静态验证的委托变量。
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true)]
public sealed class TransactsAttribute : Attribute { }

/// <summary>
/// 标记方法可能失败并触发事务自动回滚（隐含 [Transacts]）。
/// 在事务之外调用并且失败时，会抛出 <see cref="TxFailedException"/>。
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Parameter, Inherited = true)]
public sealed class DecidesAttribute : Attribute { }

/// <summary>
/// 标记方法为跨帧结构化并发协程。
/// 严禁在事务上下文中调用，且内部禁止使用不可取消的原生 ToSignal。
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true)]
public sealed class SuspendsAttribute : Attribute { }

/// <summary>
/// 标记方法或属性为无副作用纯函数，允许在事务内外安全调用。
/// 分析器会校验 [Pure] 方法体：只能修改自己的局部变量，只能调用其它 [Pure] 方法或白名单内的纯计算 API，
/// 只能通过 Tx.Simulate / Tx.WhereCan 这类“试跑后必定回滚”的入口触碰事务状态。
/// 标在委托参数上表示该委托必须是纯函数（传入的 lambda 按 [Pure] 规则检查）。
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true)]
public sealed class PureAttribute : Attribute { }

/// <summary>
/// 标记方法具有不可逆副作用，编译器严禁在事务上下文中直接调用。
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = true)]
public sealed class NoRollbackAttribute : Attribute { }
