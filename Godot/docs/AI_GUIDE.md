# VTX (`using VTX;`) 人类与 AI Agent 编码指南

> **给 AI Agent 的核心提示（TL;DR）**：
> 本引擎内置了 `VTX` 事务回滚与结构化并发系统，并由 Roslyn 编译器强制执行 **`VTX001` ~ `VTX016` 十六道红线**。完整可编译示例代码见同目录下的 [`Examples.cs`](Examples.cs)。
> 1. **禁止拆分 `CanExecute()` 与 `Execute()`**：凡涉及多步条件校验与状态修改（如扣费、合成、出牌、吃碰杠、技能结算），统一写成一个标有 `[Decides]` 的方法，在里面直接修改事务状态。
>    - **失败 = 返回 `false`，只有这一种写法**：`[Decides]` 方法必须返回 `bool`（`VTX016`）。检查条件写 `if (!Tx.Require(cond, "原因")) return false;`，直接失败写 `return Tx.Failed("原因");`，判空写 `if (target is not { } t) return Tx.Failed("没有目标");`，成功走到最后 `return true;`。需要带回结果时用 out 参数：`bool TryFind(out Enemy enemy)`。
>    - **失败必须一路传播**：调用别的 `[Decides]` 方法时写 `if (!Other(...)) return false;`，或者 `return A() && B();`。结果不能丢弃、不能先存进变量或当参数传（`VTX014`）；失败后不能接着做别的事（`VTX015`）。要“A 不行就试 B”（Verse 的 `A[] or B[]`），先用保存点包住 A：`if (!Tx.Try(this, static s => s.A())) return B();`——内层 `Tx.Try` 失败只回滚 A 自己。
>    - **异常只表示 bug**：事务里抛出的异常（越界、空引用、`KeyNotFoundException`……）会先回滚，再原样抛给调用方；不要用异常表达“条件不满足”。
>    - 询问是否可执行（UI 按钮高亮 / AI 推演）：`Tx.Simulate(() => MyAction(), out string? failReason)`（试跑后 100% 自动回滚，失败时拿到原因）。
>    - 真正执行：`Tx.Try(() => MyAction(), out string? failReason)`（成功则提交，失败则自动回滚）。热路径用零分配写法：`Tx.Try(this, static s => s.MyAction())`。
>    - 需要结果：`Tx.Try(this, static (MyType s, out int bonus) => s.TryRoll(20, out bonus), out int bonus)`（`Tx.Simulate` 同理，可以预览结果）。
>    - `[Decides]` 方法只能在事务里调用（`Tx.Try` / `Tx.Simulate` / `Tx.FirstOf` / `Tx.WhereCan` 的 lambda 里，或另一个 `[Decides]` 方法里，否则 `VTX015`）。
>    - 枚举/搜索多个候选：`Tx.WhereCan(candidates, x => MyAction(x), resultList)`（无损筛选所有合法项，结果追加进 `resultList`）或 `Tx.FirstOf(candidates, x => MyAction(x), out var chosen)`（按优先级回溯试探并提交第一个成功项）。
> 2. **事务状态、不变量與持久化声明规则**：
>    - 标量/结构体/引用字段：在 `partial class` 中写 `[TxField] private int _gold;`，编译器会自动生成公开属性 `Gold`、快照 `CaptureTxSnapshot()` / `RestoreTxSnapshot(snap)`、状态对比 `DiffTxState(before)` 以及 JSON 序列化 `ToTxJson()` / `FromTxJson(json)`。读写时**必须使用生成的公开属性 `Gold`**（禁止直接写 `_gold`，否则触发 `VTX004`）。
>    - 类不变量（守恒律/上下限契约）：在返回 `bool` 的无参方法上贴 `[TxInvariant]`（如 `[TxInvariant] private bool CheckValid() => Tx.Require(Mana <= MaxMana, "法力超上限");`，多条用 `&&` 连起来），事务 Commit 前会自动调用校验，返回 `false` 则回滚整个事务。
>    - 跨版本存档兼容：在类上贴 `[TxPersistable]` 与 `[TxBaselineField("Gold", "int")]`（或维护项目根目录 `vtx.schema.txt`），若后续删除或篡改已持久化字段类型将触发 `VTX009` 编译报错。
>    - 容器字段：**严禁对 `T[]`、`List<T>`、`Dictionary<K,V>` 贴 `[TxField]`（触发 `VTX006`）**。请直接声明只读属性：`public TxList<Item> Items { [Pure] get; } = new();`、`TxArray<T>` 或 `TxDict<K,V>`。
> 3. **`[Pure]` 与委托规则**：
>    - `[Pure]` 不是“消除报错”的开关，编译器会检查 `[Pure]` 方法体（`VTX010`）：只能改自己的局部变量，只能调用其它 `[Pure]` 方法；需要试跑事务逻辑请用 `Tx.Simulate` / `Tx.WhereCan`。
>    - 事务内调用委托时，委托必须可静态验证：直接在 `Tx.Try(...)` 参数位置写 lambda；自定义的高阶函数请把委托参数标为 `[Transacts]`（如 `void Each<T>(IEnumerable<T> xs, [Transacts] Action<T> f)`），调用方传入的 lambda 会按事务规则检查。
>    - `override` 与接口实现会自动继承基类 / 接口成员上的 `[Transacts]`、`[Pure]` 等效果，方法体同样受检查。
> 4. **确定性随机与资源路径规则**：
>    - 事务或 `[Pure]` 方法内**严禁调用 `System.Random`、`GD.Randf`/`Randi`、`DateTime.Now`（触发 `VTX007`）**。请使用状态随事务自动回滚的 **`TxRandom`**（保证 `Tx.Simulate` 预览与 `Tx.Try` 真跑掷出完全相同的随机数）。
>    - 引用项目资源时优先使用自动生成的强类型清单 **`VTX.Res`**（如 `Res.Scenes.Player`、`Res.Shaders.Player`、`Res.Textures.Icon`）；手写不存在的 `"res://..."` 字符串路径会直接触发 `VTX008` 编译错误。
> 5. **跨帧异步等待规则**：
>    - 严禁在 `[Transacts]` / `[Decides]` 方法或 `Tx.Try` 块内使用 `await`（触发 `VTX003`）。
>    - 跨帧协程统一标 `[Suspends]`，使用 `TxTask.Race`、`TxTask.Sync`、`TxTask.Rush`、`TxTask.WithScope`（配合 `scope.Branch`）、`TxTask.Defer`、`TxTask.Sleep(seconds, ct)`（暂停时不计时；暂停菜单里用 `TxTask.SleepIgnoringPause`）和支持强类型返回值解包的 `TxTask.WaitSignal<T0, T1>(obj, signal, ct)`。**严禁在 `[Suspends]` 内调用原生 `ToSignal`（触发 `VTX005`）**。
>    - 所有 `TxTask` API 都在 Godot 主线程调用；父令牌被取消时 `Race` / `Rush` / `Sync` 抛出 `OperationCanceledException`，不会返回“胜者”。
>    - 按帧等待用 `TxTask.NextFrame(ct)` / `DelayFrames(n, ct)` / `NextPhysicsFrame(ct)`，等条件用 `TxTask.WaitUntil(() => 条件, ct)`（条件必须只读）；**不要用 `Task.Delay`**（真实时间、不受暂停影响，触发 `VTX005`）。
>    - 协程手里有 `ct` 就一路传下去（否则 `VTX005`）；协程属于某个节点时用 `TxTask.LifetimeOf(this)` 作为令牌，节点 `QueueFree` / 被释放时协程自动取消。
>    - 不等待的 `[Suspends]` 调用必须写 `.Forget()`（异常会通过 `GD.PushError` 报告），`[Suspends]` 方法不能是 `async void`（`VTX012`）。
>    - 排查协程泄漏：`TxTask.TrackStackTraces = true;` 然后看 `TxTask.GetActiveWaits()`。
> 6. **线程规则**：`Tx`、`TxTask`、`TxGodot`、`[TxField]` 状态和场景树节点都只属于主线程（运行时会抛异常，编译期 `VTX013`）。后台线程（`Task.Run` / `Parallel` / `WorkerThreadPool`）只算拷贝出来的纯数据，在主线程上 `await` 它之后自动回到主线程再写回；已经身处后台线程时用 `await TxTask.SwitchToMainThread();` 回到主线程；在后台线程对只属于该线程的数据做事务推演时，先写 `using var scope = Tx.EnterWorkerScope();`（作用域内仍然不能碰 Godot 对象）。不要写 `ConfigureAwait(false)`。

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

### 6. 运行时语义边界（务必了解）
- **只有经过 Undo Log 的状态会回滚**：`[TxField]` 属性、`TxArray`/`TxList`/`TxDict`、`TxRandom`、`TxGodot` 封装。普通字段、BCL 集合、Godot 节点属性不会回滚（编译器会尽量拦住这些写法）。
- **`Tx.Defer` 只在最外层事务提交后执行**；若延迟动作抛出异常，事务仍然已提交，异常会在所有延迟动作执行完后从 `Tx.Try` 抛出（多个时为 `AggregateException`）。
- **`TxGodot` 的属性修改立即生效**，引擎同步发出的通知（如 `visibility_changed`）回滚时无法撤回，`Tx.Simulate` 中同样会触发；节点树操作（AddChild / RemoveChild / QueueFree）与信号推迟到提交时执行，因此**在同一事务内不可见**。
- **`WaitSignal` 的信号源被释放**时任务以 `ObjectDisposedException` 结束（原生 `ToSignal` 会永远挂起）。
- **`Rush` 的后台分支**只受 `parentCt` 控制，建议传入 `scope.Token`；后台分支的异常通过 `GD.PushError` 报告。
- **`TxRandom` + `Simulate` 可以“预知”随机结果**：例如在 `WhereCan` 里调用 `CheckChance`，返回的就是“这次会命中”的候选。若不希望玩家/AI 预知，请不要在可预览的逻辑里掷骰。
- **失败靠返回值传播**：`Tx.Require` / `Tx.Failed` 只记录原因并返回 `false`，要靠调用方一路 `return false` 传到 `Tx.Try`，在那里回滚。事务内部的 `Tx.Try(...)` 就是一个保存点：内层失败只回滚内层。失败一次约 80 ns、零分配。
- **Tx 容器越界、`TxDict` 缺键是 bug**，抛标准异常（事务内先回滚）；可能越界 / 缺键时用 `TryGetAt` / `TryGetValue`。
- **线程**：事务状态（Undo Log）按线程隔离；`Tx.EnterWorkerScope()` 只是声明“这些数据只属于当前线程”，VTX 不会替你加锁。
- **未覆盖（编译器不检查）**：属性 getter / 用户定义运算符 / 构造函数内部的副作用。请保持它们无副作用。

---

## 二、编译期红线（`VTX001` ~ `VTX009`）排错字典

当 AI 或开发者编写的代码违反安全边界时，`dotnet build` 会报出以下错误。遇到报错时请严格按对应方式修正，**严禁使用 `#pragma warning disable` 屏蔽**：

| 错误码 | 触发原因 | 标准修复方法 |
|---|---|---|
| **`VTX001`** | 事务内修改了非事务属性/字段、原生数组元素 `arr[i]`、外部捕获的局部变量、`ref` 参数 / `ref` 局部变量 / `Span`，或订阅/退订事件（通过赋值、`++`、`out`/`ref` 实参、元组解构都算修改；修改本事务内 `new` 出来的局部对象不算） | 1. 标量字段改为 `[TxField] private T _field;` 并写生成的属性；<br>2. 数组/列表改为 `TxArray<T>` / `TxList<T>` / `TxDict<K,V>`；<br>3. Godot 节点属性改用 `TxGodot.SetPosition` / `SetProperty` 等。 |
| **`VTX002`** | 事务内调用了未标记 `[Transacts]`/`[Decides]`/`[Pure]` 的方法、`[NoRollback]` 方法、无法静态验证的委托，或创建了非 `RefCounted` 的 Godot 对象（如 `new Node()`，回滚后泄漏）；也包括把委托变量 / 未标注的方法组传给 `[Transacts]` 参数 | 1. 若被调方法确实是纯计算，给它加上 `[Pure]`（方法体会被 `VTX010` 校验，不能用来“消除报错”）；<br>2. 若被调方法只修改事务状态，给它加上 `[Transacts]` 或 `[Decides]`；<br>3. 若是日志/信号/节点释放等外部副作用，改用 `TxGodot.Log`/`EmitSignal`/`QueueFree` 或包在 `Tx.Defer(() => ...)` 中；<br>4. 委托：在参数位置直接写 lambda，或把高阶函数的委托参数标为 `[Transacts]`；<br>5. Godot 节点：在事务外创建，或在 `Tx.Defer` 里创建。 |
| **`VTX003`** | 在事务方法或 `Tx.Try` 块内使用了 `await`（含 `await foreach` / `await using`）、调用了 `[Suspends]` 方法，或把事务方法写成了 `async` / 迭代器（`yield`） | 事务必须在单帧同步内完成。请将跨帧 `await` 移出事务块：先在事务内完成状态试探或结算，事务结束后再 `await TxTask.Sleep(...)`。 |
| **`VTX004`** | 在手写代码中直接给 `[TxField]` 的底层私有字段（如 `_gold`）赋值（构造函数内初始化除外） | 改为写入生成器自动生成的公开属性（如 `Gold = ...`），以确保修改经过 Undo Log。 |
| **`VTX005`** | 在 `[Suspends]` 方法内调用了不可取消的 `ToSignal`、使用了 `Task.Delay`，或手里有取消令牌却没有传给 `TxTask` 的等待 / 组合 API 或其它 `[Suspends]` 方法 | `ToSignal` → `TxTask.WaitSignal(obj, signal, ct)`；`Task.Delay` → `TxTask.Sleep(秒, ct)` / `DelayFrames(帧, ct)`；把 `ct` 传下去，确实不需要取消时显式传 `CancellationToken.None`。 |
| **`VTX006`** | 在 `[TxField]` 上声明了 `T[]`、`List<T>`、`Dictionary<K,V>`、Godot 集合等非事务可变容器（实现 `ICollection` 的类型；不可变集合与 Tx 容器除外） | 移除 `[TxField]`，改为只读属性持有事务容器：<br>`public TxList<T> Items { [Pure] get; } = new();` |
| **`VTX007`** | 在事务或 `[Pure]` 内调用了 `System.Random`、`GD.Randf`/`Randi`、`RandomNumberGenerator`、`Time.*`、`Stopwatch`、`DateTime.Now`/`Today`、`Guid.NewGuid` | 改用状态可随事务自动回滚的 `TxRandom`（如 `Rng.NextInt(...)` / `Rng.CheckChance(...)`），或在事务外生成后作为参数传入。 |
| **`VTX008`** | 代码中的 `"res://..."` 字符串路径在项目磁盘中不存在，或**大小写与磁盘不一致**（Windows 上能跑、导出后找不到） | 改用编译期自动生成的 `VTX.Res` 强类型常量（如 `Res.Scenes.Player`），或检查文件路径拼写与大小写。拼接出来的路径片段（`"res://levels/level_" + n`）与含 `{}` 的格式串不检查。 |
| **`VTX009`** | `[TxPersistable]` 类删除了已持久化的基线字段或更改了字段类型 | 恢复被删改的 `[TxField]` 字段与原类型以保持旧存档兼容，新需求请通过新增字段平滑扩展。 |
| **`VTX010`** | `[Pure]` 方法 / 属性 / lambda 有副作用：修改了非局部状态、调用了非 `[Pure]` 方法或 `[Transacts]` 方法 | 去掉副作用；若需要“试跑”事务逻辑，用 `Tx.Simulate(() => ...)`；若方法本来就会修改状态，把 `[Pure]` 换成 `[Transacts]`。 |
| **`VTX012`** | `[Suspends]` 方法返回的任务被丢弃（单独一句调用或 `_ = ...`），或 `[Suspends]` 方法写成了 `async void` | `await` 它；确实要“发出去不管”就写 `.Forget()`；`async void` 改成返回 `Task`。 |
| **`VTX013`** | 在后台线程的 lambda（`Task.Run`、`Task.Factory.StartNew`、`Parallel.*`、`ThreadPool.QueueUserWorkItem`、`new Thread(...)`、`WorkerThreadPool.AddTask`）里使用了 `Tx`、`TxTask`、`TxGodot`、`[TxField]` 属性或场景树节点的成员（lambda 里新建的局部对象、`CallDeferred` 等线程安全的投递除外） | 在主线程把需要的数据拷贝成局部变量再传进去，算完 `await` 回主线程再写入；写回节点也可以用 `CallDeferred` / `Callable.From(...).CallDeferred()`；对线程私有数据做事务推演时在 lambda 开头写 `using var scope = Tx.EnterWorkerScope();`。 |
| **`VTX014`** | `[Decides]` 方法（含 `Tx.Require` / `Tx.Failed`）的结果被丢弃（单独一句调用、`_ = ...`、放在 `Action` lambda 里），或没有直接使用（存进变量、当参数传） | 直接写在 `if` / `return` / `&&` 里：`if (!X(...)) return false;`、`return A() && B();`。 |
| **`VTX015`** | `[Decides]` 方法失败后没有传播失败而是继续执行（`if` 的失败分支不是 `return false` / `return Tx.Failed(...)` / `throw`、`A() \|\| B()`、`!A() && ...`、失败时返回了 `true`），或在事务之外调用它 | 失败就 `return false`；要“A 不行就 B”写 `if (!Tx.Try(this, static s => s.A())) return B();`（内层 Try 是保存点）；在 `Tx.Try(...)` / `Tx.Simulate(...)` 里调用。 |
| **`VTX016`** | `[Decides]` 方法（含 override / 接口实现）不返回 `bool` | 改成返回 `bool`：失败 `return false` / `return Tx.Failed(...)`，成功 `return true`；需要结果用 `out` 参数。 |
| **`VTX011`** | `[TxField]` / `[TxInvariant]` 用在了不支持的地方：struct、static / readonly 字段、非 partial 类型（含外层类型）、生成的属性名冲突或不合法、`[TxInvariant]` 方法带参数或不返回 `bool` | 改为 `partial class`；字段改成实例、非 readonly；用 `[TxField("Name")]` 指定不冲突的属性名；`[TxInvariant]` 写成无参数、返回 `bool` 的方法。 |
