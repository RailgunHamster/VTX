# VTX (`using VTX;`) 人类与 AI Agent 编码指南

> **给 AI Agent 的核心提示（TL;DR）**：
> 本引擎内置了 `VTX` 事务回滚与结构化并发系统，并由 Roslyn 编译器强制执行 **`VTX001` ~ `VTX009` 九道红线**。完整可编译示例代码见同目录下的 [`Examples.cs`](Examples.cs)。
> 1. **禁止拆分 `CanExecute()` 与 `Execute()`**：凡涉及多步条件校验与状态修改（如扣费、合成、出牌、吃碰杠、技能结算），统一写成一个标有 `[Decides]` 的方法，在里面直接修改事务状态并用 `Tx.Check(cond, reason)` 校验。
>    - 询问是否可执行（UI 按钮高亮 / AI 推演）：调用 `Tx.Simulate(() => MyAction(), out string? failReason)`（试跑后 100% 自动回滚，失败时拿到原因）。
>    - 真正执行：调用 `Tx.Try(() => MyAction(), out string? failReason)`（全部成功则提交，任一步失败则自动回滚）。
>    - 枚举/搜索多个候选：使用 `Tx.WhereCan(candidates, x => MyAction(x))`（无损筛选所有合法项）或 `Tx.FirstOf(candidates, x => MyAction(x), out var chosen)`（按优先级回溯试探并提交第一个成功项）。
> 2. **事务状态、不变量與持久化声明规则**：
>    - 标量/结构体/引用字段：在 `partial class` 中写 `[TxField] private int _gold;`，编译器会自动生成公开属性 `Gold`、快照 `CaptureTxSnapshot()` / `RestoreTxSnapshot(snap)`、状态对比 `DiffTxState(before)` 以及 JSON 序列化 `ToTxJson()` / `FromTxJson(json)`。读写时**必须使用生成的公开属性 `Gold`**（禁止直接写 `_gold`，否则触发 `VTX004`）。
>    - 类不变量（守恒律/上下限契约）：在方法上贴 `[TxInvariant]`（如 `[TxInvariant] private void CheckValid() => Tx.Check(Mana <= MaxMana);`），事务 Commit 前会自动调用校验，不满足则自动回滚整个事务。
>    - 跨版本存档兼容：在类上贴 `[TxPersistable]` 与 `[TxBaselineField("Gold", "int")]`（或维护项目根目录 `vtx.schema.txt`），若后续删除或篡改已持久化字段类型将触发 `VTX009` 编译报错。
>    - 容器字段：**严禁对 `T[]`、`List<T>`、`Dictionary<K,V>` 贴 `[TxField]`（触发 `VTX006`）**。请直接声明只读属性：`public TxList<Item> Items { [Pure] get; } = new();`、`TxArray<T>` 或 `TxDict<K,V>`。
> 3. **确定性随机与资源路径规则**：
>    - 事务或 `[Pure]` 方法内**严禁调用 `System.Random`、`GD.Randf`/`Randi`、`DateTime.Now`（触发 `VTX007`）**。请使用状态随事务自动回滚的 **`TxRandom`**（保证 `Tx.Simulate` 预览与 `Tx.Try` 真跑掷出完全相同的随机数）。
>    - 引用项目资源时优先使用自动生成的强类型清单 **`VTX.Res`**（如 `Res.Scenes.Player`、`Res.Shaders.Player`、`Res.Textures.Icon`）；手写不存在的 `"res://..."` 字符串路径会直接触发 `VTX008` 编译错误。
> 4. **跨帧异步等待规则**：
>    - 严禁在 `[Transacts]` / `[Decides]` 方法或 `Tx.Try` 块内使用 `await`（触发 `VTX003`）。
>    - 跨帧协程统一标 `[Suspends]`，使用 `TxTask.Race`、`TxTask.Sync`、`TxTask.Rush`、`TxTask.WithScope`（配合 `scope.Branch`）、`TxTask.Defer`、`TxTask.Sleep(seconds, ct)` 和支持强类型返回值解包的 `TxTask.WaitSignal<T0, T1>(obj, signal, ct)`。**严禁在 `[Suspends]` 内调用原生 `ToSignal`（触发 `VTX005`）**。

---

## 一、全特性速览（完整代码见 [`Examples.cs`](Examples.cs)）

### 1. `[TxField]` 自动生成套件（属性 + 快照 + Diff + JSON 持久化）
在 `partial class` 中给私有字段贴上 `[TxField]`，源码生成器自动生成四组能力：
- **事务属性**：`public T PropName { [Pure] get; [Transacts] set; }`
- **强类型快照**：`CaptureTxSnapshot()` 与 `RestoreTxSnapshot(snapshot)`
- **结构化状态 Diff**：`DiffTxState(beforeSnapshot)`（输出如 `"Gold: 100 -> 65, EquippedWeapon: WoodenSword -> DragonSpear"`）
- **JSON 持久化**：`ToTxJson()` 与 `[Decides] FromTxJson(json)`（在事务内反序列化存档同样受回滚与 `[TxInvariant]` 保护）

### 2. `[TxPersistable]` 存档向后兼容编译校验（`VTX009`）
贴上 `[TxPersistable]` 与 `[TxBaselineField("Gold", "int")]`（或在项目根目录放置 `vtx.schema.txt`），当 AI 在后续需求迭代中不小心删掉了 `Gold` 或把 `int` 改成 `string` 时，编译器立即报 `VTX009` 错误拦截。

### 3. `VTX.Res` 强类型资产清单（Assets as Code）与 `VTX008`
编译器在构建时自动扫描 Godot 项目目录，生成 `VTX.Res.Scenes`、`VTX.Res.Shaders`、`VTX.Res.Textures`、`VTX.Res.Resources`、`VTX.Res.Audio`、`VTX.Res.Scripts` 常量。同时，代码中任何手写的 `"res://..."` 字面量都会在编译期检查磁盘文件是否存在，写错路径立即报 `VTX008`。

### 4. `TxRandom` 确定性可回滚随机数与 `VTX007`
`TxRandom` 的内部 64 位种子受 `Tx.Record` Undo Log 保护：
- 在 `Tx.Simulate` 中试跑暴击/抽卡时，试跑结束种子自动回退；紧接着调用 `Tx.Try` 真跑时，必然掷出 **100% 相同的随机结果**；
- 在 `Tx.Try` 中若后续条件失败触发回滚，随机种子同样回退，杜绝“故意触发失败垫刀刷随机数”。

### 5. `TxTask.WaitSignal<T0, T1, ...>` 强类型信号参数解包
支持 0~3 个参数的强类型异步等待，并在被 `Race` 或父作用域取消时自动 `Disconnect`：
```csharp
(string droppedItem, int droppedQty) = await TxTask.WaitSignal<string, int>(this, SignalName.LootDropped, ct);
```

---

## 二、编译期红线（`VTX001` ~ `VTX009`）排错字典

当 AI 或开发者编写的代码违反安全边界时，`dotnet build` 会报出以下错误。遇到报错时请严格按对应方式修正，**严禁使用 `#pragma warning disable` 屏蔽**：

| 错误码 | 触发原因 | 标准修复方法 |
|---|---|---|
| **`VTX001`** | 事务内修改了非事务属性/字段、原生数组元素 `arr[i]` 或外部捕获的局部变量 | 1. 标量字段改为 `[TxField] private T _field;` 并写生成的属性；<br>2. 数组/列表改为 `TxArray<T>` / `TxList<T>` / `TxDict<K,V>`；<br>3. Godot 节点属性改用 `TxGodot.SetPosition` / `SetProperty` 等。 |
| **`VTX002`** | 事务内调用了未标记 `[Transacts]`/`[Decides]`/`[Pure]` 的方法，或调用了 `[NoRollback]` 方法 | 1. 若被调方法是纯计算，给它加上 `[Pure]`；<br>2. 若被调方法只修改事务状态，给它加上 `[Transacts]` 或 `[Decides]`；<br>3. 若是日志/信号/节点释放等外部副作用，改用 `TxGodot.Log`/`EmitSignal`/`QueueFree` 或包在 `Tx.Defer(() => ...)` 中。 |
| **`VTX003`** | 在事务方法或 `Tx.Try` 块内使用了 `await` 或调用了 `[Suspends]` 方法 | 事务必须在单帧同步内完成。请将跨帧 `await` 移出事务块：先在事务内完成状态试探或结算，事务结束后再 `await TxTask.Sleep(...)`。 |
| **`VTX004`** | 在手写代码中直接给 `[TxField]` 的底层私有字段（如 `_gold`）赋值 | 改为写入生成器自动生成的公开属性（如 `Gold = ...`），以确保修改经过 Undo Log。 |
| **`VTX005`** | 在 `[Suspends]` 方法内调用了不可取消的 Godot 原生 `ToSignal` | 替换为 `await TxTask.WaitSignal(obj, signalName, ct)`，确保在 `TxTask.Race` 取消时能自动退订信号。 |
| **`VTX006`** | 在 `[TxField]` 上声明了 `T[]`、`List<T>`、`Dictionary<K,V>` 等非事务可变容器 | 移除 `[TxField]`，改为只读属性持有事务容器：<br>`public TxList<T> Items { [Pure] get; } = new();` |
| **`VTX007`** | 在事务或 `[Pure]` 内调用了 `System.Random`、`GD.Randf`/`Randi`、`DateTime.Now`、`Guid.NewGuid` | 改用状态可随事务自动回滚的 `TxRandom`（如 `Rng.NextInt(...)` / `Rng.CheckChance(...)`），或在事务外生成后作为参数传入。 |
| **`VTX008`** | 代码中的 `"res://..."` 字符串路径在项目磁盘中不存在 | 改用编译期自动生成的 `VTX.Res` 强类型常量（如 `Res.Scenes.Player`），或检查文件路径拼写。 |
| **`VTX009`** | `[TxPersistable]` 类删除了已持久化的基线字段或更改了字段类型 | 恢复被删改的 `[TxField]` 字段与原类型以保持旧存档兼容，新需求请通过新增字段平滑扩展。 |
