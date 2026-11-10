using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VTX;

public partial class Holes
{
    [TxField] private int _hp;
    public int Plain;
    public int[] Arr = new int[4];
    public List<int> Xs = new();

    [Pure] public int Bad => Plain++;                                        // expect: VTX010
    [Pure] public int Good => Plain + 1;                                     // expect: none

    public int Side() { Plain++; return Plain; }
    [Transacts] public int SideTx() { Hp++; return Hp; }

    [Transacts]
    public void Run(Func<int, bool> pred, [Transacts] Func<int, bool> txPred)
    {
        ref int r = ref Plain;
        r = 5;                                                               // expect: VTX001
        Span<int> s = Arr;
        s[0] = 1;                                                            // expect: VTX001
        Span<int> mine = stackalloc int[2];
        mine[0] = 1;                                                         // expect: none
        var picked = Xs.Select(_ => Side()).ToList();                        // expect: VTX002
        var mapped = Xs.Select(x => SideTx()).ToList();                      // expect: none
        var filtered = Xs.Where(pred).ToList();                              // expect: VTX002
        var filtered2 = Xs.Where(txPred).ToList();                           // expect: none
        Func<int> m = Side;                                                  // expect: none
        m();                                                                 // expect: VTX002
        Tx.Defer(Console.WriteLine);                                         // expect: none
    }

    public void LambdaAttributes()
    {
        Action a = [Transacts] () => Plain = 1;                              // expect: VTX001
        Action b = [Transacts] () => Hp = 1;                                 // expect: none
        [Transacts] void Local() { Plain = 2; }                              // expect: VTX001
        [Transacts] async Task LocalAsync() { await Task.Yield(); }          // expect: VTX003, VTX002
        Local(); a(); b(); _ = LocalAsync();
    }
}

public partial class Outer2<T>
{
    public partial class Inner2 { [TxField] private T? _value; }             // expect: none
}

public partial class Visibility
{
    private sealed class Secret { public int X; }
    [TxField] private Secret? _hidden;                                       // expect: none
    [TxField] private Secret? _secret;                                       // expect: VTX011
    [TxField] private int _public;                                           // expect: none
    [TxField] private int @class;                                            // expect: none
    [TxField("Bad Name")] private int _bad;                                  // expect: VTX011
}

public class NonPartialBase { [TxField] private int _x; }                    // expect: VTX011
public partial class DerivedFromNonPartial : NonPartialBase { [TxField] private int _y; } // expect: none

public static class CaseRun
{
    public static string Run()
    {
        var v = new Visibility();
        Tx.Try(() => v.Public = 3);
        if (v.Public != 3) return "Public";
        var o = new Outer2<string>.Inner2();
        Tx.Try(() => { o.Value = "a"; Tx.Fail(); });
        if (o.Value != null) return "Outer2<T>.Inner2 回滚";
        var d = new DerivedFromNonPartial();
        if (!Tx.Try(() => d.Y = 2) || d.Y != 2) return "DerivedFromNonPartial";
        return "ok";
    }
}
