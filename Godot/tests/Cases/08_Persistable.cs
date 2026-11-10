using VTX;

[TxPersistable]
[TxBaselineField("Gold", "int")]
[TxBaselineField("Name", "string")]
[TxBaselineField("Lost", "int")]
public partial class SaveData                                    // expect: VTX009
{
    [TxField] private int _gold;
    [TxField] private long _name;
}

[TxPersistable]
[TxBaselineField("Gold", "int")]
public partial class GoodSave                                    // expect: none
{
    [TxField] private int _gold;
    [TxField] private int _added;
}

[TxBaselineField("Gold", "string")]
public partial class NotPersistable { [TxField] private int _gold; } // expect: none
