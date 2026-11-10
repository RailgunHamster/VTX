using System;
using System.Collections.Generic;
using System.Linq;
using VTX;

public partial class Shop
{
    [TxField] private int _gold = 100;
    public int Plain;
    public static int Counter;
    public TxList<int> Items { [Pure] get; } = new();
    public List<int> RawList = new();
    public int[] RawArray = new int[4];

    [Decides]
    public void Buy(int price)
    {
        Gold -= price;                         // expect: none
        Tx.Check(Gold >= 0, "金币不足");        // expect: none
        Items.Add(price);                       // expect: none
        Plain = 1;                              // expect: VTX001
        Counter++;                              // expect: VTX001
        RawList.Add(1);                         // expect: VTX002
        RawArray[0] = 1;                        // expect: VTX001
        Console.WriteLine("x");                 // expect: VTX002
        int local = 0; local++;                 // expect: none
        var max = Math.Max(1, 2);               // expect: none
        var any = Items.Any(i => i > 3);        // expect: none
        bool has = RawList.Contains(3);         // expect: none
        string s = $"{Gold}{max}{any}{has}{s0()}"; // expect: none
        float clamped = Godot.Mathf.Clamp(1f, 0f, 2f);       // expect: none
        bool same = ReferenceEquals(Items, Items);         // expect: none
        var span = TimeSpan.FromSeconds(3).TotalSeconds;   // expect: none
        var later = DateTime.Today.AddDays(1);             // expect: VTX007
        static string s0() => "";
    }

    public void TopLevel(int captured)
    {
        int outside = 0;
        Tx.Try(() => outside++);                // expect: VTX001
        Tx.Try(() => captured = 1);             // expect: VTX001
        Tx.Try(() => Buy(10));                  // expect: none
        Tx.Try(() => { var x = 0; x += 2; });   // expect: none
        Tx.Defer(() => Plain = 2);              // expect: none
        Tx.Try(() => Tx.Defer(() => Plain = 3)); // expect: none
        Tx.Simulate(() => Plain = 4);           // expect: VTX001
        Tx.WhereCan(new[] { 1 }, p => Plain = p); // expect: VTX001
        Tx.FirstOf(new[] { 1 }, p => Buy(p), out _); // expect: none
        Plain = 5;                              // expect: none
        _gold = 1;                              // expect: VTX004
    }

    public Shop()
    {
        _gold = 50;                             // expect: none
    }
}
