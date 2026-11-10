using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using VTX;

public partial class Unit : Node3D
{
    [TxField] private int _hp;

    [Transacts]
    public IEnumerable<int> Lazy() { Hp = 1; yield return 1; }                  // expect: VTX003

    [Transacts]
    public async Task Bad() { await Task.Yield(); }                            // expect: VTX003, VTX002

    [Transacts]
    public void Rules(IAsyncEnumerable<int> xs, TxRandom rng)
    {
        var n = new Node();                                                    // expect: VTX002
        var r = new RefCounted();                                              // expect: none
        Node implicitNew = new();                                              // expect: VTX002
        Hp = rng.NextInt(10);                                                  // expect: none
        float f = GD.Randf();                                                  // expect: VTX007
        ulong t = Time.GetTicksMsec();                                         // expect: VTX007
        long ts = Stopwatch.GetTimestamp();                                    // expect: VTX007
        var now = DateTime.Today;                                              // expect: VTX007
        var rnd = new RandomNumberGenerator().Randf();                         // expect: VTX007
        var v = new Vector3(1, 2, 3);                                          // expect: none
        TxGodot.SetPosition(this, v);                                          // expect: none
        Position = v;                                                          // expect: VTX001
        GD.Print("x");                                                         // expect: VTX002
        TxGodot.Log("ok");                                                     // expect: none
        TxTask.Sleep(1);                                                       // expect: VTX003
        using var guard = TxTask.Defer(() => { });                             // expect: VTX002
    }

    public async Task AsyncBodies(IAsyncEnumerable<int> xs, IAsyncDisposable d)
    {
        Tx.Try(async () => { await Task.Yield(); });                           // expect: VTX003, VTX002
        Tx.Try(async () => { await foreach (var x in xs) { } });               // expect: VTX003
        Tx.Try(async () => { await using var _ = d; });                        // expect: VTX003
        await Task.Yield();                                                    // expect: none
    }

    [Suspends]
    public async Task Waits(CancellationToken ct)
    {
        await ToSignal(this, "ready");                                         // expect: VTX005
        await TxTask.WaitSignal(this, "ready", ct);                            // expect: none
    }

    public SignalAwaiter ToSignal(GodotObject source, StringName signal) => new();
    public sealed class SignalAwaiter { public System.Runtime.CompilerServices.TaskAwaiter GetAwaiter() => Task.CompletedTask.GetAwaiter(); }

    [Pure]
    public int PureTime() => (int)Time.GetTicksMsec();                          // expect: VTX007
}
