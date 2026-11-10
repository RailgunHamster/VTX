#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Godot;

namespace VTX.Tests;

/// <summary>手写的 ITxObject（等价于 [TxField] 生成器的输出），用于运行时测试。</summary>
internal sealed class Wallet : ITxObject
{
    private int _gold;
    public int InvariantCalls;
    public int Gold
    {
        get => _gold;
        set
        {
            if (_gold == value) return;
            if (Tx.IsActive) Tx.Record(this, _gold, static (o, v) => o._gold = v);
            _gold = value;
        }
    }
    public bool ValidateTxInvariants()
    {
        InvariantCalls++;
        return Tx.Require(Gold >= 0, "金币不能为负数");
    }
}

internal static class RuntimeTests
{
    public static void Register()
    {
        T.Add("Tx: 提交与回滚基础语义", () =>
        {
            var list = new TxList<int>();
            var arr = new TxArray<int>(3);
            var dict = new TxDict<string, int>();
            T.True(Tx.Try(() => { list.Add(1); arr[0] = 5; dict["a"] = 1; }), "提交成功");
            T.Eq(1, list.Count, "list"); T.Eq(5, arr[0], "arr"); T.Eq(1, dict["a"], "dict");
            T.True(!Tx.Try(() => { list.Add(2); arr[0] = 6; dict["a"] = 2; dict["b"] = 3; return Tx.Failed("x"); }), "失败");
            T.Eq(1, list.Count, "list 回滚"); T.Eq(5, arr[0], "arr 回滚"); T.Eq(1, dict["a"], "dict 回滚"); T.True(!dict.ContainsKey("b"), "新键回滚");
            T.Eq(0, Tx.Depth, "深度"); T.Eq(0, Tx.UndoLogCount, "日志清空");
        });

        T.Add("Tx: 嵌套事务——内层失败只回滚内层", () =>
        {
            var list = new TxList<int>();
            bool innerOk = true;
            T.True(Tx.Try(() =>
            {
                list.Add(1);
                innerOk = Tx.Try(() => { list.Add(2); return false; });
                list.Add(3);
            }), "外层提交");
            T.True(!innerOk, "内层失败");
            T.Eq("1,3", string.Join(",", list), "内容");
        });

        T.Add("Tx: 嵌套事务——内层成功、外层失败时全部回滚", () =>
        {
            var list = new TxList<int>();
            var deferred = new List<string>();
            T.True(!Tx.Try(() =>
            {
                Tx.Try(() => { list.Add(1); Tx.Defer(() => deferred.Add("inner")); });
                return false;
            }), "外层失败");
            T.Eq(0, list.Count, "内层修改也回滚");
            T.Eq(0, deferred.Count, "内层的 Defer 不执行");
        });

        T.Add("Tx: Simulate 总是回滚且丢弃 Defer，嵌套在 Try 中只回滚自己", () =>
        {
            var list = new TxList<int>();
            var ran = new List<string>();
            T.True(Tx.Simulate(() => { list.Add(1); Tx.Defer(() => ran.Add("sim")); }), "Simulate 成功");
            T.Eq(0, list.Count, "Simulate 回滚"); T.Eq(0, ran.Count, "Simulate 不执行 Defer");
            T.True(Tx.Try(() =>
            {
                list.Add(1);
                Tx.Simulate(() => list.Add(2));
                Tx.Defer(() => ran.Add("outer"));
            }), "外层提交");
            T.Eq("1", string.Join(",", list), "只保留外层修改");
            T.Eq("outer", string.Join(",", ran), "外层 Defer 执行");
        });

        T.Add("Tx: Defer 只在最外层提交时按顺序执行；事务外立即执行", () =>
        {
            var log = new List<string>();
            Tx.Try(() =>
            {
                Tx.Defer(() => log.Add("a"));
                Tx.Try(() => Tx.Defer(() => log.Add("b")));
                T.Eq(0, log.Count, "提交前不执行");
            });
            T.Eq("a,b", string.Join(",", log), "顺序");
            Tx.Defer(() => log.Add("c"));
            T.Eq("a,b,c", string.Join(",", log), "事务外立即执行");
        });

        T.Add("Tx: Defer 动作抛异常不会破坏事务状态（回归 #1）", () =>
        {
            var list = new TxList<int>();
            var log = new List<string>();
            T.Throws<InvalidOperationException>(() => Tx.Try(() =>
            {
                list.Add(1);
                Tx.Defer(() => throw new InvalidOperationException("ui callback"));
                Tx.Defer(() => log.Add("second still runs"));
            }), "异常在提交后抛出");
            T.Eq(1, list.Count, "事务已提交");
            T.Eq(1, log.Count, "后续 Defer 仍执行");
            T.Eq(0, Tx.Depth, "深度恢复为 0");
            T.True(!Tx.IsActive, "不在事务中");
            var gold = new TxList<int>(); gold.Add(100);
            T.True(!Tx.Try(() => { gold[0] = 0; return false; }), "之后的事务失败");
            T.Eq(100, gold[0], "之后的事务能正确回滚");

            var agg = T.Throws<AggregateException>(() => Tx.Try(() =>
            {
                Tx.Defer(() => throw new InvalidOperationException("1"));
                Tx.Defer(() => throw new ArgumentException("2"));
            }), "多个异常聚合");
            if (agg != null) T.Eq(2, agg.InnerExceptions.Count, "聚合数量");
            T.Eq(0, Tx.Depth, "深度");
        });

        T.Add("Tx: Defer 动作内可以开启新的顶层事务", () =>
        {
            var list = new TxList<int>();
            Tx.Try(() => Tx.Defer(() =>
            {
                T.True(!Tx.IsActive, "延迟动作在事务外执行");
                T.True(!Tx.Try(() => { list.Add(9); return false; }), "新事务可失败");
                T.True(Tx.Try(() => list.Add(1)), "新事务可提交");
            }));
            T.Eq("1", string.Join(",", list), "结果");
            T.Eq(0, Tx.Depth, "深度");
        });

        T.Add("Tx: body 抛普通异常时回滚并原样抛出", () =>
        {
            var list = new TxList<int>();
            T.Throws<FormatException>(() => Tx.Try(() => { list.Add(1); throw new FormatException(); }), "原样抛出");
            T.Eq(0, list.Count, "已回滚");
            T.Eq(0, Tx.Depth, "深度");
            T.Throws<FormatException>(() => Tx.Simulate(() => { list.Add(1); throw new FormatException(); }), "Simulate 也原样抛出");
            T.Eq(0, list.Count, "Simulate 已回滚");
        });

        T.Add("Tx 容器: 越界是 bug：事务内外都抛标准异常，事务内先回滚", () =>
        {
            var list = new TxList<int>(); list.Add(1);
            var arr = new TxArray<int>(1);
            var dict = new TxDict<string, int>();
            T.Throws<ArgumentOutOfRangeException>(() => list[5] = 1, "TxList set");
            T.Throws<ArgumentOutOfRangeException>(() => list.Insert(5, 1), "TxList Insert");
            T.Throws<ArgumentOutOfRangeException>(() => list.RemoveAt(5), "TxList RemoveAt");
            T.Throws<ArgumentOutOfRangeException>(() => arr[3] = 1, "TxArray set");
            T.Throws<KeyNotFoundException>(() => _ = dict["none"], "TxDict get");
            T.Throws<ArgumentOutOfRangeException>(() => Tx.Try(() => { list.Add(2); list[9] = 1; }), "事务内越界抛异常");
            T.Eq(1, list.Count, "并回滚");
            T.Throws<KeyNotFoundException>(() => Tx.Try(() => { list.Add(3); _ = dict["none"]; }), "事务内缺键抛异常");
            T.Eq(1, list.Count, "并回滚");
            T.True(!Tx.Try(list, static l => l.TryGetAt(5, out _)), "TryGetAt 越界返回 false");
            T.Eq("TxList 下标越界", Tx.LastFailReason, "TryGetAt 原因");
            T.True(Tx.Try(list, static l => l.TryGetAt(0, out int v) && v == 1), "TryGetAt 成功");
            T.True(!Tx.Try(arr, static a => a.TryGetAt(-1, out _)), "TxArray.TryGetAt 越界");
            T.Eq(0, Tx.Depth, "深度");
        });

        T.Add("Tx 容器: Clear/Remove/Insert 回滚后内容与顺序一致", () =>
        {
            var list = new TxList<string>();
            foreach (var s in new[] { "a", "b", "c", "d" }) list.Add(s);
            T.True(!Tx.Try(() => { list.Remove("b"); list.Insert(0, "x"); list.RemoveAt(3); list.Clear(); list.Add("z"); return false; }), "失败");
            T.Eq("a,b,c,d", string.Join(",", list), "列表");
            var dict = new TxDict<string, int>();
            dict.Set("A", 1); dict.Set("B", 2); dict.Set("C", 3);
            Tx.Simulate(() => { dict.Remove("A"); dict.Set("D", 4); dict.Remove("C"); dict.Set("A", 9); });
            T.Eq("A,B,C", string.Join(",", Keys(dict)), "字典枚举顺序");
            T.Eq(1, dict["A"], "字典值");
        });

        T.Add("Tx: 失败原因（LastFailReason / out failReason）", () =>
        {
            int gold = 10;
            T.True(!Tx.Try(gold, static gold => Tx.Require(gold > 20, "金币不足"), out string? reason), "失败");
            T.Eq("金币不足 (gold > 20)", reason, "带原因与表达式");
            T.True(!Tx.Try(() => false), "Func<bool> 返回 false");
            T.Eq("Transaction body returned false", Tx.LastFailReason, "默认原因");
            // 嵌套：先有一个被吞掉的内层失败，之后外层 body 返回 false，不应沿用旧原因
            T.True(!Tx.Try(() =>
            {
                Tx.Try(() => Tx.Failed("内层"));
                return Tx.Try(() => false);
            }), "外层失败");
            T.Eq("Transaction body returned false", Tx.LastFailReason, "不沿用过期原因");
            T.True(Tx.Try(() => { }, out string? none), "成功");
            T.Eq<string?>(null, none, "成功时 failReason 为 null");
        });

        T.Add("Tx: [TxInvariant] 校验与 Touch 去重性能（回归 #7）", () =>
        {
            var w = new Wallet { Gold = 10 };
            T.True(!Tx.Try(() => { w.Gold = -5; }, out string? reason), "违反不变量回滚");
            T.Eq(10, w.Gold, "值回滚");
            T.True(reason!.Contains("金币不能为负数"), "原因");
            T.True(!Tx.Simulate(() => { w.Gold = -1; }), "Simulate 也校验");
            T.True(Tx.Try(() => { w.Gold = -1; w.Gold = 3; }), "提交前恢复合法即可");
            var objs = Enumerable.Range(0, 20000).Select(_ => new Wallet()).ToArray();
            var sw = Stopwatch.StartNew();
            Tx.Try(() => { foreach (var o in objs) { o.Gold = 1; o.Gold = 2; } });
            sw.Stop();
            T.True(sw.ElapsedMilliseconds < 200, $"2 万个对象的事务耗时 {sw.ElapsedMilliseconds} ms");
            T.Eq(1, objs[0].InvariantCalls, "每个对象只校验一次");
            T.Eq(0, Tx.UndoLogCount, "提交后清空");
            // 回滚后 touched 集合也要清理：再次修改仍会被校验
            T.True(!Tx.Try(() => { w.Gold = 5; return false; }), "失败");
            T.True(!Tx.Try(() => { w.Gold = -9; }), "再次触碰仍校验");
        });

        T.Add("Tx: Undo 步骤自身抛异常时继续恢复其余步骤", () =>
        {
            var list = new TxList<int>();
            var ex = T.Throws<TxRollbackException>(() => Tx.Try(() =>
            {
                list.Add(1);
                Tx.Record(list, 0, static (_, _) => throw new InvalidOperationException("bad undo"));
                list.Add(2);
                return false;
            }), "报告回滚异常");
            T.Eq(0, list.Count, "其余步骤仍恢复");
            T.Eq(0, Tx.Depth, "深度");
            T.Eq(0, Tx.UndoLogCount, "日志清空");
        });

        T.Add("Tx: FirstOf / WhereCan", () =>
        {
            var w = new Wallet { Gold = 50 };
            var offers = new[] { 80, 60, 30, 10 };
            // 不写 Require：靠 Wallet 的不变量（Gold >= 0）在提交前拦下
            var can = new List<int>();
            Tx.WhereCan(offers, p => { w.Gold -= p; return true; }, can);
            T.Eq("30,10", string.Join(",", can), "WhereCan");
            T.Eq(50, w.Gold, "WhereCan 不改状态");
            T.True(Tx.FirstOf(offers, p => { w.Gold -= p; return true; }, out int chosen), "FirstOf");
            T.Eq(30, chosen, "选中第一个合法项"); T.Eq(20, w.Gold, "并提交");
            T.True(!Tx.FirstOf(new[] { 100 }, p => { w.Gold -= p; return true; }, out _), "都失败");
            T.Eq(20, w.Gold, "都失败时不变");
        });

        T.Add("TxRandom: Simulate 预览与 Try 一致，失败回滚种子", () =>
        {
            var rng = new TxRandom(42);
            ulong s0 = rng.State;
            var preview = new int[1];
            Tx.Simulate(() => { preview[0] = rng.NextInt(100); });
            T.Eq(s0, rng.State, "Simulate 后种子回退");
            int actual = 0;
            Tx.Try(() => { actual = rng.NextInt(100); });
            T.Eq(preview[0], actual, "结果一致");
            T.True(!Tx.Try(rng, static r => r.CheckChance(0f, "不可能")), "CheckChance 未命中返回 false");
            T.Eq("不可能", Tx.LastFailReason, "CheckChance 原因");
            ulong s1 = rng.State;
            Tx.Try(() => { rng.NextUInt(); rng.NextUInt(); return false; });
            T.Eq(s1, rng.State, "失败回滚");
            T.True(rng.NextInt(-5, 5) is >= -5 and < 5, "范围");
            T.Eq(int.MinValue <= rng.NextInt(int.MinValue, int.MaxValue), true, "大范围不溢出");
            rng.State = 0; T.True(rng.State != 0, "0 种子被规范化");
        });

        T.Add("Tx: 事务状态按线程隔离", () =>
        {
            bool otherActive = true;
            Tx.Try(() =>
            {
                var th = new Thread(() => otherActive = Tx.IsActive);
                th.Start(); th.Join();
            });
            T.True(!otherActive, "其它线程不在事务中");
        });

        T.Add("Tx: 不抛异常的失败（Require / Failed）同样整体回滚", () =>
        {
            var w = new Wallet { Gold = 10 };
            var log = new TxList<string>();
            static bool Buy(Wallet w, TxList<string> log, int price)
            {
                log.Add("buy " + price);
                if (!Tx.Require(w.Gold >= price, "金币不足")) return false;
                w.Gold -= price;
                return true;
            }
            T.True(Tx.Try(() => Buy(w, log, 3) && Buy(w, log, 4)), "两次都买得起");
            T.Eq(3, w.Gold, "扣款");
            T.True(!Tx.Try((w, log), static s => Buy(s.w, s.log, 2) && Buy(s.w, s.log, 5), out var why), "第二次买不起");
            T.Eq(3, w.Gold, "第一次的扣款也回滚");
            T.Eq(2, log.Count, "日志回滚");
            T.Eq("金币不足 (w.Gold >= price)", why, "原因");
            T.True(!Tx.Try(() => Tx.Failed("没有目标")), "Failed");
            T.Eq("没有目标", Tx.LastFailReason, "Failed 原因");
            T.True(!Tx.Try(() => false), "直接返回 false");
            T.Eq("Transaction body returned false", Tx.LastFailReason, "默认原因");
            T.True(!Tx.Try(() => Tx.Require(1 > 2)), "无原因的 Require");
            T.Eq("1 > 2", Tx.LastFailReason, "条件表达式");
            T.True(Tx.Require(true), "事务外 Require(true)");
            T.True(!Tx.Require(false), "事务外 Require(false) 只返回 false 不抛异常");
        });

        T.Add("Tx: 失败原因的格式", () =>
        {
            string? s = null;
            T.True(!Tx.Try(() => Tx.Require(1 > 2, "r")), "Require"); T.Eq("r (1 > 2)", Tx.LastFailReason, "带原因");
            T.True(!Tx.Try(() => Tx.Require(1 > 2)), "Require"); T.Eq("1 > 2", Tx.LastFailReason, "无原因时用表达式");
            T.True(!Tx.Try(() => s is not { } v ? Tx.Failed("没有名字") : v.Length > 0), "判空"); T.Eq("没有名字", Tx.LastFailReason, "判空原因");
            T.True(!Tx.Try(() => Tx.Failed()), "Failed"); T.Eq("Tx.Failed()", Tx.LastFailReason, "Failed 默认");
            var w = new Wallet { Gold = 1 };
            T.True(!Tx.Try(() => { w.Gold = -1; }), "不变量");
            T.Eq("金币不能为负数 (Gold >= 0)", Tx.LastFailReason, "不变量原因");
            T.True(Tx.Try(() => { }), "成功"); T.Eq(null, Tx.LastFailReason, "顶层成功后清空");
        });

        T.Add("Tx: 零分配重载——Try/Simulate(state)、保存点、FirstOf/WhereCan(Func)", () =>
        {
            var w = new Wallet { Gold = 10 };
            // 事务内的 Try(state) 是保存点：A 失败只回滚 A，再试 B（Verse 的 A[] or B[]）
            T.True(Tx.Try(w, static w =>
            {
                w.Gold -= 1;
                if (!Tx.Try(w, static w => { w.Gold -= 100; return Tx.Require(w.Gold >= 0); }))
                    w.Gold -= 2;
                return true;
            }), "外层提交");
            T.Eq(7, w.Gold, "A 回滚、B 生效");
            T.True(Tx.Simulate(w, static w => { w.Gold = 0; return true; }), "Simulate 成功");
            T.Eq(7, w.Gold, "Simulate 回滚");
            T.True(!Tx.Simulate(w, static w => Tx.Failed("x"), out var r) && r == "x", "Simulate 失败原因");

            int[] cands = { 9, 8, 5, 3 };
            T.True(Tx.FirstOf(cands, c => { w.Gold -= c; return Tx.Require(w.Gold >= 0); }, out int chosen), "FirstOf");
            T.Eq(5, chosen, "第一个买得起的"); T.Eq(2, w.Gold, "只保留成功候选的修改");
            var results = new List<int> { -1 };
            int added = Tx.WhereCan(cands, c => { w.Gold -= c; return w.Gold >= 0; }, results);
            T.Eq(0, added, "都买不起"); T.Eq(1, results.Count, "不清空结果列表");
            w.Gold = 100;
            T.Eq(4, Tx.WhereCan(cands, c => { w.Gold -= c; return w.Gold >= 0; }, results), "都买得起");
            T.Eq(100, w.Gold, "WhereCan 不留修改");
        });

        T.Add("线程规则：后台线程不能开事务，除非 EnterWorkerScope；Worker 里不能改 Godot 对象", () =>
        {
            Exception? noScope = null, godotObj = null;
            bool scopedOk = false;
            int scopedGold = -1;
            var th = new Thread(() =>
            {
                try { Tx.Try(() => { }); } catch (Exception e) { noScope = e; }
                using (var scope = Tx.EnterWorkerScope())
                {
                    var w = new Wallet { Gold = 5 };
                    scopedOk = !Tx.Try(() => { w.Gold = 1; return false; });
                    scopedGold = w.Gold;
                    var n = new Node3D();
                    try { Tx.Try(() => TxGodot.SetPosition(n, new Vector3(1, 0, 0))); } catch (Exception e) { godotObj = e; }
                }
                // 作用域结束后又不允许了
                try { Tx.Try(() => { }); noScope = noScope is null ? new Exception("作用域结束后仍允许") : noScope; } catch (InvalidOperationException) { }
            });
            th.Start(); th.Join();
            T.True(noScope is InvalidOperationException && noScope.Message.Contains("主线程"), "无作用域时报错");
            T.True(scopedOk && scopedGold == 5, "作用域内正常回滚");
            T.True(godotObj is InvalidOperationException && godotObj.Message.Contains("Godot 对象"), "Worker 事务里改 Godot 对象报错");
            T.Eq(0, Tx.Depth, "主线程深度不受影响");
            using (Tx.EnterWorkerScope()) { T.True(Tx.Try(() => { }), "主线程上 EnterWorkerScope 是空操作"); }
        });

        T.Add("TxGodot: 属性回滚、延迟节点树操作与信号", () =>
        {
            var n3 = new Node3D(); var n2 = new Node2D(); var parent = new Node(); var child = new Node();
            var emitter = new Node();
            T.True(!Tx.Try(() =>
            {
                TxGodot.SetPosition(n3, new Vector3(1, 2, 3));
                TxGodot.SetVisible(n3, false);
                TxGodot.SetVisible((CanvasItem)n2, false);
                TxGodot.SetRotation2D(n2, 1.5f);
                TxGodot.SetScale2D(n2, new Vector2(2, 2));
                TxGodot.SetProperty(n3, "hp", 5);
                TxGodot.AddChild(parent, child);
                T.True(child.GetParent() == null, "事务内节点树操作不可见");
                TxGodot.EmitSignal(emitter, "hit", 1);
                TxGodot.QueueFree(child);
                TxGodot.Log("never");
                return false;
            }), "失败");
            T.True(n3.Position == default, "位置回滚"); T.True(n3.Visible, "3D 可见性回滚"); T.True(n2.Visible, "2D 可见性回滚");
            T.Eq(0f, n2.Rotation, "2D 旋转回滚"); T.True(n2.Scale == new Vector2(1, 1), "2D 缩放回滚");
            T.Eq(null, n3.Get("hp").Obj, "SetProperty 回滚");
            T.Eq(0, parent.Children.Count, "AddChild 未执行"); T.Eq(0, emitter.Emitted.Count, "信号未发出");
            T.True(!child.QueuedForDeletion, "未释放"); T.True(!GD.Printed.Contains("never"), "未打印");

            var args = new Variant[] { 1 };
            T.True(Tx.Try(() =>
            {
                TxGodot.AddChild(parent, child);
                TxGodot.EmitSignal(emitter, "hit", args);
                args[0] = 99; // 调用后修改数组不影响已登记的信号参数
            }), "提交");
            T.Eq(1, parent.Children.Count, "AddChild 提交后执行");
            T.Eq(1, emitter.Emitted.Count, "信号提交后发出");
            T.Eq(1, (int)emitter.Emitted[0].Item2[0].Obj!, "参数已拷贝");
        });

        T.Add("TxGodot: 回滚时对象已释放则跳过恢复", () =>
        {
            var n = new Node3D();
            T.True(!Tx.Try(() =>
            {
                TxGodot.SetPosition(n, new Vector3(1, 0, 0));
                n.Free();
                return false;
            }), "失败且不抛异常");
            T.Eq(0, Tx.Depth, "深度");
        });
    }

    private static IEnumerable<string> Keys(TxDict<string, int> d)
    {
        foreach (var kv in d) yield return kv.Key;
    }
}
