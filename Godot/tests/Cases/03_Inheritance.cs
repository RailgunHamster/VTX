using VTX;

public interface IRule
{
    [Transacts] void Apply();
}

public class BaseUnit
{
    public int Plain;
    [Transacts] public virtual void Apply() { }
    [Pure] public virtual int Score() => 1;
    [Transacts] public int Prop { get => Plain; set => Plain = value; }   // expect: VTX001
}

public class Derived : BaseUnit, IRule
{
    public override void Apply() { Plain = 42; }                         // expect: VTX001
    public override int Score() { Plain++; return Plain; }              // expect: VTX010
}

public class Impl : IRule
{
    public int Plain;
    public void Apply() { Plain = 1; }                                   // expect: VTX001
}

public class Explicit : IRule
{
    public int Plain;
    void IRule.Apply() { Plain = 1; }                                    // expect: VTX001
}

public static class Caller
{
    public static void Run(BaseUnit b, IRule r, Derived d)
    {
        Tx.Try(() => b.Apply());                                         // expect: none
        Tx.Try(() => r.Apply());                                         // expect: none
        Tx.Try(() => d.Apply());                                         // expect: none
        Tx.Try(() => d.Prop = 3);                                        // expect: none
        Tx.Try(() => d.Score());                                         // expect: none
    }
}
