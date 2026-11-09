# VTX

面向 AI 辅助游戏开发的**单线程内存事务回滚（Transactional Rollback）**、**帧同步结构化并发（Structured Concurrency）**与 **100% 编译期效果红线（Compile-Time Effect Redlines）**基础设施。

核心思路：
- **消除 `CanExecute()` 与 `Execute()` 的逻辑分裂**：只写一份带 `[Decides]` 的业务规则代码，直接修改状态并用 `Tx.Check(cond)` 校验。UI 可用性判定或 AI 走法推演直接调用 `Tx.Simulate(...)`（试跑后 100% 自动回滚），真正执行调用 `Tx.Try(...)`（全部成功则提交，任一步失败则逆序自动回滚）。
- **结构化并发零泄漏**：提供按游戏帧时间推进的 `TxTask.Race` / `Sync` / `Sleep` / `WaitSignal`，赢家完成时自动取消落败分支并退订信号回调。
- **80% 状态回滚 + 100% 编译期红线**：通过源码生成器自动生成 `[TxField]` 属性 Undo Log，并由编译器静态分析器强制执行 `VTX001`~`VTX006` 红线。任何在事务内修改非事务状态、调用不可逆 API 或跨帧 `await` 的越界行为均在编译期当场拦截。

## 引擎支持

每个引擎一个独立子目录，支持按需稀疏检出（Sparse Checkout）：

| 目录 | 引擎 / 平台 | 状态 |
| --- | --- | --- |
| [`Godot/`](Godot/README.md) | Godot 4.8（.NET / C#，内建于 `GodotSharp` 与 `Godot.SourceGenerators`） | 可用 |

## 只取单个引擎目录（以 Godot 为例）

```powershell
git clone --filter=blob:none --sparse https://github.com/RailgunHamster/VTX.git vtx
git -C vtx sparse-checkout set Godot
```

详细接入方式、架构说明以及面向人类与 AI Agent 的编码规范见对应引擎目录（如 [`Godot/README.md`](Godot/README.md) 与 [`Godot/docs/AI_GUIDE.md`](Godot/docs/AI_GUIDE.md)）。
