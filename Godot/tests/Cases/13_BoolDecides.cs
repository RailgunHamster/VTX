using System;
using Godot;
using VTX;

public partial class Shop : Node
{
    [TxField] private int _gold = 10;
    [TxField] private int _gems = 1;

    [Decides]
    public bool BuyWithGold(int price)
    {
        Gold -= price;
        return Tx.Require(Gold >= 0, "金币不足");                                    // expect: none
    }

    [Decides]
    public bool BuyWithGems(int price)
    {
        if (!Tx.Require(Gems >= price, "宝石不足")) return false;                    // expect: none
        Gems -= price;
        return true;
    }

    [Decides]
    public bool Buy(int price)
    {
        Tx.Require(Gold > 0);                                                        // expect: VTX014
        BuyWithGold(price);                                                          // expect: VTX014
        _ = BuyWithGold(price);                                                      // expect: VTX014
        if (!BuyWithGold(price)) return false;                                       // expect: none
        if (!BuyWithGold(price)) { Tx.Fail("x"); }                                   // expect: none
        if (!BuyWithGold(price)) return Tx.Failed("买不起");                          // expect: none
        if (BuyWithGold(price)) { } else { throw new InvalidOperationException(); }  // expect: none
        if (!BuyWithGold(price)) BuyWithGems(1);                                     // expect: VTX015, VTX014
        if (BuyWithGold(price)) { Gems += 1; }                                       // expect: VTX015
        bool any = BuyWithGold(price) || BuyWithGems(price);                         // expect: VTX015
        bool both = BuyWithGold(price) && BuyWithGems(price);                        // expect: none
        if (!Tx.Try(this, static s => s.BuyWithGold(5))) { Gems -= 1; }              // expect: none（保存点：失败那一支已回滚）
        if (!Tx.Require(Gems > 0)) { Gold += 1; }                                    // expect: none（Require 自己不改状态）
        return BuyWithGold(price) && BuyWithGems(price);                             // expect: none
    }

    public void OnClick()
    {
        if (Tx.Try(this, static s => s.Buy(3))) { }                                  // expect: none
        if (Tx.Try(() => BuyWithGold(3))) { }                                        // expect: none
        if (BuyWithGold(3)) { }                                                      // expect: VTX015
        Tx.FirstOf(new[] { 1, 2 }, p => BuyWithGold(p), out int chosen);             // expect: none
    }
}

public static class CaseRun
{
    public static string Run()
    {
        var shop = new Shop();
        if (!Tx.Try(shop, static s => s.BuyWithGold(4) && s.BuyWithGems(1))) return "两次购买应成功";
        if (shop.Gold != 6 || shop.Gems != 0) return $"扣款错误 {shop.Gold} {shop.Gems}";
        if (Tx.Try(shop, static s => s.BuyWithGold(1) && s.BuyWithGems(1))) return "宝石不足应失败";
        if (shop.Gold != 6) return "失败后金币应回滚";
        if (Tx.LastFailReason != "宝石不足 (Gems >= price)") return "原因: " + Tx.LastFailReason;
        return "ok";
    }
}
