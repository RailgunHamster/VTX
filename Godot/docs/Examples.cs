#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using VTX;

namespace GodotTest.Examples;

/// <summary>
/// VTX 全特性完整示例：
/// 涵盖基础事务、回溯搜索、结构化并发、自动快照/Diff、类不变量、失败原因追踪、
/// 失败的写法：[Decides] 方法一律返回 bool，用 Tx.Require / Tx.Failed，调用方 if (!X()) return false; 传播；
/// 以及新增的：
/// - 特性 8：[TxPersistable] + [TxBaselineField] 存档向后兼容编译校验 + ToTxJson / FromTxJson
/// - 特性 9：VTX.Res 强类型资产清单（Assets as Code）+ VTX008 路径存在性编译校验
/// - 特性 10：TxTask.WaitSignal<T0, T1> 强类型信号参数解包等待
/// - 特性 12：TxRandom 确定性可回滚随机数（保证 Simulate 预览与 Try 真跑掷出完全相同的随机结果）+ VTX007 红线
/// </summary>
[TxPersistable(version: 1)]
[TxBaselineField("Gold", "int")]
[TxBaselineField("Mana", "int")]
[TxBaselineField("MaxMana", "int")]
[TxBaselineField("EquippedWeapon", "string")]
public partial class VtxFeatureShowcase : Node3D
{
    [Signal]
    public delegate void LootDroppedEventHandler(string itemId, int quantity);

    // =========================================================================
    // 0. 基础 [TxField] 状态声明 + 事务容器 + 确定性可回滚随机数 TxRandom
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
    public TxRandom Rng { [Pure] get; } = new(seed: 123456789UL);

    // =========================================================================
    // 特性 4：类不变量校验（[TxInvariant]）
    // =========================================================================
    [TxInvariant]
    private bool CheckClassInvariants() =>
        Tx.Require(Gold >= 0, "金币不能为负数") &&
        Tx.Require(Mana >= 0 && Mana <= MaxMana, "法力值超出合法区间 [0, MaxMana]");

    // =========================================================================
    // 核心模式：CanExecute 与 Execute 二合一（[Decides]）
    // =========================================================================
    [Decides]
    public bool BuyAndDrinkPotion(string potionName, int price, int manaRestore)
    {
        if (!Tx.Require(Gold >= price, "金币不足")) return false;
        Gold -= price;

        Mana += manaRestore;   // 超过 MaxMana 时由 [TxInvariant] 在提交前拦下
        Buffs[potionName] = Buffs.GetOrDefault(potionName, 0) + 1;
        return true;
    }

    // =========================================================================
    // 特性 1：多候选回溯搜索与合法候选筛选（Tx.FirstOf & Tx.WhereCan）
    // =========================================================================
    public readonly record struct ShopOffer(string Name, int Price, int ManaRestore);

    [Pure]
    public List<ShopOffer> GetAffordableAndValidOffers(IEnumerable<ShopOffer> offers)
    {
        var valid = new List<ShopOffer>();
        Tx.WhereCan(offers, offer => BuyAndDrinkPotion(offer.Name, offer.Price, offer.ManaRestore), valid);
        return valid;
    }

    public bool TryBuyFirstAvailableOffer(IEnumerable<ShopOffer> priorityOffers, out ShopOffer chosen) =>
        Tx.FirstOf(
            priorityOffers,
            offer => BuyAndDrinkPotion(offer.Name, offer.Price, offer.ManaRestore),
            out chosen);

    // =========================================================================
    // 特性 2 & 10：结构化并发（Rush / WithScope + Branch / Defer / WaitSignal<T0, T1>）
    // =========================================================================
    [Suspends]
    public async Task<string> CastUltimateSkillAsync(CancellationToken ct = default)
    {
        bool branchCleanedUp = false;

        await TxTask.WithScope(async scope =>
        {
            IsChanneling = true;
            using var _ = TxTask.Defer(() => IsChanneling = false);

            scope.Branch(async branchCt =>
            {
                ShieldVfxActive = true;
                using var vfxGuard = TxTask.Defer(() =>
                {
                    ShieldVfxActive = false;
                    branchCleanedUp = true;
                });
                await TxTask.Sleep(10.0, branchCt);
            });

            await TxTask.Rush(
                scope.Token,
                async t => await TxTask.Sleep(0.02, t),
                async t => await TxTask.Sleep(0.03, t)
            );
        }, ct);

        // 特性 10：强类型信号等待 WaitSignal<string, int>，直接解包信号参数作为返回值
        var waitLootTask = TxTask.WaitSignal<string, int>(this, SignalName.LootDropped, ct);
        EmitSignal(SignalName.LootDropped, "DragonScale", 3);
        (string droppedItem, int droppedQty) = await waitLootTask;

        return $"Channeling={IsChanneling}, ShieldVfx={ShieldVfxActive}, BranchCleaned={branchCleanedUp}, Loot={droppedItem}x{droppedQty}";
    }

    // =========================================================================
    // 特性 3 & 8：自动快照 Diff + JSON 持久化序列化（ToTxJson / FromTxJson）
    // =========================================================================
    public string DemoAutoSnapshotDiffAndPersistence()
    {
        VtxFeatureShowcaseTxSnapshot before = CaptureTxSnapshot();
        string savedJson = ToTxJson();

        bool ok = Tx.Try(() =>
        {
            Gold -= 35;
            Mana += 20;
            EquippedWeapon = "DragonSpear";
        });

        string diff = DiffTxState(before);

        // 通过自动生成的 FromTxJson 在事务内恢复存档（同样受 [TxInvariant] 保护）
        bool restoredOk = Tx.Try(() => FromTxJson(savedJson));

        return $"Ok={ok} | Restored={restoredOk} | Diff=[{diff}] | GoldAfterRestore={Gold}";
    }

    // =========================================================================
    // 特性 9：强类型资产引用（VTX.Res）
    // =========================================================================
    [Pure]
    public string DemoStronglyTypedAssetRegistry()
    {
        // 由 TxFieldGenerator 扫描项目目录自动生成的强类型常量，改名或删除会在编译期报错
        string scenePath = Res.Scenes.Player;
        string shaderPath = Res.Shaders.Player;
        string iconPath = Res.Textures.Icon;
        return $"Scene={scenePath}, Shader={shaderPath}, Icon={iconPath}";
    }

    // =========================================================================
    // 特性 12：确定性可回滚随机数（TxRandom）
    // =========================================================================
    [Decides]
    public bool TryRollForgeEnhancement(int goldCost, out int bonus)
    {
        bonus = 0;
        if (!Tx.Require(Gold >= goldCost, "强化金币不足")) return false;
        Gold -= goldCost;

        // 使用 TxRandom 生成随机暴击倍率：
        // 若在 Simulate 中预览，种子会在预览结束后自动回退；紧接着调 Try 会掷出完全相同的点数！
        bonus = Rng.NextInt(10, 100);
        return true;
    }

    public bool VerifyDeterministicRollBetweenSimulateAndTry()
    {
        ulong initialSeed = Rng.State;

        // 1. 先用 Simulate 沙盒预览下一次强化会掷出多少点加成
        bool canRoll = Tx.Simulate(this, static (VtxFeatureShowcase s, out int bonus) => s.TryRollForgeEnhancement(20, out bonus), out int previewBonus);
        // Simulate 结束后，Rng.State 已自动回滚回 initialSeed，Gold 仍为 100
        bool seedRewound = Rng.State == initialSeed && Gold == 100;

        // 2. 再用 Try 真正执行强化：因为种子已回退到同一位置，掷出的加成与预览 100% 相同！
        bool committed = Tx.Try(this, static (VtxFeatureShowcase s, out int bonus) => s.TryRollForgeEnhancement(20, out bonus), out int actualBonus);
        Gold = 100; // 还原金币供后续测试

        return canRoll && seedRewound && committed && previewBonus == actualBonus;
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
    // 综合自检入口
    // =========================================================================
    public string RunAllNewFeaturesSelfTest()
    {
        Gold = 100;
        Mana = 50;
        MaxMana = 100;
        EquippedWeapon = "WoodenSword";

        var offers = new[]
        {
            new ShopOffer("TooExpensiveElixir", Price: 250, ManaRestore: 10),
            new ShopOffer("OverflowManaPotion", Price: 20, ManaRestore: 80),
            new ShopOffer("ValidMinorManaPotion", Price: 30, ManaRestore: 25)
        };

        string reason0 = DiagnoseWhyCannotBuy(offers[0]);
        string reason1 = DiagnoseWhyCannotBuy(offers[1]);
        bool invariantRolledBackGold = Gold == 100 && Mana == 50;

        List<ShopOffer> validOffers = GetAffordableAndValidOffers(offers);
        bool whereCanOk = validOffers.Count == 1 && validOffers[0].Name == "ValidMinorManaPotion" && Gold == 100;

        bool firstOfOk = TryBuyFirstAvailableOffer(offers, out ShopOffer chosen) &&
                         chosen.Name == "ValidMinorManaPotion" &&
                         Gold == 70 &&
                         Mana == 75;

        Gold = 100;
        Mana = 50;
        string diffReport = DemoAutoSnapshotDiffAndPersistence();
        bool diffAndPersistOk = diffReport.Contains("Gold: 100 -> 65") &&
                                diffReport.Contains("Restored=True") &&
                                Gold == 100;

        bool rngDeterministicOk = VerifyDeterministicRollBetweenSimulateAndTry();
        string assetsInfo = DemoStronglyTypedAssetRegistry();
        bool assetsOk = assetsInfo.Contains("res://demo/player.tscn") &&
                        assetsInfo.Contains("res://demo/player.gdshader") &&
                        assetsInfo.Contains("res://icon.svg");

        return $"[VTX Full Showcase] Invariant={invariantRolledBackGold} | FailTrace=({reason0}) | " +
               $"WhereCan={whereCanOk} | FirstOf={firstOfOk} | Persist&Diff={diffAndPersistOk} | " +
               $"DetermRng={rngDeterministicOk} | AssetsAsCode={assetsOk} ({assetsInfo})";
    }
}
