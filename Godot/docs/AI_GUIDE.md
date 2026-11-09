# VTX (`using VTX;`) 人类与 AI Agent 编码指南

> **给 AI Agent 的核心提示（TL;DR）**：
> 本引擎内置了 `VTX` 事务回滚与结构化并发系统，并由 Roslyn 编译器强制执行 `VTX001`~`VTX006` 红线。
> 1. **禁止拆分 `CanExecute()` 与 `Execute()`**：凡涉及多步条件校验与状态修改（如扣费、合成、出牌、吃碰杠、技能结算），统一写成一个标有 `[Decides]` 的方法，在里面直接修改事务状态并用 `Tx.Check(cond)` 校验。
>    - 询问是否可执行（UI 按钮高亮 / AI 推演）：调用 `Tx.Simulate(() => MyAction())`（试跑后 100% 自动回滚）。
>    - 真正执行：调用 `Tx.Try(() => MyAction())`（全部成功则提交，任一步失败则自动回滚）。
> 2. **事务状态声明规则**：
>    - 标量/结构体/引用字段：在 `partial class` 中写 `[TxField] private int _gold;`，读写时**必须使用生成的公开属性 `Gold`**（禁止直接写 `_gold`，否则触发 `VTX004`）。
>    - 容器字段：**严禁对 `T[]`、`List<T>`、`Dictionary<K,V>` 贴 `[TxField]`（触发 `VTX006`）**。请直接声明只读属性：`public TxList<Item> Items { [Pure] get; } = new();`、`TxArray<T>` 或 `TxDict<K,V>`。
> 3. **跨帧异步等待规则**：
>    - 严禁在 `[Transacts]` / `[Decides]` 方法或 `Tx.Try` 块内使用 `await`（触发 `VTX003`）。
>    - 跨帧协程统一标 `[Suspends]`，使用 `TxTask.Race`、`TxTask.Sync`、`TxTask.Sleep(seconds, ct)` 和 `TxTask.WaitSignal(obj, signal, ct)`。**严禁在 `[Suspends]` 内调用原生 `ToSignal`（触发 `VTX005`）**。

---

## 一、快速上手模板

### 1. 声明可回滚状态与执行多步事务

```csharp
using Godot;
using VTX;

public partial class BattleUnit : Node3D
{
    // 1. 标量/值类型字段：贴 [TxField]，编译器自动生成公开属性 Gold、Mana、Weapon
    [TxField] private int _gold = 100;
    [TxField] private int _mana = 50;
    [TxField] private string _weapon = "WoodenSword";

    // 2. 容器：使用 TxList / TxDict / TxArray，按元素增量记录 Undo Log
    public TxList<string> Inventory { [Pure] get; } = new();
    public TxDict<string, int> Buffs { [Pure] get; } = new();

    // 3. 规则逻辑只写一份：标记 [Decides]，边改状态边用 Tx.Check 校验
    [Decides]
    public void CastUpgradeSpell(int slotIndex, int manaCost)
    {
        Gold -= 40;
        Tx.Check(Gold >= 0);                    // 不足则中断，Gold 自动回滚

        Mana -= manaCost;
        Tx.Check(Mana >= 0);                    // 不足则中断，Gold 与 Mana 自动回滚

        string mat = Inventory.At(slotIndex);   // 越界自动中断并回滚
        Tx.Check(mat == "MythrilOre");

        Inventory.RemoveAt(slotIndex);
        Weapon = "MagicBlade";
        Buffs["AtkUp"] = Buffs.GetOrDefault("AtkUp", 0) + 1;

        // 修改 Godot 节点属性（失败可回滚）
        TxGodot.SetPosition(this, Position + new Vector3(1, 0, 0));

        // 不可逆副作用（日志、发信号、增删节点）通过 TxGodot 或 Tx.Defer 延迟到 Commit 时执行
        TxGodot.Log("升级成功！");
    }

    // UI 按钮判定或 AI 沙盒推演：试跑一遍，无论成败都 100% 还原现场
    [Pure]
    public bool CanCastUpgradeSpell(int slotIndex, int manaCost) =>
        Tx.Simulate(() => CastUpgradeSpell(slotIndex, manaCost));

    // 真正执行：成功则 Commit，失败则自动还原所有修改
    public bool TryCastUpgradeSpell(int slotIndex, int manaCost) =>
        Tx.Try(() => CastUpgradeSpell(slotIndex, manaCost));
}
```

---

### 2. 结构化并发（`TxTask`）

无需在 `_Process` 手动驱动（`TxTask` 会自动挂载 `SceneTree.ProcessFrame`），直接编写无泄漏的跨帧逻辑：

```csharp
[Suspends]
public async Task WaitPlayerActionOrTimeout(Button confirmBtn)
{
    int winner = await TxTask.Race(
        // 分支 0：等待玩家点击按钮（若超时落败，自动 Disconnect 信号回调，绝不泄漏）
        async ct => await TxTask.WaitSignal(confirmBtn, BaseButton.SignalName.Pressed, ct),

        // 分支 1：10 秒游戏逻辑时间超时（受暂停/Engine.TimeScale 影响）
        async ct =>
        {
            try
            {
                await TxTask.Sleep(10.0, ct);
            }
            finally
            {
                // 若玩家提前点击按钮，该分支立即收到取消并执行 finally 清理
            }
        }
    );
}
```

---

## 二、效果属性标签速查表

| 属性标签 | 含义与编译器约束 |
|---|---|
| `[TxField]` | 贴在 `partial class` 的私有字段上，自动生成带 Undo Log 的公开属性（`_hp` $\rightarrow$ `Hp`）。 |
| `[Transacts]` | 方法/属性可安全在事务内调用，内部一切修改与调用受 `VTX001`~`VTX003` 检查。 |
| `[Decides]` | 方法不仅可回滚，还可能通过 `Tx.Check(false)` 或 `Tx.Fail()` 触发失败控制流。 |
| `[Pure]` | 无副作用纯方法/只读属性，允许在事务内外任意调用。 |
| `[NoRollback]` | 具有不可逆副作用（如写磁盘、发网络包），在事务内调用会直接触发 `VTX002` 报错。 |
| `[Suspends]` | 跨帧协程方法，严禁在事务内调用（`VTX003`），且内部严禁使用裸 `ToSignal`（`VTX005`）。 |

---

## 三、自定义复杂数据结构如何接入事务？

当 `[TxField]` + `TxArray<T>` / `TxList<T>` / `TxDict<K,V>` 不足以表达你的底层数据结构（例如：自定义的双向链表、稀疏八叉树、位棋盘 Bitboard、环形缓冲区）时，**切勿使用全量深拷贝**。

请直接在自定义数据结构内部使用 **`Tx.Record`** 编写增量逆操作，并为公开的写方法贴上 `[Transacts]`：

```csharp
using VTX;

public sealed class CustomBitboard
{
    private ulong _bits;
    private int _popCount;

    public ulong Bits { [Pure] get => _bits; }
    public int PopCount { [Pure] get => _popCount; }

    [Transacts]
    public void SetBit(int index)
    {
        ulong mask = 1UL << index;
        if ((_bits & mask) != 0)
            return;

        // 零闭包增量记录：仅在事务激活时保存旧的 (bits, popCount)
        if (Tx.IsActive)
        {
            Tx.Record(this, _bits, _popCount, static (self, oldBits, oldPop) =>
            {
                self._bits = oldBits;
                self._popCount = oldPop;
            });
        }

        _bits |= mask;
        _popCount++;
    }
}
```
> **注意**：务必使用 `static (self, oldVal) => ...` 静态 Lambda 传给 `Tx.Record`，这样 C# 编译器会全局缓存该委托实例，配合 `Tx` 内部的线程本地泛型对象池，可以实现 **0 字节 GC 堆分配**。

---

## 四、编译期红线（`VTX001` ~ `VTX006`）排错字典

当 AI 或开发者编写的代码违反事务安全边界时，`dotnet build` 会报出以下错误。遇到报错时请严格按对应方式修正，**严禁使用 `#pragma warning disable` 屏蔽**：

| 错误码 | 触发原因 | 标准修复方法 |
|---|---|---|
| **`VTX001`** | 事务内修改了非事务属性/字段、原生数组元素 `arr[i]` 或外部捕获的局部变量 | 1. 标量字段改为 `[TxField] private T _field;` 并写生成的属性；<br>2. 数组/列表改为 `TxArray<T>` / `TxList<T>` / `TxDict<K,V>`；<br>3. Godot 节点属性改用 `TxGodot.SetPosition` / `SetProperty` 等。 |
| **`VTX002`** | 事务内调用了未标记 `[Transacts]`/`[Decides]`/`[Pure]` 的方法，或调用了 `[NoRollback]` 方法 | 1. 若被调方法是纯计算，给它加上 `[Pure]`；<br>2. 若被调方法只修改事务状态，给它加上 `[Transacts]` 或 `[Decides]`；<br>3. 若是日志/信号/节点释放等外部副作用，改用 `TxGodot.Log`/`EmitSignal`/`QueueFree` 或包在 `Tx.Defer(() => ...)` 中。 |
| **`VTX003`** | 在事务方法或 `Tx.Try` 块内使用了 `await` 或调用了 `[Suspends]` 方法 | 事务必须在单帧同步内完成。请将跨帧 `await` 移出事务块：先在事务内完成状态试探或结算，事务结束后再 `await TxTask.Sleep(...)`。 |
| **`VTX004`** | 在手写代码中直接给 `[TxField]` 的底层私有字段（如 `_gold`）赋值 | 改为写入生成器自动生成的公开属性（如 `Gold = ...`），以确保修改经过 Undo Log。 |
| **`VTX005`** | 在 `[Suspends]` 方法内调用了不可取消的 Godot 原生 `ToSignal` | 替换为 `await TxTask.WaitSignal(obj, signalName, ct)`，确保在 `TxTask.Race` 取消时能自动退订信号。 |
| **`VTX006`** | 在 `[TxField]` 上声明了 `T[]`、`List<T>`、`Dictionary<K,V>` 等非事务可变容器 | 移除 `[TxField]`，改为只读属性持有事务容器：<br>`public TxList<T> Items { [Pure] get; } = new();` |
