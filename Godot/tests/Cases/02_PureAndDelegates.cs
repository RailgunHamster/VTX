using System;
using System.Collections.Generic;
using VTX;

public partial class Hero
{
    [TxField] private int _hp = 10;
    public int Raw;
    public event Action? Changed;
    private Action? _stored;

    [Pure] public int Peek() { Raw++; return Raw; }                 // expect: VTX010
    [Pure] public int Twice(int x) { int y = x; y *= 2; return y; }  // expect: none
    [Pure] public bool CanHit() => Tx.Simulate(() => Hit(3));       // expect: none
    [Pure] public void BadPure() { Hit(1); }                        // expect: VTX010
    [Pure] public void BadPure2() { Tx.Try(() => Hit(1)); }         // expect: VTX010
    [Pure] public void BadPure3() { Hp = 3; }                       // expect: VTX010
    [Pure] public int Ok(List<int> xs) => xs.Count + Math.Abs(-1) + Twice(2); // expect: none

    [Decides] public void Hit(int dmg) { Hp -= dmg; Tx.Check(Hp >= 0); } // expect: none

    public void MutateSelf() { Raw = 1; }
    [Transacts] public void MutateSelfTx() { Hp = 1; }

    [Transacts]
    public static void ForEach<T>(IEnumerable<T> xs, [Transacts] Action<T> f)
    {
        foreach (var x in xs) f(x);                                 // expect: none
    }

    [Transacts]
    public static void CallBad(Action cb)
    {
        cb();                                                        // expect: VTX002
        cb.Invoke();                                                 // expect: VTX002
        cb?.Invoke();                                                // expect: VTX002
    }

    public void Run(Action cb)
    {
        Tx.Try(MutateSelf);                                          // expect: VTX002
        Tx.Try(MutateSelfTx);                                        // expect: none
        Action a = () => Raw = 2;
        Tx.Try(a);                                                   // expect: VTX002
        Tx.Try(cb);                                                  // expect: VTX002
        Tx.Try(() => cb());                                          // expect: VTX002
        Tx.Try(() => { Action local = () => Hp = 1; local(); });     // expect: none
        Tx.Try(() => { Action local = () => Raw = 1; local(); });    // expect: VTX001
        Tx.Try(() => Peek());                                        // expect: none
        ForEach(new[] { 1, 2 }, x => Hp -= x);                       // expect: none
        ForEach(new[] { 1, 2 }, x => Raw -= x);                      // expect: VTX001
        Tx.Try(() => Changed += () => { });                          // expect: VTX001
        Tx.Try(() => _stored = null);                                // expect: VTX001
        Tx.Try((Action)null!);                                       // expect: none
    }
}
