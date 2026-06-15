using System;
using System.Collections.Generic;
using UnityEngine;

namespace RedDot
{
    /// <summary>
    /// 前缀树路由层 —— 运行时零字符串操作，所有 hash 由编辑器预计算。
    /// </summary>
    public class RedDotTrie
    {
        private struct TrieNode
        {
            public int ParentIndex;
            public int PathHash;
            public Dictionary<int, int> Children;

            public TrieNode(int parentIndex, int pathHash)
            {
                ParentIndex = parentIndex;
                PathHash = pathHash;
                Children = null;
            }

            public bool IsRoot => ParentIndex == -1;
            public bool IsLeaf => Children == null || Children.Count == 0;
        }

        private List<TrieNode> _nodes;
        private Dictionary<int, int> _pathToIndex;

        private const int DEFAULT_CAPACITY = 1024;

        public int NodeCount => _nodes.Count;
        public const int ROOT_INDEX = 0;
        public const int INVALID_INDEX = -1;

        public RedDotTrie(int initialCapacity = DEFAULT_CAPACITY)
        {
            _nodes = new List<TrieNode>(initialCapacity);
            _pathToIndex = new Dictionary<int, int>(initialCapacity);
            _nodes.Add(new TrieNode(INVALID_INDEX, 0));
            _pathToIndex[0] = ROOT_INDEX;
        }

        public int RegisterNode(int pathHash, int parentHash)
        {
            if (_pathToIndex.TryGetValue(pathHash, out int existing))
                return existing;

            int parentIndex;
            if (!_pathToIndex.TryGetValue(parentHash, out parentIndex))
            {
                if (parentHash != 0)
                    Debug.LogWarning($"[RedDotTrie] RegisterNode: parentHash=0x{parentHash:X8} not registered, attach pathHash=0x{pathHash:X8} to root");

                parentIndex = ROOT_INDEX;
            }

            TrieNode parent = _nodes[parentIndex];
            if (parent.Children == null)
            {
                parent.Children = new Dictionary<int, int>();
                _nodes[parentIndex] = parent;
            }

            int newIndex = _nodes.Count;
            _nodes.Add(new TrieNode(parentIndex, pathHash));
            parent.Children[pathHash] = newIndex;
            _nodes[parentIndex] = parent;
            _pathToIndex[pathHash] = newIndex;
            return newIndex;
        }

        public int FindIndex(int pathHash)
        {
            return _pathToIndex.TryGetValue(pathHash, out int i) ? i : INVALID_INDEX;
        }

        public bool TryFindIndex(int pathHash, out int idx)
        {
            return _pathToIndex.TryGetValue(pathHash, out idx);
        }

        public int GetParentIndex(int idx)
        {
            return (idx < 0 || idx >= _nodes.Count) ? INVALID_INDEX : _nodes[idx].ParentIndex;
        }

        public int GetPathHash(int idx)
        {
            if (idx < 0 || idx >= _nodes.Count)
            {
                Debug.Assert(idx >= 0 && idx < _nodes.Count, $"[RedDotTrie] GetPathHash: idx={idx} out of range [0, {_nodes.Count})");
                return 0; // Root 的 PathHash 也是 0，作为安全兜底
            }
            return _nodes[idx].PathHash;
        }

        public bool ContainsPath(int pathHash)
        {
            return _pathToIndex.ContainsKey(pathHash);
        }

        public IEnumerable<int> GetAncestors(int idx)
        {
            int cur = GetParentIndex(idx);
            while (cur != INVALID_INDEX)
            {
                yield return cur;
                cur = _nodes[cur].ParentIndex;
            }
        }

        public void CollectAncestors(int idx, List<int> outList)
        {
            outList.Clear();
            int cur = GetParentIndex(idx);
            while (cur != INVALID_INDEX)
            {
                outList.Add(cur);
                cur = _nodes[cur].ParentIndex;
            }
        }

        public int GetChildCount(int idx)
        {
            if (idx < 0 || idx >= _nodes.Count)
                return 0;

            return _nodes[idx].Children?.Count ?? 0;
        }

        public void GetChildren(int idx, List<int> outChildren)
        {
            outChildren.Clear();

            if (idx < 0 || idx >= _nodes.Count)
                return;

            var c = _nodes[idx].Children;
            if (c != null)
            {
                foreach (var kv in c)
                    outChildren.Add(kv.Value);
            }
        }

        public IEnumerable<int> GetChildren(int idx)
        {
            if (idx < 0 || idx >= _nodes.Count)
                yield break;

            var c = _nodes[idx].Children;
            if (c == null)
                yield break;

            foreach (var kv in c)
                yield return kv.Value;
        }

        public bool TryRemoveNode(int pathHash)
        {
            if (!_pathToIndex.TryGetValue(pathHash, out int idx))
                return false;

            var node = _nodes[idx];

            if (!node.IsLeaf || node.IsRoot)
                return false;

            int pi = node.ParentIndex;
            if (pi != INVALID_INDEX)
            {
                var p = _nodes[pi];
                p.Children?.Remove(node.PathHash);
                _nodes[pi] = p;
            }

            _pathToIndex.Remove(pathHash);
            _nodes[idx] = new TrieNode(INVALID_INDEX, 0);
            return true;
        }

        public void Clear()
        {
            _nodes.Clear();
            _pathToIndex.Clear();
            _nodes.Add(new TrieNode(INVALID_INDEX, 0));
            _pathToIndex[0] = ROOT_INDEX;
        }

        /// <summary>按 hash 反向解析路径（仅编辑器调试用，会产生字符串分配）</summary>
        public string GetFullPathByHash(int idx)
        {
            if (idx < 0 || idx >= _nodes.Count)
                return "";

            if (idx == ROOT_INDEX)
                return "Root";

            var parts = new List<string>();
            int cur = idx;
            while (cur != ROOT_INDEX && cur != INVALID_INDEX)
            {
                parts.Add($"0x{_nodes[cur].PathHash:X8}");
                cur = _nodes[cur].ParentIndex;
            }

            parts.Reverse();
            return string.Join("_", parts);
        }

#if UNITY_EDITOR
        public void DebugPrint()
        {
            Debug.Log($"[RedDotTrie] Nodes={_nodes.Count}, Paths={_pathToIndex.Count}");
            DebugPrintRecursive(ROOT_INDEX, 0);
        }

        private void DebugPrintRecursive(int idx, int depth)
        {
            if (idx < 0 || idx >= _nodes.Count)
                return;

            var n = _nodes[idx];
            string indent = new string(' ', depth * 2);
            string label = n.IsRoot ? "Root" : $"0x{n.PathHash:X8}";
            string info = n.IsLeaf ? "[leaf]" : $"[{n.Children.Count} children]";
            Debug.Log($"{indent}├─ [{idx}] {label} p={n.ParentIndex} {info}");

            if (n.Children != null)
            {
                foreach (var kv in n.Children)
                    DebugPrintRecursive(kv.Value, depth + 1);
            }
        }
#endif
    }
}
