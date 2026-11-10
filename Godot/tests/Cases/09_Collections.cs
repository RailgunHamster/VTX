using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using VTX;

public partial class C6
{
    [TxField] private int[] _a = null!;                          // expect: VTX006
    [TxField] private List<int> _b = null!;                      // expect: VTX006
    [TxField] private Godot.Collections.Array _c = null!;        // expect: VTX006
    [TxField] private ImmutableList<int> _d = null!;             // expect: none
    [TxField] private TxList<int> _e = new();                    // expect: none
    [TxField] private Collection<int> _f = null!;                // expect: VTX006
    [TxField] private string _g = "";                            // expect: none
}
