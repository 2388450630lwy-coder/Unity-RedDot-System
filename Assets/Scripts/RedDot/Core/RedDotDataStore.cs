using System;
using System.Collections.Generic;
using UnityEngine;

namespace RedDot
{
    /// <summary>
    /// 红点数据存储层。类型分自身类型（count > 0 时有效）和聚合类型（self | children 的 OR）。
    /// </summary>
    public class RedDotDataStore
    {
        private int[] _selfCounts;
        private int[] _totalCounts;

        /// <summary>节点自身类型标志（不含子节点）</summary>
        private RedDotType[] _selfTypeFlags;

        /// <summary>节点聚合类型标志（自身 + 子孙 OR 汇聚）</summary>
        private RedDotType[] _totalTypeFlags;

        private List<Action<RedDotState>>[] _listeners;

        private int _capacity;
        private const int DEFAULT_CAPACITY = 1024;
        private const int GROWTH_FACTOR = 2;

        public RedDotDataStore(int initialCapacity = DEFAULT_CAPACITY)
        {
            _capacity = initialCapacity;
            _selfCounts = new int[_capacity];
            _totalCounts = new int[_capacity];
            _selfTypeFlags = new RedDotType[_capacity];
            _totalTypeFlags = new RedDotType[_capacity];
            _listeners = new List<Action<RedDotState>>[_capacity];
        }

        public void EnsureCapacity(int requiredIndex)
        {
            if (requiredIndex < _capacity)
                return;

            int newCapacity = _capacity;
            while (newCapacity <= requiredIndex)
                newCapacity *= GROWTH_FACTOR;

            Array.Resize(ref _selfCounts, newCapacity);
            Array.Resize(ref _totalCounts, newCapacity);
            Array.Resize(ref _selfTypeFlags, newCapacity);
            Array.Resize(ref _totalTypeFlags, newCapacity);
            Array.Resize(ref _listeners, newCapacity);
            _capacity = newCapacity;
        }

        /// <summary>设置自身计数，返回 delta（用于祖先链上溯）</summary>
        public int SetSelfCount(int nodeIndex, int count)
        {
            EnsureCapacity(nodeIndex);
            int oldSelf = _selfCounts[nodeIndex];

            if (oldSelf == count)
                return 0;

            int delta = count - oldSelf;
            _selfCounts[nodeIndex] = count;
            _totalCounts[nodeIndex] += delta;
            return delta;
        }

        public int GetSelfCount(int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= _capacity)
                return 0;

            return _selfCounts[nodeIndex];
        }

        public int GetTotalCount(int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= _capacity)
                return 0;

            return _totalCounts[nodeIndex];
        }

        public bool AddDeltaToTotal(int nodeIndex, int delta)
        {
            if (delta == 0)
                return false;

            EnsureCapacity(nodeIndex);
            int oldTotal = _totalCounts[nodeIndex];
            _totalCounts[nodeIndex] += delta;
            return oldTotal != _totalCounts[nodeIndex];
        }

        public bool SetSelfTypeFlag(int nodeIndex, RedDotType type)
        {
            EnsureCapacity(nodeIndex);
            RedDotType oldType = _selfTypeFlags[nodeIndex];

            if (oldType == type)
                return false;

            _selfTypeFlags[nodeIndex] = type;
            return true;
        }

        public RedDotType GetSelfTypeFlag(int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= _capacity)
                return 0;

            return _selfTypeFlags[nodeIndex];
        }

        /// <summary>写入 Manager 聚合 self|children 后的类型标志</summary>
        public bool SetTypeFlags(int nodeIndex, RedDotType flags)
        {
            EnsureCapacity(nodeIndex);
            RedDotType oldFlags = _totalTypeFlags[nodeIndex];

            if (oldFlags == flags)
                return false;

            _totalTypeFlags[nodeIndex] = flags;
            return true;
        }

        public RedDotType GetTypeFlags(int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= _capacity)
                return 0;

            return _totalTypeFlags[nodeIndex];
        }

        /// <summary>获取最高优先级类型（Number > IsNew > CanUpdate > Tips > Normal）</summary>
        public RedDotType GetHighestType(int nodeIndex)
        {
            RedDotType flags = GetTypeFlags(nodeIndex);

            if (flags == 0)
                return 0;

            foreach (var t in RedDotTypeHelper.PriorityOrder)
            {
                if ((flags & t) == t)
                    return t;
            }
            return 0;
        }

        public List<RedDotType> GetActiveTypes(int nodeIndex)
        {
            var result = new List<RedDotType>();
            GetActiveTypes(nodeIndex, result);
            return result;
        }

        public void GetActiveTypes(int nodeIndex, List<RedDotType> outList)
        {
            outList.Clear();
            RedDotType flags = GetTypeFlags(nodeIndex);
            foreach (var t in RedDotTypeHelper.PriorityOrder)
            {
                if ((flags & t) == t)
                    outList.Add(t);
            }
        }

        public RedDotState GetState(int nodeIndex, int pathHash)
        {
            if (nodeIndex < 0 || nodeIndex >= _capacity)
                return new RedDotState(pathHash, 0, 0, 0);

            return new RedDotState(
                pathHash,
                _selfCounts[nodeIndex],
                _totalCounts[nodeIndex],
                GetHighestType(nodeIndex));
        }

        public void AddListener(int nodeIndex, Action<RedDotState> callback)
        {
            if (callback == null)
                return;

            EnsureCapacity(nodeIndex);

            if (_listeners[nodeIndex] == null)
                _listeners[nodeIndex] = new List<Action<RedDotState>>(2);

            var list = _listeners[nodeIndex];
            if (!list.Contains(callback))
                list.Add(callback);
        }

        public void RemoveListener(int nodeIndex, Action<RedDotState> callback)
        {
            if (callback == null)
                return;

            if (nodeIndex < 0 || nodeIndex >= _capacity)
                return;

            var list = _listeners[nodeIndex];
            if (list != null)
            {
                list.Remove(callback);
                if (list.Count == 0)
                    _listeners[nodeIndex] = null;
            }
        }

        /// <summary>通知节点所有监听器（反向遍历，安全处理自注销）</summary>
        public void NotifyListeners(int nodeIndex, RedDotState state)
        {
            if (nodeIndex < 0 || nodeIndex >= _capacity)
                return;

            var list = _listeners[nodeIndex];

            if (list == null || list.Count == 0)
                return;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                try
                {
                    list[i]?.Invoke(state);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[RedDotDataStore] Listener callback error: {e}");
                }
            }
        }

        public bool HasListeners(int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= _capacity)
                return false;

            return _listeners[nodeIndex] != null && _listeners[nodeIndex].Count > 0;
        }

        public int ListenerCount(int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= _capacity)
                return 0;

            return _listeners[nodeIndex]?.Count ?? 0;
        }

        public void ResetNode(int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= _capacity)
                return;

            _selfCounts[nodeIndex] = 0;
            _totalCounts[nodeIndex] = 0;
            _selfTypeFlags[nodeIndex] = 0;
            _totalTypeFlags[nodeIndex] = 0;
            _listeners[nodeIndex]?.Clear();
            _listeners[nodeIndex] = null;
        }

        public void Clear()
        {
            Array.Clear(_selfCounts, 0, _capacity);
            Array.Clear(_totalCounts, 0, _capacity);
            Array.Clear(_selfTypeFlags, 0, _capacity);
            Array.Clear(_totalTypeFlags, 0, _capacity);
            for (int i = 0; i < _capacity; i++)
            {
                _listeners[i]?.Clear();
                _listeners[i] = null;
            }
        }
    }
}
