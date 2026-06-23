using System;
using System.Collections.Generic;
using UnityEngine;

namespace RedDot
{
    /// <summary>
    /// 红点系统门面 —— Mono 单例，协调 Trie 路由层与 DataStore 数据层。
    ///
    /// 核心流程 SetRedDot：
    /// 1. Trie 查找节点索引
    /// 2. DataStore 更新自身计数，得到 delta
    /// 3. 沿父链上溯，逐层增量更新 TotalCount
    /// 4. 通知受影响的监听器
    ///
    /// O(depth)，与树总大小无关，不做全局扫描。
    /// </summary>
    public class RedDotManager : MonoBehaviour
    {
        private static RedDotManager _instance;

        /// <summary>全局访问点，场景中不存在时会自动创建。</summary>
        public static RedDotManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindObjectOfType<RedDotManager>();
                    if (_instance == null)
                    {
                        var go = new GameObject("[RedDotManager]");
                        _instance = go.AddComponent<RedDotManager>();
                        DontDestroyOnLoad(go);
                    }
                }
                return _instance;
            }
        }

        public static bool HasInstance => _instance != null;

        private RedDotTrie      _trie;
        private RedDotDataStore _data;
        private readonly List<int>    _childrenBuffer   = new List<int>(8);
        private readonly HashSet<int> _staticPathIds = new HashSet<int>();

        /// <summary>动态节点复合键映射：(parentId, childId) → nodeIndex。</summary>
        private readonly Dictionary<(int parentId, long childId), int> _dynamicKeyToIndex
            = new Dictionary<(int, long), int>();

        /// <summary>动态节点 ID 注册表：(parentId, childId) → 已分配的负数唯一 ID。</summary>
        private readonly Dictionary<(int parentId, long childId), int> _dynamicIdRegistry
            = new Dictionary<(int, long), int>();

        /// <summary>下一个动态 ID，从 -1 递减，与正数 StableId 天然隔离。</summary>
        private int _nextDynamicId = -1;

        private bool _initialized;
        private bool _registered;
        private bool _isRegistering;

        public RedDotTrie Trie => _trie;

        private int DynamicNodeId(int parentId, long childId)
        {
            var key = (parentId, childId);
            if (!_dynamicIdRegistry.TryGetValue(key, out int id))
            {
                id = _nextDynamicId--;
                _dynamicIdRegistry[key] = id;
            }
            return id;
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            Initialize();
            EnsureRegistered();
        }

        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }

        private void Initialize()
        {
            if (_initialized)
                return;

            _trie        = new RedDotTrie();
            _data        = new RedDotDataStore();
            _initialized = true;
        }

        /// <summary>
        /// 确保生成路径已注册。UI OnEnable 可能早于业务 Start，此处保证路径就绪。
        /// </summary>
        public void EnsureRegistered()
        {
            Initialize();

            if (_registered || _isRegistering)
                return;

            _isRegistering = true;
            try
            {
                RedDotPathsRegistration.RegisterAll(this);
                _registered = true;
            }
            finally
            {
                _isRegistering = false;
            }
        }

        /// <summary>
        /// 注册静态红点节点。pathId 为编辑器分配的 StableId（const int 常量）。
        /// </summary>
        public int RegisterNode(int pathId, int parentId, bool isStatic = false)
        {
            Initialize();
            int nodeIndex = _trie.RegisterNode(pathId, parentId);
            _data.EnsureCapacity(nodeIndex);

            if (isStatic || _isRegistering)
                _staticPathIds.Add(pathId);

            RefreshNodeFlags(nodeIndex);
            return nodeIndex;
        }

        // ══════════════════════════════════════════════════════════════════
        //  核心写操作（内部）
        // ══════════════════════════════════════════════════════════════════

        private void SetRedDotByIndex(int nodeIndex, int count, RedDotType type)
        {
            if (count < 0)
            {
                Debug.LogWarning($"[RedDotManager] SetRedDot: count={count} < 0, clamped to 0");
                count = 0;
            }

            int delta = _data.SetSelfCount(nodeIndex, count);

            RedDotType selfType        = count > 0 ? type : 0;
            bool       selfTypeChanged  = _data.SetSelfTypeFlag(nodeIndex, selfType);
            bool       totalTypeChanged = RefreshNodeFlags(nodeIndex);

            if (delta != 0 || selfTypeChanged || totalTypeChanged)
                NotifyNode(nodeIndex);

            int current = _trie.GetParentIndex(nodeIndex);
            while (current != RedDotTrie.INVALID_INDEX)
            {
                bool totalChanged = _data.AddDeltaToTotal(current, delta);
                bool flagsChanged = RefreshNodeFlags(current);
                if (totalChanged || flagsChanged)
                    NotifyNode(current);
                current = _trie.GetParentIndex(current);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  静态路径公共 API（通过 int pathId 路由）
        // ══════════════════════════════════════════════════════════════════

        /// <summary>设置静态节点的自身红点计数和类型。</summary>
        public void SetRedDot(int pathId, int count, RedDotType type = RedDotType.Normal)
        {
            if (pathId == 0)
                return;

            EnsureRegistered();

            int nodeIndex = _trie.FindIndex(pathId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
            {
                Debug.LogWarning($"[RedDotManager] SetRedDot: pathId={pathId} not registered");
                return;
            }

            SetRedDotByIndex(nodeIndex, count, type);
        }

        /// <summary>刷新节点聚合类型标志 = self | children</summary>
        private bool RefreshNodeFlags(int nodeIndex)
        {
            RedDotType flags = _data.GetSelfTypeFlag(nodeIndex);
            _trie.GetChildren(nodeIndex, _childrenBuffer);

            for (int i = 0; i < _childrenBuffer.Count; i++)
                flags |= _data.GetTypeFlags(_childrenBuffer[i]);

            return _data.SetTypeFlags(nodeIndex, flags);
        }

        private void NotifyNode(int nodeIndex)
        {
            int pathId = _trie.GetPathId(nodeIndex);
            RedDotState state = _data.GetState(nodeIndex, pathId);
            try
            {
                _data.NotifyListeners(nodeIndex, state);
            }
            catch (Exception e)
            {
                Debug.LogError($"[RedDotManager] Listener callback error: {e}");
            }
        }

        public RedDotType GetEffectiveType(int pathId)
        {
            if (pathId == 0)
                return 0;

            EnsureRegistered();
            int nodeIndex = _trie.FindIndex(pathId);

            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return 0;

            return _data.GetHighestType(nodeIndex);
        }

        public List<RedDotType> GetActiveTypes(int pathId)
        {
            var result = new List<RedDotType>();
            GetActiveTypes(pathId, result);
            return result;
        }

        /// <summary>获取活跃类型（优先级降序），写入调用方复用的 List，零分配。</summary>
        public void GetActiveTypes(int pathId, List<RedDotType> outList)
        {
            outList.Clear();

            if (pathId == 0)
                return;

            EnsureRegistered();
            int nodeIndex = _trie.FindIndex(pathId);

            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return;

            _data.GetActiveTypes(nodeIndex, outList);
        }

        public RedDotState GetState(int pathId)
        {
            if (pathId == 0)
                return new RedDotState(0L, 0, 0, 0);

            EnsureRegistered();
            int nodeIndex = _trie.FindIndex(pathId);

            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return new RedDotState(pathId, 0, 0, 0);

            return _data.GetState(nodeIndex, pathId);
        }

        /// <summary>获取路径的聚合红点计数（自身 + 子孙），O(1)。</summary>
        public int GetRedDot(int pathId)
        {
            if (pathId == 0)
                return 0;

            EnsureRegistered();
            int nodeIndex = _trie.FindIndex(pathId);

            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return 0;

            return _data.GetTotalCount(nodeIndex);
        }

        /// <summary>获取路径的自身红点计数（不含子孙）。</summary>
        public int GetSelfRedDot(int pathId)
        {
            if (pathId == 0)
                return 0;

            EnsureRegistered();
            int nodeIndex = _trie.FindIndex(pathId);

            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return 0;

            return _data.GetSelfCount(nodeIndex);
        }

        /// <summary>订阅红点变化，注册时立即回调一次当前状态。</summary>
        public void AddListener(int pathId, Action<RedDotState> callback)
        {
            if (callback == null)
                return;

            if (pathId == 0)
            {
                callback.Invoke(new RedDotState(0L, 0, 0, 0));
                return;
            }

            EnsureRegistered();

            int nodeIndex = _trie.FindIndex(pathId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
            {
                Debug.LogWarning($"[RedDotManager] AddListener: pathId={pathId} not registered");
                return;
            }

            _data.AddListener(nodeIndex, callback);

            try
            {
                callback.Invoke(_data.GetState(nodeIndex, pathId));
            }
            catch (Exception e)
            {
                Debug.LogError($"[RedDotManager] AddListener initial callback error: {e}");
            }
        }

        public void RemoveListener(int pathId, Action<RedDotState> callback)
        {
            if (callback == null || pathId == 0)
                return;

            EnsureRegistered();

            int nodeIndex = _trie.FindIndex(pathId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return;

            _data.RemoveListener(nodeIndex, callback);
        }

        // ══════════════════════════════════════════════════════════════════
        //  动态节点 API（通过复合键 (parentId, childId) 路由，零碰撞）
        // ══════════════════════════════════════════════════════════════════

        // 动态节点 = 父节点 StableId + 业务 long childId，适用于运行时动态创建的红点，
        // 如邮件第 N 封、背包第 N 格、活动第 N 期。
        // 所有动态 API 优先通过 _dynamicKeyToIndex 查找，保证 O(1) 路由，零碰撞。

        /// <summary>
        /// 通过复合键查找动态节点 nodeIndex。
        /// 找不到时回退到 Trie 直查（兼容直接注册的节点）。
        /// </summary>
        private int FindDynamicIndex(int parentId, long childId)
        {
            if (_dynamicKeyToIndex.TryGetValue((parentId, childId), out int nodeIndex))
                return nodeIndex;

            return _trie.FindIndex(DynamicNodeId(parentId, childId));
        }

        /// <summary>
        /// 注册动态子节点。parentId 如 RedDotPaths.Root_Mail，childId 为业务唯一键（支持 long）。
        /// 同时写入复合键映射，后续所有操作通过复合键路由，零碰撞。
        /// </summary>
        public int RegisterDynamicNode(int parentId, long childId)
        {
            EnsureRegistered();

            var key = (parentId, childId);
            if (_dynamicKeyToIndex.TryGetValue(key, out int cached))
                return cached;

            int pathId   = DynamicNodeId(parentId, childId);
            int nodeIndex = _trie.RegisterNode(pathId, parentId);
            _data.EnsureCapacity(nodeIndex);
            RefreshNodeFlags(nodeIndex);
            _dynamicKeyToIndex[key] = nodeIndex;
            return nodeIndex;
        }

        public void SetRedDot(int parentId, long childId, int count, RedDotType type = RedDotType.Normal)
        {
            EnsureRegistered();

            int nodeIndex = FindDynamicIndex(parentId, childId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
            {
                Debug.LogWarning($"[RedDotManager] SetRedDot: dynamic node (parentId={parentId}, childId={childId}) not registered");
                return;
            }

            SetRedDotByIndex(nodeIndex, count, type);
        }

        public int GetRedDot(int parentId, long childId)
        {
            EnsureRegistered();
            int nodeIndex = FindDynamicIndex(parentId, childId);
            return nodeIndex == RedDotTrie.INVALID_INDEX ? 0 : _data.GetTotalCount(nodeIndex);
        }

        public int GetSelfRedDot(int parentId, long childId)
        {
            EnsureRegistered();
            int nodeIndex = FindDynamicIndex(parentId, childId);
            return nodeIndex == RedDotTrie.INVALID_INDEX ? 0 : _data.GetSelfCount(nodeIndex);
        }

        /// <summary>获取动态子节点状态快照。</summary>
        public RedDotState GetState(int parentId, long childId)
        {
            EnsureRegistered();
            int nodeIndex = FindDynamicIndex(parentId, childId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return new RedDotState(DynamicNodeId(parentId, childId), 0, 0, 0);

            int pathId = _trie.GetPathId(nodeIndex);
            return _data.GetState(nodeIndex, pathId);
        }

        /// <summary>清零动态子节点（递归清理：静态子孙清零，动态子孙移除）。</summary>
        public void ClearNode(int parentId, long childId)
        {
            EnsureRegistered();
            int nodeIndex = FindDynamicIndex(parentId, childId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX) return;

            int pathId = _trie.GetPathId(nodeIndex);
            ClearNodeRecursive(pathId);
        }

        /// <summary>订阅动态子节点变化，注册时立即回调当前状态。</summary>
        public void AddListener(int parentId, long childId, Action<RedDotState> callback)
        {
            if (callback == null) return;
            EnsureRegistered();

            int nodeIndex = FindDynamicIndex(parentId, childId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
            {
                Debug.LogWarning($"[RedDotManager] AddListener: dynamic node (parentId={parentId}, childId={childId}) not registered");
                return;
            }

            _data.AddListener(nodeIndex, callback);

            try
            {
                int pathId = _trie.GetPathId(nodeIndex);
                callback.Invoke(_data.GetState(nodeIndex, pathId));
            }
            catch (Exception e)
            {
                Debug.LogError($"[RedDotManager] AddListener initial callback error: {e}");
            }
        }

        public void RemoveListener(int parentId, long childId, Action<RedDotState> callback)
        {
            if (callback == null) return;
            EnsureRegistered();

            int nodeIndex = FindDynamicIndex(parentId, childId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX) return;

            _data.RemoveListener(nodeIndex, callback);
        }

        // ══════════════════════════════════════════════════════════════════
        //  清理
        // ══════════════════════════════════════════════════════════════════

        /// <summary>清零静态节点自身 SelfCount（不递归，子节点不受影响）。</summary>
        public void ClearNode(int pathId)
        {
            if (pathId == 0)
                return;

            SetRedDot(pathId, 0);
        }

        /// <summary>
        /// 递归清理子树：静态节点 SelfCount → 0，动态节点 RemoveDynamicLeafNode。
        /// 移除失败（如有监听器）则回退到 SetRedDot(id, 0)。
        /// </summary>
        public void ClearNodeRecursive(int pathId)
        {
            if (pathId == 0) return;
            EnsureRegistered();

            int nodeIndex = _trie.FindIndex(pathId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX) return;

            _childrenBuffer.Clear();
            _trie.GetChildren(nodeIndex, _childrenBuffer);

            for (int i = _childrenBuffer.Count - 1; i >= 0; i--)
            {
                _trie.GetChildren(nodeIndex, _childrenBuffer);
                int  childIdx = _childrenBuffer[i];
                int childId  = _trie.GetPathId(childIdx);
                ClearNodeRecursive(childId);
            }

            if (_staticPathIds.Contains(pathId))
                SetRedDotByIndex(nodeIndex, 0, RedDotType.Normal);
            else if (!RemoveDynamicLeafNode(pathId))
                SetRedDotByIndex(nodeIndex, 0, RedDotType.Normal);
        }

        /// <summary>移除运行时动态创建的叶子节点。静态生成路径请使用 ClearNode。</summary>
        public bool RemoveDynamicLeafNode(int pathId)
        {
            if (pathId == 0)
                return false;

            EnsureRegistered();

            if (_staticPathIds.Contains(pathId))
                return false;

            int nodeIndex = _trie.FindIndex(pathId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return false;

            if (_data.HasListeners(nodeIndex) || _trie.GetChildCount(nodeIndex) != 0)
                return false;

            SetRedDotByIndex(nodeIndex, 0, RedDotType.Normal);
            bool removed = _trie.TryRemoveNode(pathId);

            if (removed)
            {
                _data.ResetNode(nodeIndex);

                // 同步清理复合键映射，避免 nodeIndex 被复用后误命中
                (int, long) foundKey = default;
                bool found = false;
                foreach (var kv in _dynamicKeyToIndex)
                {
                    if (kv.Value == nodeIndex) { foundKey = kv.Key; found = true; break; }
                }
                if (found) _dynamicKeyToIndex.Remove(foundKey);
            }

            return removed;
        }

        /// <summary>重置整个红点系统（慎用）</summary>
        public void ResetAll()
        {
            Initialize();
            _trie.Clear();
            _data.Clear();
            _staticPathIds.Clear();
            _dynamicKeyToIndex.Clear();
            _dynamicIdRegistry.Clear();
            _nextDynamicId = -1;
            _registered = false;
            EnsureRegistered();
        }
    }
}
