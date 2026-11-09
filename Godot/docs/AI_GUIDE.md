# VTX (`using VTX;`) 人类与 AI Agent 编码指南

> **给 AI Agent 的核心提示（TL;DR）**：
> 本引擎内置了 `VTX` 事务回滚与结构化并发系统，并由 Roslyn 编译器强制执行 `VTX001`~`VTX006` 红线。完整可编译示例代码见同目录下的 [`Examples.cs`](Examples.cs)。
> 1. **禁止拆分 `CanExecute()` 与 `Execute()`**：凡涉及多步条件校验与状态修改（如扣费、合成、出牌、吃碰杠、技能结算），统一写成一个标有 `[Decides]` 的方法，在里面直接修改事务状态并用 `Tx.Check(cond, reason)` 校验。
>    - 询问是否可执行（UI 按钮高亮 / AI 推演）：调用 `Tx.Simulate(() => MyAction(), out string? failReason)`（试跑后 100% 自动回滚，失败时拿到原因）。
>    - 真正执行：调用 `Tx.Try(() => MyAction(), out string? failReason)`（全部成功则提交，任一步失败则自动回滚）。
>    - 枚举/搜索多个候选：使用 `Tx.WhereCan(candidates, x => MyAction(x))`（无损筛选所有合法项）或 `Tx.FirstOf(candidates, x => MyAction(x), out var chosen)`（按优先级回溯试探并提交第一个成功项）。
> 2. **事务状态与不变量声明规则**：
>    - 标量/结构体/引用字段：在 `partial class` 中写 `[TxField] private int _gold;`，编译器会自动生成公开属性 `Gold`、强类型快照 `CaptureTxSnapshot()` / `RestoreTxSnapshot(snap)`、以及状态对比 `DiffTxState(before)`。读写时**必须使用生成的公开属性 `Gold`**（禁止直接写 `_gold`，否则触发 `VTX004`）。
>    - 类不变量（守恒律/上下限契约）：在方法上贴 `[TxInvariant]`（如 `[TxInvariant] private void CheckValid() => Tx.Check(Mana <= MaxMana);`），事务 Commit 前会自动调用校验，不满足则自动回滚整个事务。
>    - 容器字段：**严禁对 `T[]`、`List<T>`、`Dictionary<K,V>` 贴 `[TxField]`（触发 `VTX006`）**。请直接声明只读属性：`public TxList<Item> Items { [Pure] get; } = new();`、`TxArray<T>` 或 `TxDict<K,V>`。
> 3. **跨帧异步等待规则**：
>    - 严禁在 `[Transacts]` / `[Decides]` 方法或 `Tx.Try` 块内使用 `await`（触发 `VTX003`）。
>    - 跨帧协程统一标 `[Suspends]`，使用 `TxTask.Race`、`TxTask.Sync`、`TxTask.Rush`、`TxTask.WithScope`（配合 `scope.Branch`）、`TxTask.Defer`、`TxTask.Sleep(seconds, ct)` 和 `TxTask.WaitSignal(obj, signal, ct)`。**严禁在 `[Suspends]` 内调用原生 `ToSignal`（触发 `VTX005`）**。

---

## 一、核心与进阶特性一览（完整代码见 [`Examples.cs`](Examples.cs)）

### 1. `[TxField]` 自动属性、自动快照与 `DiffTxState`
在 `partial class` 中给私有字段贴上 `[TxField]`，源码生成器自动生成三组能力：
- **事务属性**：`public T PropName { [Pure] get; [Transacts] set; }`
- **强类型快照**：`CaptureTxSnapshot()` 与 `RestoreTxSnapshot(snapshot)`（可在事务内外一键保存/恢复所有事务字段）
- **结构化状态 Diff**：`DiffTxState(beforeSnapshot)`（直接输出如 `"Gold: 100 -> 65, EquippedWeapon: WoodenSword -> DragonSpear"`，方便 `aidbg` 调试与日志审计）

### 2. `[CallerArgumentExpression]` 失败原因自动追踪
`Tx.Check(Gold >= 0, "金币不足")` 在失败时会自动捕获源码表达式与原因，填入 `Tx.LastFailReason`（并通过 `Tx.Simulate(body, out string? failReason)` / `Tx.Try(body, out string? failReason)` 输出），生成如 `"金币不足 (Gold >= 0)"` 的精确诊断信息。

### 3. `[TxInvariant]` 类不变量提交前自动自检
在 `partial class` 的无参方法上标记 `[TxInvariant]`：
```csharp
[TxInvariant]
private void CheckClassInvariants()
{
    Tx.Check(Gold >= 0, "金币不能为负数");
    Tx.Check(Mana >= 0 && Mana <= MaxMana, "法力值超出合法区间 [0, MaxMana]");
}
```
只要事务修改了该对象的任意 `[TxField]` 或事务容器，在准备 Commit 的瞬间会自动调用所有 `[TxInvariant]` 方法；一旦违反契约，整个事务当场自动回滚。

### 4. `Tx.FirstOf` 与 `Tx.WhereCan`（多候选回溯与无损筛选）
- **`Tx.WhereCan(candidates, x => Act(x))`**：在真实游戏状态上逐个试跑 `Act(x)` 并强制回滚，返回所有合法的候选列表（用于卡牌可出牌高亮、麻将合法动作枚举、战棋可移动格子计算）。
- **`Tx.FirstOf(candidates, x => Act(x), out var chosen)`**：按顺序尝试每个候选，失败自动回滚试下一个，第一个成功的直接 Commit 生效并返回 `true`。

### 5. 结构化并发全家桶（`Race` / `Sync` / `Rush` / `WithScope`+`Branch` / `Defer`）
- **`TxTask.Race`**：第一个完成的分支获胜，其余分支立即取消并等待清理完成。
- **`TxTask.Sync`**：等待全部分支完成；任一分支异常则取消其余分支。
- **`TxTask.Rush`**：第一个分支完成时主干立即继续往下走，其余未完成分支继续在后台运行（受外层 `CancellationToken` 管控）。
- **`TxTask.WithScope(async scope => { scope.Branch(...); })`**：分叉出不阻塞主流程的伴随子任务（如持续护盾特效、循环音效），当离开 `WithScope` 时自动取消所有未结束的 `Branch` 子任务。
- **`using var _ = TxTask.Defer(() => ...)`**：在修改状态的紧邻位置注册清理逻辑，协程正常退出或被取消时自动按 LIFO 逆序执行。

---

## 二、效果属性标签速查表

| 属性标签 | 含义与编译器约束 |
|---|---|
| `[TxField]` | 贴在 `partial class` 的私有字段上，自动生成带 Undo Log 的公开属性、快照与 Diff 方法。 |
| `[TxInvariant]` | 贴在 `partial class` 的无参方法上，事务修改该对象后在 Commit 前自动执行契约校验。 |
| `[Transacts]` | 方法/属性可安全在事务内调用，内部一切修改与调用受 `VTX001`~`VTX003` 检查。 |
| `[Decides]` | 方法不仅可回滚，还可能通过 `Tx.Check(false)` 或 `Tx.Fail()` 触发失败控制流。 |
| `[Pure]` | 无副作用纯方法/只读属性，允许在事务内外任意调用。 |
| `[NoRollback]` | 具有不可逆副作用（如写磁盘、发网络包），在事务内调用会直接触发 `VTX002` 报错。 |
| `[Suspends]` | 跨帧协程方法，严禁在事务内调用（`VTX003`），且内部严禁使用裸 `ToSignal`（`VTX005`）。 |

---

## 三、编译期红线（`VTX001` ~ `VTX006`）排错字典

当 AI 或开发者编写的代码违反事务安全边界时，`dotnet build` 会报出以下错误。遇到报错时请严格按对应方式修正，**严禁使用 `#pragma warning disable` 屏蔽**：

| 错误码 | 触发原因 | 标准修复方法 |
|---|---|---|
| **`VTX001`** | 事务内修改了非事务属性/字段、原生数组元素 `arr[i]` 或外部捕获的局部变量 | 1. 标量字段改为 `[TxField] private T _field;` 并写生成的属性；<br>2. 数组/列表改为 `TxArray<T>` / `TxList<T>` / `TxDict<K,V>`；<br>3. Godot 节点属性改用 `TxGodot.SetPosition` / `SetProperty` 等。 |
| **`VTX002`** | 事务内调用了未标记 `[Transacts]`/`[Decides]`/`[Pure]` 的方法，或调用了 `[NoRollback]` 方法 | 1. 若被调方法是纯计算，给它加上 `[Pure]`；<br>2. 若被调方法只修改事务状态，给它加上 `[Transacts]` 或 `[Decides]`；<br>3. 若是日志/信号/节点释放等外部副作用，改用 `TxGodot.Log`/`EmitSignal`/`QueueFree` 或包在 `Tx.Defer(() => ...)` 中。 |
| **`VTX003`** | 在事务方法或 `Tx.Try` 块内使用了 `await` 或调用了 `[Suspends]` 方法 | 事务必须在单帧同步内完成。请将跨帧 `await` 移出事务块：先在事务内完成状态试探或结算，事务结束后再 `await TxTask.Sleep(...)`。 |
| **`VTX004`** | 在手写代码中直接给 `[TxField]` 的底层私有字段（如 `_gold`）赋值 | 改为写入生成器自动生成的公开属性（如 `Gold = ...`），以确保修改经过 Undo Log。 |
| **`VTX005`** | 在 `[Suspends]` 方法内调用了不可取消的 Godot 原生 `ToSignal` | 替换为 `await TxTask.WaitSignal(obj, signalName, ct)`，确保在 `TxTask.Race` 取消时能自动退订信号。 |
| **`VTX006`** | 在 `[TxField]` 上声明了 `T[]`、`List<T>`、`Dictionary<K,V>` 等非事务可变容器 | 移除 `[TxField]`，改为只读属性持有事务容器：<br>`public TxList<T> Items { [Pure] get; } = new();` |
