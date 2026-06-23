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
        private readonly HashSet<long> _staticPathHashes = new HashSet<long>();

        /// <summary>
        /// 动态节点复合键映射：(parentHash, childId) → nodeIndex。
        /// 与 Trie 的 hash→index 映射并行存在，保证即使 ComputeDynamic 发生哈希碰撞，
        /// 复合键仍能精确路由到正确节点，完全消除动态节点碰撞影响。
        /// </summary>
        private readonly Dictionary<(long parentHash, long childId), int> _dynamicKeyToIndex
            = new Dictionary<(long, long), int>();

        private bool _initialized;
        private bool _registered;
        private bool _isRegistering;

        public RedDotTrie Trie => _trie;

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
        /// 注册红点节点。运行时零字符串分配，所有 hash 由编辑器预计算。
        /// </summary>
        public int RegisterNode(long pathHash, long parentPathHash, bool isStatic = false)
        {
            Initialize();
            int nodeIndex = _trie.RegisterNode(pathHash, parentPathHash);
            _data.EnsureCapacity(nodeIndex);

            if (isStatic || _isRegistering)
                _staticPathHashes.Add(pathHash);

            RefreshNodeFlags(nodeIndex);
            return nodeIndex;
        }

        // ══════════════════════════════════════════════════════════════════
        //  核心写操作（内部）
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 通过 nodeIndex 直接写入红点计数，跳过 hash 查找，供静态和动态路径共用。
        /// </summary>
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
        //  静态路径公共 API（通过 long hash 路由）
        // ══════════════════════════════════════════════════════════════════

        /// <summary>设置静态节点的自身红点计数和类型。</summary>
        public void SetRedDot(long pathHash, int count, RedDotType type = RedDotType.Normal)
        {
            if (pathHash == 0L)
                return;

            EnsureRegistered();

            int nodeIndex = _trie.FindIndex(pathHash);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
            {
                Debug.LogWarning($"[RedDotManager] SetRedDot: pathHash=0x{pathHash:X16} not registered");
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
            long pathHash = _trie.GetPathHash(nodeIndex);
            RedDotState state = _data.GetState(nodeIndex, pathHash);
            try
            {
                _data.NotifyListeners(nodeIndex, state);
            }
            catch (Exception e)
            {
                Debug.LogError($"[RedDotManager] Listener callback error: {e}");
            }
        }

        public RedDotType GetEffectiveType(long pathHash)
        {
            if (pathHash == 0L)
                return 0;

            EnsureRegistered();
            int nodeIndex = _trie.FindIndex(pathHash);

            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return 0;

            return _data.GetHighestType(nodeIndex);
        }

        public List<RedDotType> GetActiveTypes(long pathHash)
        {
            var result = new List<RedDotType>();
            GetActiveTypes(pathHash, result);
            return result;
        }

        /// <summary>获取活跃类型（优先级降序），写入调用方复用的 List，零分配。</summary>
        public void GetActiveTypes(long pathHash, List<RedDotType> outList)
        {
            outList.Clear();

            if (pathHash == 0L)
                return;

            EnsureRegistered();
            int nodeIndex = _trie.FindIndex(pathHash);

            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return;

            _data.GetActiveTypes(nodeIndex, outList);
        }

        public RedDotState GetState(long pathHash)
        {
            if (pathHash == 0L)
                return new RedDotState(0L, 0, 0, 0);

            EnsureRegistered();
            int nodeIndex = _trie.FindIndex(pathHash);

            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return new RedDotState(pathHash, 0, 0, 0);

            return _data.GetState(nodeIndex, pathHash);
        }

        /// <summary>获取路径的聚合红点计数（自身 + 子孙），O(1)。</summary>
        public int GetRedDot(long pathHash)
        {
            if (pathHash == 0L)
                return 0;

            EnsureRegistered();
            int nodeIndex = _trie.FindIndex(pathHash);

            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return 0;

            return _data.GetTotalCount(nodeIndex);
        }

        /// <summary>获取路径的自身红点计数（不含子孙）。</summary>
        public int GetSelfRedDot(long pathHash)
        {
            if (pathHash == 0L)
                return 0;

            EnsureRegistered();
            int nodeIndex = _trie.FindIndex(pathHash);

            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return 0;

            return _data.GetSelfCount(nodeIndex);
        }

        /// <summary>订阅红点变化，注册时立即回调一次当前状态。</summary>
        public void AddListener(long pathHash, Action<RedDotState> callback)
        {
            if (callback == null)
                return;

            if (pathHash == 0L)
            {
                callback.Invoke(new RedDotState(0L, 0, 0, 0));
                return;
            }

            EnsureRegistered();

            int nodeIndex = _trie.FindIndex(pathHash);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
            {
                Debug.LogWarning($"[RedDotManager] AddListener: pathHash=0x{pathHash:X16} not registered");
                return;
            }

            _data.AddListener(nodeIndex, callback);

            try
            {
                callback.Invoke(_data.GetState(nodeIndex, pathHash));
            }
            catch (Exception e)
            {
                Debug.LogError($"[RedDotManager] AddListener initial callback error: {e}");
            }
        }

        public void RemoveListener(long pathHash, Action<RedDotState> callback)
        {
            if (callback == null || pathHash == 0L)
                return;

            EnsureRegistered();

            int nodeIndex = _trie.FindIndex(pathHash);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return;

            _data.RemoveListener(nodeIndex, callback);
        }

        // ══════════════════════════════════════════════════════════════════
        //  动态节点 API（通过复合键 (parentHash, childId) 路由，零碰撞）
        // ══════════════════════════════════════════════════════════════════

        // 动态节点 = 父节点 hash + 业务 ID（int 或 long），适用于运行时动态创建的红点，
        // 如邮件第 N 封、背包第 N 格、活动第 N 期。
        //
        // 关键设计：所有动态 API 优先通过 _dynamicKeyToIndex[(parentHash, childId)]
        // 查找 nodeIndex，而非通过 ComputeDynamic 产生的 long hash。
        // 即使两个不同的 (parentHash, childId) 对恰好生成相同 hash，
        // 复合键仍能精确路由，完全消除动态节点碰撞的影响。

        /// <summary>
        /// 通过复合键查找动态节点 nodeIndex。
        /// 找不到时回退到 hash 查找（兼容直接用 hash 注册的旧节点）。
        /// </summary>
        private int FindDynamicIndex(long parentHash, int childId)
        {
            if (_dynamicKeyToIndex.TryGetValue((parentHash, (long)childId), out int nodeIndex))
                return nodeIndex;

            return _trie.FindIndex(RedDotHash.ComputeDynamic(parentHash, childId));
        }

        private int FindDynamicIndex(long parentHash, long childId)
        {
            if (_dynamicKeyToIndex.TryGetValue((parentHash, childId), out int nodeIndex))
                return nodeIndex;

            return _trie.FindIndex(RedDotHash.ComputeDynamic(parentHash, childId));
        }

        /// <summary>
        /// 注册动态子节点。parentHash 如 RedDotPaths.Root_Mail，childId 如 1001。
        /// 同时写入复合键映射，后续所有操作通过复合键路由，不受 hash 碰撞影响。
        /// </summary>
        public int RegisterDynamicNode(long parentHash, int childId)
        {
            EnsureRegistered();

            var key = (parentHash, (long)childId);
            if (_dynamicKeyToIndex.TryGetValue(key, out int cached))
                return cached;

            long pathHash = RedDotHash.ComputeDynamic(parentHash, childId);
            int  nodeIndex = RegisterNode(pathHash, parentHash, isStatic: false);
            _dynamicKeyToIndex[key] = nodeIndex;
            return nodeIndex;
        }

        /// <summary>
        /// 注册动态子节点（long childId）。适用于超出 int 范围的 ID。
        /// </summary>
        public int RegisterDynamicNode(long parentHash, long childId)
        {
            EnsureRegistered();

            var key = (parentHash, childId);
            if (_dynamicKeyToIndex.TryGetValue(key, out int cached))
                return cached;

            long pathHash = RedDotHash.ComputeDynamic(parentHash, childId);
            int  nodeIndex = RegisterNode(pathHash, parentHash, isStatic: false);
            _dynamicKeyToIndex[key] = nodeIndex;
            return nodeIndex;
        }

        /// <summary>设置动态子节点红点。</summary>
        public void SetRedDot(long parentHash, int childId, int count, RedDotType type = RedDotType.Normal)
        {
            EnsureRegistered();

            int nodeIndex = FindDynamicIndex(parentHash, childId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
            {
                Debug.LogWarning($"[RedDotManager] SetRedDot: dynamic node ({parentHash:X16}, {childId}) not registered");
                return;
            }

            SetRedDotByIndex(nodeIndex, count, type);
        }

        /// <summary>设置动态子节点红点（long childId）。</summary>
        public void SetRedDot(long parentHash, long childId, int count, RedDotType type = RedDotType.Normal)
        {
            EnsureRegistered();

            int nodeIndex = FindDynamicIndex(parentHash, childId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
            {
                Debug.LogWarning($"[RedDotManager] SetRedDot: dynamic node ({parentHash:X16}, {childId}) not registered");
                return;
            }

            SetRedDotByIndex(nodeIndex, count, type);
        }

        /// <summary>查询动态子节点 TotalCount。</summary>
        public int GetRedDot(long parentHash, int childId)
        {
            EnsureRegistered();
            int nodeIndex = FindDynamicIndex(parentHash, childId);
            return nodeIndex == RedDotTrie.INVALID_INDEX ? 0 : _data.GetTotalCount(nodeIndex);
        }

        /// <summary>查询动态子节点 TotalCount（long childId）。</summary>
        public int GetRedDot(long parentHash, long childId)
        {
            EnsureRegistered();
            int nodeIndex = FindDynamicIndex(parentHash, childId);
            return nodeIndex == RedDotTrie.INVALID_INDEX ? 0 : _data.GetTotalCount(nodeIndex);
        }

        /// <summary>查询动态子节点 SelfCount。</summary>
        public int GetSelfRedDot(long parentHash, int childId)
        {
            EnsureRegistered();
            int nodeIndex = FindDynamicIndex(parentHash, childId);
            return nodeIndex == RedDotTrie.INVALID_INDEX ? 0 : _data.GetSelfCount(nodeIndex);
        }

        /// <summary>查询动态子节点 SelfCount（long childId）。</summary>
        public int GetSelfRedDot(long parentHash, long childId)
        {
            EnsureRegistered();
            int nodeIndex = FindDynamicIndex(parentHash, childId);
            return nodeIndex == RedDotTrie.INVALID_INDEX ? 0 : _data.GetSelfCount(nodeIndex);
        }

        /// <summary>获取动态子节点状态快照。</summary>
        public RedDotState GetState(long parentHash, int childId)
        {
            EnsureRegistered();
            int nodeIndex = FindDynamicIndex(parentHash, childId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return new RedDotState(RedDotHash.ComputeDynamic(parentHash, childId), 0, 0, 0);

            long pathHash = _trie.GetPathHash(nodeIndex);
            return _data.GetState(nodeIndex, pathHash);
        }

        /// <summary>获取动态子节点状态快照（long childId）。</summary>
        public RedDotState GetState(long parentHash, long childId)
        {
            EnsureRegistered();
            int nodeIndex = FindDynamicIndex(parentHash, childId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return new RedDotState(RedDotHash.ComputeDynamic(parentHash, childId), 0, 0, 0);

            long pathHash = _trie.GetPathHash(nodeIndex);
            return _data.GetState(nodeIndex, pathHash);
        }

        /// <summary>清零动态子节点（递归清理：静态子孙清零，动态子孙移除）。</summary>
        public void ClearNode(long parentHash, int childId)
        {
            EnsureRegistered();
            int nodeIndex = FindDynamicIndex(parentHash, childId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX) return;

            long pathHash = _trie.GetPathHash(nodeIndex);
            ClearNodeRecursive(pathHash);
        }

        /// <summary>清零动态子节点（long childId）。</summary>
        public void ClearNode(long parentHash, long childId)
        {
            EnsureRegistered();
            int nodeIndex = FindDynamicIndex(parentHash, childId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX) return;

            long pathHash = _trie.GetPathHash(nodeIndex);
            ClearNodeRecursive(pathHash);
        }

        /// <summary>订阅动态子节点变化，注册时立即回调当前状态。</summary>
        public void AddListener(long parentHash, int childId, Action<RedDotState> callback)
        {
            if (callback == null) return;
            EnsureRegistered();

            int nodeIndex = FindDynamicIndex(parentHash, childId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
            {
                Debug.LogWarning($"[RedDotManager] AddListener: dynamic node ({parentHash:X16}, {childId}) not registered");
                return;
            }

            _data.AddListener(nodeIndex, callback);

            try
            {
                long pathHash = _trie.GetPathHash(nodeIndex);
                callback.Invoke(_data.GetState(nodeIndex, pathHash));
            }
            catch (Exception e)
            {
                Debug.LogError($"[RedDotManager] AddListener initial callback error: {e}");
            }
        }

        /// <summary>订阅动态子节点变化（long childId）。</summary>
        public void AddListener(long parentHash, long childId, Action<RedDotState> callback)
        {
            if (callback == null) return;
            EnsureRegistered();

            int nodeIndex = FindDynamicIndex(parentHash, childId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
            {
                Debug.LogWarning($"[RedDotManager] AddListener: dynamic node ({parentHash:X16}, {childId}) not registered");
                return;
            }

            _data.AddListener(nodeIndex, callback);

            try
            {
                long pathHash = _trie.GetPathHash(nodeIndex);
                callback.Invoke(_data.GetState(nodeIndex, pathHash));
            }
            catch (Exception e)
            {
                Debug.LogError($"[RedDotManager] AddListener initial callback error: {e}");
            }
        }

        /// <summary>取消订阅动态子节点。</summary>
        public void RemoveListener(long parentHash, int childId, Action<RedDotState> callback)
        {
            if (callback == null) return;
            EnsureRegistered();

            int nodeIndex = FindDynamicIndex(parentHash, childId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX) return;

            _data.RemoveListener(nodeIndex, callback);
        }

        /// <summary>取消订阅动态子节点（long childId）。</summary>
        public void RemoveListener(long parentHash, long childId, Action<RedDotState> callback)
        {
            if (callback == null) return;
            EnsureRegistered();

            int nodeIndex = FindDynamicIndex(parentHash, childId);
            if (nodeIndex == RedDotTrie.INVALID_INDEX) return;

            _data.RemoveListener(nodeIndex, callback);
        }

        // ══════════════════════════════════════════════════════════════════
        //  清理
        // ══════════════════════════════════════════════════════════════════

        /// <summary>清零自身 SelfCount（不递归，子节点不受影响）。</summary>
        public void ClearNode(long pathHash)
        {
            if (pathHash == 0L)
                return;

            SetRedDot(pathHash, 0);
        }

        /// <summary>
        /// 递归清理子树：静态节点 SelfCount → 0，动态节点 RemoveDynamicLeafNode。
        /// 移除失败（如有监听器）则回退到 SetRedDot(hash, 0)。
        /// </summary>
        public void ClearNodeRecursive(long pathHash)
        {
            if (pathHash == 0L) return;
            EnsureRegistered();

            int nodeIndex = _trie.FindIndex(pathHash);
            if (nodeIndex == RedDotTrie.INVALID_INDEX) return;

            _childrenBuffer.Clear();
            _trie.GetChildren(nodeIndex, _childrenBuffer);

            for (int i = _childrenBuffer.Count - 1; i >= 0; i--)
            {
                _trie.GetChildren(nodeIndex, _childrenBuffer);
                int  childIdx  = _childrenBuffer[i];
                long childHash = _trie.GetPathHash(childIdx);
                ClearNodeRecursive(childHash);
            }

            if (_staticPathHashes.Contains(pathHash))
                SetRedDot(pathHash, 0);
            else if (!RemoveDynamicLeafNode(pathHash))
                SetRedDot(pathHash, 0);
        }

        /// <summary>移除运行时动态创建的叶子节点。静态生成路径请使用 ClearNode。</summary>
        public bool RemoveDynamicLeafNode(long pathHash)
        {
            if (pathHash == 0L)
                return false;

            EnsureRegistered();

            if (_staticPathHashes.Contains(pathHash))
                return false;

            int nodeIndex = _trie.FindIndex(pathHash);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return false;

            if (_data.HasListeners(nodeIndex) || _trie.GetChildCount(nodeIndex) != 0)
                return false;

            SetRedDot(pathHash, 0);
            bool removed = _trie.TryRemoveNode(pathHash);

            if (removed)
            {
                _data.ResetNode(nodeIndex);

                // 同步清理复合键映射，避免 nodeIndex 被复用后误命中
                (long, long) foundKey = default;
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
            _staticPathHashes.Clear();
            _dynamicKeyToIndex.Clear();
            _registered = false;
            EnsureRegistered();
        }
    }
}
