# VTX for Godot（C# 引擎内建事务回滚与结构化并发）

将 **VTX**（内存事务回滚、帧同步结构化并发、编译期效果检查）内建到 Godot 4.8 .NET 引擎中。所有使用 `Godot.NET.Sdk` 的 C# 游戏项目无需安装插件或修改 `.csproj`，只需 `using VTX;` 即可开箱使用。

---

## 目录结构

```text
Godot/
├── README.md                   # Godot 模块接入与构建说明
├── docs/
│   ├── AI_GUIDE.md             # 人类与 AI Agent 编写规范与排错手册（可直接被项目 AGENTS.md 引用）
│   └── Examples.cs             # 涵盖全部 VTX 特性的完整可编译代码示例
├── runtime/                    # 链接编译进 GodotSharp.dll (namespace VTX)
│   ├── VtxAttributes.cs        # [TxField], [TxInvariant], [TxPersistable], [Transacts], [Decides], [Suspends], [Pure], [NoRollback]
│   ├── Tx.cs                   # 事务运行时：Tx.Try, Tx.Simulate, Tx.FirstOf, Tx.WhereCan, Tx.Check, Tx.NotNull, Tx.Record, Tx.Defer
│   ├── TxCollections.cs        # O(1) 增量回滚容器：TxArray<T>, TxList<T>, TxDict<TKey, TValue>
│   ├── TxRandom.cs             # 确定性可回滚伪随机数生成器（保证 Simulate 预览与 Try 真跑结果一致）
│   ├── TxGodot.cs              # Godot 节点事务安全封装：SetPosition/Rotation/Visible + 延迟节点树/信号操作
│   └── TxTask.cs               # 帧同步结构化并发：Race, Sync, Rush, WithScope+Branch, Defer, Sleep, WaitSignal<T>
└── analyzers/                  # 链接编译进 Godot.SourceGenerators.dll
    ├── TxFieldGenerator.cs     # [TxField] 属性/快照/Diff/JSON 生成器 + VTX.Res 强类型资产清单生成器
    └── VtxAnalyzer.cs          # Roslyn 静态分析器（强制拦截 VTX001 ~ VTX009）
```

---

## 引擎接入与构建

在 Godot 引擎源码树中，将本仓库置于 `engine/modules/vtx`：

1. **`GodotSharp.csproj`**（`engine/modules/mono/glue/GodotSharp/GodotSharp/GodotSharp.csproj`）：
   ```xml
   <Compile Include="..\..\..\..\vtx\Godot\runtime\*.cs" LinkBase="VTX" />
   ```
2. **`Godot.SourceGenerators.csproj`**（`engine/modules/mono/editor/Godot.NET.Sdk/Godot.SourceGenerators/Godot.SourceGenerators.csproj`）：
   ```xml
   <Compile Include="..\..\..\..\vtx\Godot\analyzers\*.cs" LinkBase="VTX" />
   ```
3. **重新编译程序集并刷新本地 NuGet 包缓存**：
   ```powershell
   cd "[root]/godot/engine"
   python modules\mono\build_scripts\build_assemblies.py --godot-output-dir=bin --push-nupkgs-local="bin\GodotSharp\Tools\nupkgs"
   ```

---

## 使用说明与 AI 编码规范

详见 [`docs/AI_GUIDE.md`](docs/AI_GUIDE.md)。
