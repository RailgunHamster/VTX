using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using VTX;

public partial class Hero : Node3D
{
    [TxField] private int _hp;

    [Suspends]
    public async Task Cast(CancellationToken ct)
    {
        await TxTask.Sleep(1);                                                      // expect: VTX005
        await TxTask.Sleep(1, ct);                                                  // expect: none
        await TxTask.NextFrame(CancellationToken.None);                             // expect: none
        await Task.Delay(1000, ct);                                                 // expect: VTX005
        await TxTask.Race(c => TxTask.Sleep(1), c => TxTask.Sleep(2, c));           // expect: VTX005, VTX005
        await TxTask.Race(ct, c => TxTask.Sleep(1, c), c => TxTask.NextFrame(c));   // expect: none
        await TxTask.WaitUntil(() => Hp > 3, ct);                                   // expect: none
        await Channel(ct);                                                          // expect: none
        await Channel();                                                            // expect: VTX005
        Channel(ct);                                                                // expect: VTX012
        _ = Channel(ct);                                                            // expect: VTX012
        Channel(ct).Forget();                                                       // expect: none
        TxTask.Sleep(1, ct);                                                        // expect: VTX012
    }

    [Suspends]
    public async Task Channel(CancellationToken ct = default) { await TxTask.NextFrame(ct); }

    [Suspends]
    public async Task NoToken()
    {
        await TxTask.Sleep(1);                                                      // expect: none
        await TxTask.Sleep(1, TxTask.LifetimeOf(this));                             // expect: none
    }

    [Suspends]
    public async void Bad() { await TxTask.NextFrame(); }                          // expect: VTX012

    public async void _Ready()
    {
        await Cast(TxTask.LifetimeOf(this));                                        // expect: none
        Cast(default);                                                              // expect: VTX012
        await Task.Delay(10);                                                       // expect: none
    }

}

