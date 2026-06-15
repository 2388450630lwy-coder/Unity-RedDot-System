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

        private RedDotTrie _trie;
        private RedDotDataStore _data;
        private readonly List<int> _childrenBuffer = new List<int>(8);
        private readonly HashSet<int> _staticPathHashes = new HashSet<int>();
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
            {
                _instance = null;
            }
        }

        private void Initialize()
        {
            if (_initialized)
                return;

            _trie = new RedDotTrie();
            _data = new RedDotDataStore();
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
        public int RegisterNode(int pathHash, int parentPathHash, bool isStatic = false)
        {
            Initialize();
            int nodeIndex = _trie.RegisterNode(pathHash, parentPathHash);
            _data.EnsureCapacity(nodeIndex);

            if (isStatic || _isRegistering)
                _staticPathHashes.Add(pathHash);

            RefreshNodeFlags(nodeIndex);
            return nodeIndex;
        }

        /// <summary>
        /// 设置节点的自身红点计数和类型，增量沿祖先链上溯。
        /// </summary>
        public void SetRedDot(int pathHash, int count, RedDotType type = RedDotType.Normal)
        {
            if (pathHash == 0)
                return;

            EnsureRegistered();

            if (count < 0)
            {
                Debug.LogWarning($"[RedDotManager] SetRedDot: count={count} < 0, clamped to 0");
                count = 0;
            }

            int nodeIndex = _trie.FindIndex(pathHash);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
            {
                Debug.LogWarning($"[RedDotManager] SetRedDot: pathHash=0x{pathHash:X8} not registered");
                return;
            }

            int delta = _data.SetSelfCount(nodeIndex, count);

            // count 清零时同步清掉自身类型，避免父节点类型残留
            RedDotType selfType = count > 0 ? type : 0;
            bool selfTypeChanged = _data.SetSelfTypeFlag(nodeIndex, selfType);
            bool totalTypeChanged = RefreshNodeFlags(nodeIndex);

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
            int pathHash = _trie.GetPathHash(nodeIndex);
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

        public RedDotType GetEffectiveType(int pathHash)
        {
            if (pathHash == 0)
                return 0;

            EnsureRegistered();
            int nodeIndex = _trie.FindIndex(pathHash);

            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return 0;

            return _data.GetHighestType(nodeIndex);
        }

        public List<RedDotType> GetActiveTypes(int pathHash)
        {
            if (pathHash == 0)
                return new List<RedDotType>();

            EnsureRegistered();
            int nodeIndex = _trie.FindIndex(pathHash);

            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return new List<RedDotType>();

            return _data.GetActiveTypes(nodeIndex);
        }

        /// <summary>获取活跃类型（优先级降序），写入调用方复用的 List，零分配。</summary>
        public void GetActiveTypes(int pathHash, List<RedDotType> outList)
        {
            outList.Clear();

            if (pathHash == 0)
                return;

            EnsureRegistered();
            int nodeIndex = _trie.FindIndex(pathHash);

            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return;

            _data.GetActiveTypes(nodeIndex, outList);
        }

        public RedDotState GetState(int pathHash)
        {
            if (pathHash == 0)
                return new RedDotState(0, 0, 0, 0);

            EnsureRegistered();
            int nodeIndex = _trie.FindIndex(pathHash);

            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return new RedDotState(pathHash, 0, 0, 0);

            return _data.GetState(nodeIndex, pathHash);
        }

        /// <summary>获取路径的聚合红点计数（自身 + 子孙），O(1)。</summary>
        public int GetRedDot(int pathHash)
        {
            if (pathHash == 0)
                return 0;

            EnsureRegistered();
            int nodeIndex = _trie.FindIndex(pathHash);

            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return 0;

            return _data.GetTotalCount(nodeIndex);
        }

        /// <summary>获取路径的自身红点计数（不含子孙）。</summary>
        public int GetSelfRedDot(int pathHash)
        {
            if (pathHash == 0)
                return 0;

            EnsureRegistered();
            int nodeIndex = _trie.FindIndex(pathHash);

            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return 0;

            return _data.GetSelfCount(nodeIndex);
        }

        /// <summary>
        /// 订阅红点变化，注册时立即回调一次当前状态。
        /// </summary>
        public void AddListener(int pathHash, Action<RedDotState> callback)
        {
            if (callback == null)
                return;

            if (pathHash == 0)
            {
                callback.Invoke(new RedDotState(0, 0, 0, 0));
                return;
            }

            EnsureRegistered();

            int nodeIndex = _trie.FindIndex(pathHash);
            if (nodeIndex == RedDotTrie.INVALID_INDEX)
            {
                Debug.LogWarning($"[RedDotManager] AddListener: pathHash=0x{pathHash:X8} not registered");
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

        public void RemoveListener(int pathHash, Action<RedDotState> callback)
        {
            if (callback == null)
                return;

            if (pathHash == 0)
                return;

            EnsureRegistered();

            int nodeIndex = _trie.FindIndex(pathHash);

            if (nodeIndex == RedDotTrie.INVALID_INDEX)
                return;

            _data.RemoveListener(nodeIndex, callback);
        }

        #region 动态节点

        // 动态节点 = 父节点 hash + 业务 int ID，通过 RedDotHash.ComputeDynamic 计算子节点 hash，零字符串分配。
        // 适用于运行时动态创建的红点，如邮件第 N 封、背包第 N 格、活动第 N 期。

        /// <summary>注册动态子节点。parentHash 如 RedDotPaths.Root_Mail，childId 如 1001。</summary>
        public int RegisterDynamicNode(int parentHash, int childId)
        {
            EnsureRegistered();
            int pathHash = RedDotHash.ComputeDynamic(parentHash, childId);
            return RegisterNode(pathHash, parentHash, isStatic: false);
        }

        /// <summary>设置动态子节点红点。</summary>
        public void SetRedDot(int parentHash, int childId, int count, RedDotType type = RedDotType.Normal)
        {
            int pathHash = RedDotHash.ComputeDynamic(parentHash, childId);
            SetRedDot(pathHash, count, type);
        }

        /// <summary>查询动态子节点 TotalCount。</summary>
        public int GetRedDot(int parentHash, int childId)
        {
            int pathHash = RedDotHash.ComputeDynamic(parentHash, childId);
            return GetRedDot(pathHash);
        }

        /// <summary>查询动态子节点 SelfCount。</summary>
        public int GetSelfRedDot(int parentHash, int childId)
        {
            int pathHash = RedDotHash.ComputeDynamic(parentHash, childId);
            return GetSelfRedDot(pathHash);
        }

        /// <summary>获取动态子节点状态快照。</summary>
        public RedDotState GetState(int parentHash, int childId)
        {
            int pathHash = RedDotHash.ComputeDynamic(parentHash, childId);
            return GetState(pathHash);
        }

        /// <summary>清零动态子节点（递归清理：静态子孙清零，动态子孙移除）。</summary>
        public void ClearNode(int parentHash, int childId)
        {
            int pathHash = RedDotHash.ComputeDynamic(parentHash, childId);
            ClearNodeRecursive(pathHash);
        }

        /// <summary>订阅动态子节点变化，注册时立即回调当前状态。</summary>
        public void AddListener(int parentHash, int childId, Action<RedDotState> callback)
        {
            int pathHash = RedDotHash.ComputeDynamic(parentHash, childId);
            AddListener(pathHash, callback);
        }

        /// <summary>取消订阅动态子节点。</summary>
        public void RemoveListener(int parentHash, int childId, Action<RedDotState> callback)
        {
            int pathHash = RedDotHash.ComputeDynamic(parentHash, childId);
            RemoveListener(pathHash, callback);
        }

        #endregion

        /// <summary>清零自身 SelfCount（不递归，子节点不受影响）。</summary>
        public void ClearNode(int pathHash)
        {
            if (pathHash == 0)
                return;

            SetRedDot(pathHash, 0);
        }

        /// <summary>
        /// 递归清理子树：静态节点 SelfCount → 0，动态节点 RemoveDynamicLeafNode。
        /// 移除失败（如有监听器）则回退到 SetRedDot(hash, 0)。
        /// </summary>
        public void ClearNodeRecursive(int pathHash)
        {
            if (pathHash == 0) return;
            EnsureRegistered();

            int nodeIndex = _trie.FindIndex(pathHash);
            if (nodeIndex == RedDotTrie.INVALID_INDEX) return;

            _childrenBuffer.Clear();
            _trie.GetChildren(nodeIndex, _childrenBuffer);

            // 先递归清理子节点
            for (int i = _childrenBuffer.Count - 1; i >= 0; i--)
            {
                int childIdx = _childrenBuffer[i];
                int childHash = _trie.GetPathHash(childIdx);
                ClearNodeRecursive(childHash);
            }

            // 清理自身：静态清零，动态移除（移除失败则回退到清零）
            if (_staticPathHashes.Contains(pathHash))
            {
                SetRedDot(pathHash, 0);
            }
            else if (!RemoveDynamicLeafNode(pathHash))
            {
                SetRedDot(pathHash, 0);
            }
        }

        /// <summary>移除运行时动态创建的叶子节点。静态生成路径请使用 ClearNode。</summary>
        public bool RemoveDynamicLeafNode(int pathHash)
        {
            if (pathHash == 0)
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
                _data.ResetNode(nodeIndex);

            return removed;
        }

        /// <summary>重置整个红点系统（慎用）</summary>
        public void ResetAll()
        {
            Initialize();
            _trie.Clear();
            _data.Clear();
            _staticPathHashes.Clear();
            _registered = false;
            EnsureRegistered();
        }
    }
}
