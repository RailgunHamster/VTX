using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using VTX;

public sealed class Board
{
    public int[] Cells = new int[9];
}

public partial class Mover : Node3D
{
    [TxField] private int _gold;

    public async Task Plan(CancellationToken ct)
    {
        var start = Position;                                                        // expect: none（主线程）
        int gold = Gold;                                                             // expect: none
        var path = await Task.Run(() => start.X + gold, ct);                         // expect: none（只用拷贝出来的数据）
        Position = new Vector3(path, 0, 0);                                          // expect: none（await 之后回到主线程）

        await Task.Run(() =>
        {
            var p = Position;                                                        // expect: VTX013
            Gold = 5;                                                                // expect: VTX013
            Tx.Try(() => { });                                                       // expect: VTX013
            TxTask.Sleep(1).Forget();                                                // expect: VTX013
            TxGodot.SetPosition(this, default);                                      // expect: VTX013
            GetParent();                                                             // expect: VTX013
            CallDeferred("Foo");                                                     // expect: none（线程安全的投递）
            Callable.From(() => Position = default).CallDeferred();                  // expect: none（回到主线程执行）
            var fresh = new Node3D();
            fresh.Position = default;                                                // expect: none（新建的、还没进场景树）
            bool active = Tx.IsActive;                                               // expect: none
        }, ct);

        Parallel.For(0, 4, i => { var r = Rotation; });                              // expect: VTX013
        new Thread(() => GetParent()).Start();                                       // expect: VTX013
        ThreadPool.QueueUserWorkItem(_ => Tx.Simulate(() => { }));                   // expect: VTX013
        WorkerThreadPool.AddTask(Callable.From(() => GetParent()));                  // expect: VTX013
        var list = new List<int> { 1 };
        list.ForEach(x => Position = default);                                       // expect: none（同步执行，在主线程）
    }

    public Task<int> Search(Board copy) => Task.Run(() =>
    {
        using var scope = Tx.EnterWorkerScope();
        var moves = new List<int>();
        Tx.WhereCan(new[] { 0, 1, 2 }, c => copy.Cells[c] == 0, moves);             // expect: none（在 Worker 作用域里推演独立数据）
        TxTask.NextFrame().Forget();                                                 // expect: VTX013（Worker 作用域也不能用 TxTask）
        return moves.Count;
    });
}
