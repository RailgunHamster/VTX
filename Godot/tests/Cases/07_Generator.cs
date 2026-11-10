using System;
using VTX;

namespace Game
{
    public partial class Outer
    {
        public partial class Inner { [TxField] private int _hp; }                          // expect: none
    }

    public partial class Box<T> where T : class
    {
        [TxField] private T? _item;                                                        // expect: none
        [TxField] private int _count;                                                      // expect: none
    }

    public partial record Rec { [TxField] private int _n; }                                // expect: none

    public partial class BaseC
    {
        [TxField] private int _a;
        [TxInvariant] private bool CheckA() => Tx.Require(A >= 0, "A 不能为负");
    }

    public partial class DerivedC : BaseC
    {
        [TxField] private int _b;
        [TxInvariant] private bool CheckB() => Tx.Require(B >= 0, "B 不能为负");
    }

    public sealed partial class SealedC { [TxField] private int _v; [TxInvariant] private bool Check() => V < 100; }

    public partial class OnlyInvariant
    {
        public int X;
        [TxInvariant] private bool Check() => Tx.Require(X >= 0, "X");
    }

    public partial struct S { [TxField] private int _v; }                                  // expect: VTX011
    public partial class St { [TxField] private static int _s; }                           // expect: VTX011
    public partial class Ro { [TxField] private readonly int _r = 1; }                     // expect: VTX011
    public class NotPartial { [TxField] private int _x; }                                  // expect: VTX011
    public class NotPartialOuter { public partial class In { [TxField] private int _x; } } // expect: VTX011
    public partial class Clash { [TxField] private int _x; public int X => 0; }            // expect: VTX011
    public partial class BadInvariant
    {
        [TxField] private int _x;
        [TxInvariant] private bool Bad(int a) => a > 0;                                    // expect: VTX011
        [TxInvariant] private void Old() { }                                               // expect: VTX011
    }
}

public static class CaseRun
{
    public static string Run()
    {
        var inner = new Game.Outer.Inner();
        Tx.Try(inner, static i => { i.Hp = 5; return false; });
        if (inner.Hp != 0) return "嵌套类回滚失败";

        var box = new Game.Box<string>();
        if (!Tx.Try(() => { box.Item = "x"; box.Count = 2; })) return "泛型类提交失败";
        Tx.Try(box, static b => { b.Item = null; return Tx.Failed(); });
        if (box.Item != "x") return "泛型类回滚失败";

        var d = new Game.DerivedC();
        if (Tx.Try(() => d.A = -1)) return "派生类实例没有校验基类不变量";
        if (Tx.Try(() => d.B = -1)) return "派生类不变量未校验";
        if (!Tx.Try(() => { d.A = 1; d.B = 2; })) return "合法修改失败";
        var snap = d.CaptureTxSnapshot();
        Tx.Try(() => d.B = 7);
        if (!d.DiffTxState(snap).Contains("B: 2 -> 7")) return "派生类 Diff: " + d.DiffTxState(snap);
        var baseSnap = ((Game.BaseC)d).CaptureTxSnapshot();
        if (baseSnap.A != 1) return "基类快照";

        var s = new Game.SealedC();
        if (Tx.Try(() => s.V = 200)) return "sealed 类不变量未校验";

        var o = new Game.OnlyInvariant();
        if (o is not ITxObject) return "只有 [TxInvariant] 的类也应实现 ITxObject";
        o.X = -1;
        if (Tx.Try(() => Tx.Touch(o))) return "手动 Touch 后应校验不变量";

        var r = new Game.Rec();
        string json = "";
        Tx.Try(() => r.N = 3);
        json = r.ToTxJson();
        var r2 = new Game.Rec();
        if (!Tx.Try(() => r2.FromTxJson(json)) || r2.N != 3) return "JSON 往返: " + json;
        return "ok";
    }
}
