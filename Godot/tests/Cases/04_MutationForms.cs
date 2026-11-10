using System;
using System.Collections.Generic;
using VTX;

public sealed class Bag { public int Count; public int[] Slots = new int[2]; public Bag? Inner; }

public partial class Forms
{
    [TxField] private int _value;
    public int Plain;
    public (int A, int B) Pair;
    public Bag Shared = new();

    [Pure] static bool Parse(string s, out int v) { v = s.Length; return true; }   // expect: none

    [Transacts]
    public void Run(ref int byRef, out int result, Bag param)
    {
        result = 1;                                          // expect: none
        byRef = 2;                                           // expect: VTX001
        Parse("x", out Plain);                               // expect: VTX001
        Parse("x", out var fresh);                           // expect: none
        Parse("x", out _);                                   // expect: none
        Parse("x", out _value);                              // expect: VTX004
        (Plain, Value) = (1, 2);                             // expect: VTX001
        (var a, var b) = (1, 2);                             // expect: none
        int x = 0, y = 0; (x, y) = (y, x);                   // expect: none
        var bag = new Bag { Count = 3, Inner = Shared };     // expect: none
        bag.Count = 5;                                       // expect: none
        bag.Slots[0] = 1;                                    // expect: VTX001
        bag.Inner!.Count = 9;                                // expect: VTX001
        var arr = new int[3];
        arr[0] = 1;                                          // expect: none
        var reused = new Bag();
        reused = Shared;
        reused.Count = 1;                                    // expect: VTX001
        param.Count = 1;                                     // expect: VTX001
        var copy = Pair with { A = 2 };                      // expect: none
        Value = fresh + a + b + copy.A + x;                  // expect: none
    }
}
