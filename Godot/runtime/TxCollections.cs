#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace VTX;

/// <summary>
/// 支持元素级增量事务回滚的定长数组容器（替代原生 T[]）。
/// 修改单个元素时只记录 (index, oldValue) 的 O(1) 增量日志，绝不拷贝整个数组。
/// </summary>
public sealed class TxArray<T> : IReadOnlyList<T>
{
    private readonly T[] _items;

    public TxArray(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _items = new T[length];
    }

    public int Length
    {
        [Pure] get => _items.Length;
    }

    int IReadOnlyCollection<T>.Count => _items.Length;

    public T this[int index]
    {
        [Pure]
        get => _items[index];

        [Transacts]
        set
        {
            if ((uint)index >= (uint)_items.Length)
            {
                Tx.FailOrThrow($"TxArray 下标越界: {index} (Length={_items.Length})",
                    new ArgumentOutOfRangeException(nameof(index), index, "Index was out of range."));
            }
            T prev = _items[index];
            if (!EqualityComparer<T>.Default.Equals(prev, value))
            {
                if (Tx.IsActive)
                {
                    Tx.Record(this, index, prev, static (self, idx, oldVal) => self._items[idx] = oldVal);
                }
                _items[index] = value;
            }
        }
    }

    /// <summary>可失败下标读取（越界时失败回滚；事务外越界抛 <see cref="TxFailedException"/>）。</summary>
    [Decides]
    public T At(int index)
    {
        Tx.Check((uint)index < (uint)_items.Length, "TxArray 下标越界");
        return _items[index];
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}

/// <summary>
/// 支持元素级增量事务回滚的动态列表容器（替代原生 List&lt;T&gt;）。
/// </summary>
public sealed class TxList<T> : IReadOnlyList<T>
{
    private readonly List<T> _items = new();

    public int Count
    {
        [Pure] get => _items.Count;
    }

    public T this[int index]
    {
        [Pure]
        get => _items[index];

        [Transacts]
        set
        {
            if ((uint)index >= (uint)_items.Count)
            {
                Tx.FailOrThrow($"TxList 下标越界: {index} (Count={_items.Count})",
                    new ArgumentOutOfRangeException(nameof(index), index, "Index was out of range."));
            }
            T prev = _items[index];
            if (!EqualityComparer<T>.Default.Equals(prev, value))
            {
                if (Tx.IsActive)
                {
                    Tx.Record(this, index, prev, static (self, idx, oldVal) => self._items[idx] = oldVal);
                }
                _items[index] = value;
            }
        }
    }

    /// <summary>
    /// 可失败下标读取（越界时自动触发事务回滚，无需手写 if 边界检查）。
    /// </summary>
    [Decides]
    public T At(int index)
    {
        Tx.Check((uint)index < (uint)_items.Count, "TxList 下标越界");
        return _items[index];
    }

    [Transacts]
    public void Add(T item)
    {
        int index = _items.Count;
        _items.Add(item);
        if (Tx.IsActive)
        {
            Tx.Record(this, index, static (self, idx) => self._items.RemoveAt(idx));
        }
    }

    [Transacts]
    public void Insert(int index, T item)
    {
        if ((uint)index > (uint)_items.Count)
        {
            Tx.FailOrThrow($"TxList.Insert 下标越界: {index} (Count={_items.Count})",
                new ArgumentOutOfRangeException(nameof(index), index, "Index must be within the bounds of the List."));
        }
        _items.Insert(index, item);
        if (Tx.IsActive)
        {
            Tx.Record(this, index, static (self, idx) => self._items.RemoveAt(idx));
        }
    }

    [Transacts]
    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)_items.Count)
        {
            Tx.FailOrThrow($"TxList.RemoveAt 下标越界: {index} (Count={_items.Count})",
                new ArgumentOutOfRangeException(nameof(index), index, "Index was out of range."));
        }
        T prev = _items[index];
        _items.RemoveAt(index);
        if (Tx.IsActive)
        {
            Tx.Record(this, index, prev, static (self, idx, oldVal) => self._items.Insert(idx, oldVal));
        }
    }

    [Transacts]
    public bool Remove(T item)
    {
        int idx = _items.IndexOf(item);
        if (idx < 0)
            return false;

        RemoveAt(idx);
        return true;
    }

    [Transacts]
    public void Clear()
    {
        if (_items.Count == 0)
            return;

        if (Tx.IsActive)
        {
            T[] snapshot = _items.ToArray();
            Tx.Record(this, snapshot, static (self, oldArr) =>
            {
                self._items.Clear();
                self._items.AddRange(oldArr);
            });
        }
        _items.Clear();
    }

    [Pure]
    public bool Contains(T item) => _items.Contains(item);

    [Pure]
    public int IndexOf(T item) => _items.IndexOf(item);

    [Pure]
    public List<T>.Enumerator GetEnumerator() => _items.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
}

/// <summary>
/// 支持键值级增量事务回滚的字典容器（替代原生 Dictionary&lt;TKey, TValue&gt;）。
/// </summary>
public sealed class TxDict<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    private readonly Dictionary<TKey, TValue> _dict = new();

    public int Count
    {
        [Pure] get => _dict.Count;
    }

    public TValue this[TKey key]
    {
        [Decides]
        get
        {
            if (!_dict.TryGetValue(key, out TValue? val))
            {
                Tx.FailOrThrow($"TxDict 中不存在键 '{key}'",
                    new KeyNotFoundException($"The given key '{key}' was not present in the dictionary."));
            }
            return val;
        }

        [Transacts]
        set => Set(key, value);
    }

    [Transacts]
    public void Set(TKey key, TValue value)
    {
        if (_dict.TryGetValue(key, out TValue? prev))
        {
            if (!EqualityComparer<TValue>.Default.Equals(prev, value))
            {
                if (Tx.IsActive)
                {
                    Tx.Record(this, key, prev, static (self, k, oldVal) => self._dict[k] = oldVal);
                }
                _dict[key] = value;
            }
        }
        else
        {
            if (Tx.IsActive)
            {
                Tx.Record(this, key, static (self, k) => self._dict.Remove(k));
            }
            _dict[key] = value;
        }
    }

    [Transacts]
    public bool Remove(TKey key)
    {
        if (!_dict.TryGetValue(key, out TValue? prev))
            return false;

        _dict.Remove(key);
        if (Tx.IsActive)
        {
            Tx.Record(this, key, prev, static (self, k, oldVal) => self._dict[k] = oldVal);
        }
        return true;
    }

    [Pure]
    public bool ContainsKey(TKey key) => _dict.ContainsKey(key);

    [Pure]
    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) =>
        _dict.TryGetValue(key, out value);

    [Pure]
    public TValue GetOrDefault(TKey key, TValue defaultValue = default!) =>
        _dict.TryGetValue(key, out TValue? val) ? val : defaultValue;

    [Pure]
    public Dictionary<TKey, TValue>.Enumerator GetEnumerator() => _dict.GetEnumerator();

    IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => _dict.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _dict.GetEnumerator();
}
