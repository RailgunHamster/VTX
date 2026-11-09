#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using VTX;

namespace GodotTest.Examples;

/// <summary>
/// VTX 全特性完整示例（包含基础事务、5 项进阶 Verse 特性与自定义复杂数据结构接入）。
/// 本文件同时保存在：
/// 1. engine/modules/vtx/Godot/docs/Examples.cs（文档参考示例）
/// 2. godot-test/demo/VtxExamples.cs（在 godot-test 中直接编译并由 DemoPlayer 运行自检）
/// </summary>
public partial class VtxFeatureShowcase : Node3D
{
    // =========================================================================
    // 0. 基础 [TxField] 状态声明 + 事务容器
    //    TxFieldGenerator 会自动为 partial class 生成：
    //    - 公开属性：Gold, Mana, MaxMana, EquippedWeapon, IsChanneling, ShieldVfxActive
    //    - 快照与对比：CaptureTxSnapshot(), RestoreTxSnapshot(snap), DiffTxState(before)
    //    - ITxObject 接口（自动在 Commit 前调用所有 [TxInvariant] 方法）
    // =========================================================================
    [TxField] private int _gold = 100;
    [TxField] private int _mana = 50;
    [TxField] private int _maxMana = 100;
    [TxField] private string _equippedWeapon = "WoodenSword";
    [TxField] private bool _isChanneling;
    [TxField] private bool _shieldVfxActive;

    public TxList<string> Inventory { [Pure] get; } = new();
    public TxDict<string, int> Buffs { [Pure] get; } = new();
    public TxArray<int> BoardGrid { [Pure] get; } = new(8);

    // =========================================================================
    // 特性 4：类不变量校验（[TxInvariant]）
    // 只要事务修改了本对象的任何 [TxField]，在最外层或子事务准备 Commit 的瞬间，
    // 运行时会自动执行此方法。一旦违反守恒律或数值上下限，当场自动回滚整个事务！
    // =========================================================================
    [TxInvariant]
    private void CheckClassInvariants()
    {
        Tx.Check(Gold >= 0, "金币不能为负数");
        Tx.Check(Mana >= 0 && Mana <= MaxMana, "法力值超出合法区间 [0, MaxMana]");
    }

    // =========================================================================
    // 核心模式：CanExecute 与 Execute 二合一（[Decides]）
    // =========================================================================
    [Decides]
    public void BuyAndDrinkPotion(string potionName, int price, int manaRestore)
    {
        Gold -= price;
        // 特性 5：[CallerArgumentExpression] 自动捕获失败表达式 "Gold >= 0" 与自定义原因
        Tx.Check(Gold >= 0, "金币不足");

        Mana += manaRestore;
        // 注意：即使这里没手写 Tx.Check(Mana <= MaxMana)，提交前 [TxInvariant] 也会自动拦截溢出并回滚！
        Buffs[potionName] = Buffs.GetOrDefault(potionName, 0) + 1;
    }

    // =========================================================================
    // 特性 1：多候选回溯搜索与合法候选筛选（Tx.FirstOf & Tx.WhereCan）
    // =========================================================================
    public readonly record struct ShopOffer(string Name, int Price, int ManaRestore);

    /// <summary>
    /// 示例 1A (Tx.WhereCan)：遍历商店所有药水，在真实状态上无损试跑，
    /// 筛出所有“买得起且喝了之后法力不会溢出 [TxInvariant] 上限”的合法商品。
    /// </summary>
    [Pure]
    public List<ShopOffer> GetAffordableAndValidOffers(IEnumerable<ShopOffer> offers)
    {
        return Tx.WhereCan(offers, offer => BuyAndDrinkPotion(offer.Name, offer.Price, offer.ManaRestore));
    }

    /// <summary>
    /// 示例 1B (Tx.FirstOf)：按优先级尝试购买列表中的第一瓶合法药水；
    /// 前面失败的候选会自动回滚，第一个成功的候选直接提交生效并返回 true。
    /// </summary>
    public bool TryBuyFirstAvailableOffer(IEnumerable<ShopOffer> priorityOffers, out ShopOffer chosen)
    {
        return Tx.FirstOf(
            priorityOffers,
            offer => BuyAndDrinkPotion(offer.Name, offer.Price, offer.ManaRestore),
            out chosen);
    }

    // =========================================================================
    // 特性 2：结构化并发进阶（TxTask.Rush / WithScope + Branch / Defer）
    // =========================================================================
    /// <summary>
    /// 演示：
    /// 1. TxTask.Defer：进入吟唱时开启 IsChanneling = true，无论正常结束还是被取消自动归位 false；
    /// 2. TxTask.WithScope + scope.Branch：启动后台伴随护盾特效协程，当主技能结束退出作用域时自动取消清理；
    /// 3. TxTask.Rush：0.02s 施法前摇一结束主干立即继续，伴随的 0.04s 残影分支继续跑完。
    /// </summary>
    [Suspends]
    public async Task<string> CastUltimateSkillAsync(CancellationToken ct = default)
    {
        bool branchCleanedUp = false;

        await TxTask.WithScope(async scope =>
        {
            IsChanneling = true;
            // 1. Defer 守卫：离开此作用域（或异常/取消）时，必定恢复 IsChanneling = false
            using var _ = TxTask.Defer(() => IsChanneling = false);

            // 2. Branch 伴随子任务：循环维持护盾特效，直到外层 WithScope 结束自动被取消
            scope.Branch(async branchCt =>
            {
                ShieldVfxActive = true;
                using var vfxGuard = TxTask.Defer(() =>
                {
                    ShieldVfxActive = false;
                    branchCleanedUp = true;
                });
                await TxTask.Sleep(10.0, branchCt); // 主流程只需 0.02s，退出 WithScope 时这里会被自动取消
            });

            // 3. Rush：分支 0（0.02s 核心前摇）完结即返回继续主流程，分支 1（0.03s 刀光残影）继续在后台跑
            int rushWinner = await TxTask.Rush(
                scope.Token,
                async t => await TxTask.Sleep(0.02, t),
                async t => await TxTask.Sleep(0.03, t)
            );
        }, ct);

        return $"Channeling={IsChanneling}, ShieldVfx={ShieldVfxActive}, BranchCleaned={branchCleanedUp}";
    }

    // =========================================================================
    // 特性 3：自动生成的强类型快照与状态 Diff（CaptureTxSnapshot / DiffTxState）
    // =========================================================================
    public string DemoAutoSnapshotAndDiff()
    {
        // 1. 编译期自动生成的 CaptureTxSnapshot()，无需手写字符串拼接
        VtxFeatureShowcaseTxSnapshot before = CaptureTxSnapshot();

        // 2. 执行一次事务修改
        bool ok = Tx.Try(() =>
        {
            Gold -= 35;
            Mana += 20;
            EquippedWeapon = "DragonSpear";
        });

        // 3. 编译期自动生成的 DiffTxState(before)，直接输出结构化字段变更摘要
        string diff = DiffTxState(before);

        // 4. 还可以随时用 RestoreTxSnapshot(before) 一键还原（同样支持在事务内还原！）
        RestoreTxSnapshot(before);

        return $"Ok={ok} | Diff=[{diff}] | RestoredGold={Gold}";
    }

    // =========================================================================
    // 特性 5：失败原因追踪（Tx.Simulate / Tx.Try + out failReason）
    // =========================================================================
    [Pure]
    public string DiagnoseWhyCannotBuy(ShopOffer offer)
    {
        if (Tx.Simulate(() => BuyAndDrinkPotion(offer.Name, offer.Price, offer.ManaRestore), out string? reason))
        {
            return "CanBuy";
        }
        return $"Blocked: {reason}";
    }

    // =========================================================================
    // 综合自检入口（验证全部 5 项新特性）
    // =========================================================================
    public string RunAllNewFeaturesSelfTest()
    {
        Gold = 100;
        Mana = 50;
        MaxMana = 100;
        EquippedWeapon = "WoodenSword";

        var offers = new[]
        {
            new ShopOffer("TooExpensiveElixir", Price: 250, ManaRestore: 10), // 失败：金币不足 (Gold >= 0)
            new ShopOffer("OverflowManaPotion", Price: 20, ManaRestore: 80),  // 失败：50+80=130 触发 [TxInvariant] 法力上限回滚！
            new ShopOffer("ValidMinorManaPotion", Price: 30, ManaRestore: 25) // 成功：Gold=70, Mana=75
        };

        // 验证特性 5 (Failure Reason) & 特性 4 ([TxInvariant] 自动拦截法力溢出并回滚 Gold)
        string reason0 = DiagnoseWhyCannotBuy(offers[0]);
        string reason1 = DiagnoseWhyCannotBuy(offers[1]);
        bool invariantRolledBackGold = Gold == 100 && Mana == 50;

        // 验证特性 1A (Tx.WhereCan 无损筛选合法商品)
        List<ShopOffer> validOffers = GetAffordableAndValidOffers(offers);
        bool whereCanOk = validOffers.Count == 1 && validOffers[0].Name == "ValidMinorManaPotion" && Gold == 100;

        // 验证特性 1B (Tx.FirstOf 自动回溯前两个失败项并提交第 3 个)
        bool firstOfOk = TryBuyFirstAvailableOffer(offers, out ShopOffer chosen) &&
                         chosen.Name == "ValidMinorManaPotion" &&
                         Gold == 70 &&
                         Mana == 75;

        // 验证特性 3 (自动生成的 Snapshot 与 DiffTxState)
        Gold = 100;
        Mana = 50;
        string diffReport = DemoAutoSnapshotAndDiff();
        bool diffOk = diffReport.Contains("Gold: 100 -> 65") &&
                      diffReport.Contains("EquippedWeapon: WoodenSword -> DragonSpear") &&
                      Gold == 100;

        return $"[VTX 5-Features] InvariantRollback={invariantRolledBackGold} ({reason1}) | " +
               $"FailTrace=({reason0}) | WhereCan={whereCanOk} | FirstOf={firstOfOk} | AutoDiff={diffOk} ({diffReport})";
    }
}
