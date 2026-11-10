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
│   ├── Tx.cs                   # 事务运行时：Tx.Try, Tx.Simulate, Tx.FirstOf, Tx.WhereCan, Tx.Check/Fail/NotNull（抛异常）, Tx.Require/Failed（返回 false）, Tx.Record, Tx.Defer, Tx.EnterWorkerScope
│   ├── TxCollections.cs        # O(1) 增量回滚容器：TxArray<T>, TxList<T>, TxDict<TKey, TValue>
│   ├── TxRandom.cs             # 确定性可回滚伪随机数生成器（保证 Simulate 预览与 Try 真跑结果一致）
│   ├── TxGodot.cs              # Godot 节点事务安全封装：SetPosition/Rotation/Visible + 延迟节点树/信号操作
│   ├── VtxThread.cs            # 主线程判断（每个线程只问引擎一次）
│   └── TxTask.cs               # 帧同步结构化并发：Race, Sync, Rush, WithScope+Branch, Defer, Sleep, NextFrame/DelayFrames/NextPhysicsFrame, WaitUntil, WaitSignal<T>, LifetimeOf, Forget, GetActiveWaits
└── analyzers/                  # 链接编译进 Godot.SourceGenerators.dll
    ├── TxFieldGenerator.cs     # [TxField] 属性/快照/Diff/JSON 生成器 + VTX.Res 强类型资产清单生成器
    └── VtxAnalyzer.cs          # Roslyn 静态分析器（强制拦截 VTX001 ~ VTX010、VTX012 ~ VTX015；VTX011 由生成器报告）
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

## 测试

`tests/` 是一个独立的控制台测试工程，不依赖 Godot 编辑器：
- 运行时测试：用 `tests/GodotStubs.cs` 中的 Godot API 桩 + 模拟主线程的 SynchronizationContext，覆盖事务、容器、TxGodot、TxTask（含跨线程取消、暂停、信号源释放）；
- 分析器 / 生成器测试：`tests/Cases/*.cs` 每行用 `// expect: VTX001` 标注期望诊断，未标注的行不允许出现诊断；带 `CaseRun` 的用例还会编译并执行生成后的代码；
- `docs/Examples.cs` 会在测试中零诊断编译，并执行它自带的 `RunAllNewFeaturesSelfTest()`。

```powershell
cd "[root]/godot/engine/modules/vtx/Godot/tests"
dotnet run                 # 全部测试
dotnet run -- Analyzer     # 只跑名称包含 Analyzer 的测试
$env:VTX_TEST_DUMP="D:\tmp\vtx-gen"; dotnet run -- 07_   # 把生成器输出写到目录里查看
```

桩测试覆盖不到真实引擎的时序（暂停、时间缩放、信号、节点释放、线程）。真实引擎验收在 `godot-test/demo/vtx_demo/`：
`run_vtx_demo.bat` 会重新生成 GodotSharp、编译 godot-test，并以 headless + 固定 60 帧/秒运行 `VtxDemo.tscn`，
每项检查在 `last_run/3_godot.log` 里打印一行 `[VTX-DEMO] PASS/FAIL`，退出码 0 表示全部通过。

## 使用说明与 AI 编码规范

详见 [`docs/AI_GUIDE.md`](docs/AI_GUIDE.md)。
