using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace RedDot.Editor
{
    public class RedDotMonitor : EditorWindow
    {
        private class TreeNode
        {
            public string Name;
            public string FullPath;
            public int PathHash;
            public int Depth;
            public int SelfCount;
            public int TotalCount;
            public int ListenerCount;
            public RedDotType DisplayType;
            public bool IsExpanded = true;
            public List<TreeNode> Children = new List<TreeNode>();
        }

        private Vector2 _scrollPosition;
        private string _searchFilter = "";
        private bool _autoRefresh = true;
        private double _lastRefreshTime;
        private const double RefreshInterval = 0.3;

        private TreeNode _treeRoot;
        private int _totalNodeCount;
        private int _activeNodeCount;

        private RedDotPathDefinition _pathDefinition;
        private Dictionary<int, string> _hashToDisplayName = new Dictionary<int, string>();
        private HashSet<string> _collapsedPaths = new HashSet<string>();

        private static readonly Color ColorActive = new Color(0.9f, 0.2f, 0.2f);
        private static readonly Color ColorInactive = new Color(0.35f, 0.35f, 0.35f);
        private static readonly Color ColorRowEven = new Color(0.22f, 0.22f, 0.22f);
        private static readonly Color ColorRowOdd = new Color(0.25f, 0.25f, 0.25f);
        private static readonly Color ColorRowHighlight = new Color(0.35f, 0.10f, 0.10f);

        [MenuItem("Tools/RedDot/Monitor")]
        public static void ShowWindow()
        {
            var window = GetWindow<RedDotMonitor>("RedDot Monitor");
            window.minSize = new Vector2(560, 350);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.update += OnEditorTick;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorTick;
        }

        private void OnEditorTick()
        {
            if (!_autoRefresh) return;
            if (EditorApplication.timeSinceStartup - _lastRefreshTime < RefreshInterval) return;
            _lastRefreshTime = EditorApplication.timeSinceStartup;
            Repaint();
        }

        private void OnGUI()
        {
            if (!Application.isPlaying) { DrawIdleMessage(); return; }
            if (!RedDotManager.HasInstance) { EditorGUILayout.HelpBox("No RedDotManager.", MessageType.Warning); return; }

            LoadDisplayNames();
            BuildTree();
            DrawToolbar();
            DrawStatsBar();
            DrawTreeView();
        }

        private void DrawIdleMessage()
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label("Enter Play Mode to monitor red dots", EditorStyles.centeredGreyMiniLabel);
            GUILayout.FlexibleSpace();
        }

        // ==================== Toolbar ====================

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            Color savedColor = GUI.color;
            GUI.color = _autoRefresh ? Color.green : Color.gray;
            _autoRefresh = GUILayout.Toggle(_autoRefresh, "● Live", EditorStyles.toolbarButton, GUILayout.Width(56));
            GUI.color = savedColor;

            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(50)))
            {
                LoadDisplayNames();
                _collapsedPaths.Clear();
            }

            GUILayout.FlexibleSpace();

            _searchFilter = EditorGUILayout.TextField(_searchFilter, EditorStyles.toolbarSearchField, GUILayout.Width(140));
            if (!string.IsNullOrEmpty(_searchFilter) && GUILayout.Button("✕", EditorStyles.toolbarButton, GUILayout.Width(20)))
            {
                _searchFilter = "";
                GUI.FocusControl(null);
            }

            EditorGUILayout.EndHorizontal();
        }

        // ==================== Stats ====================

        private void DrawStatsBar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            DrawStatBadge("Total Nodes", _totalNodeCount);
            DrawStatBadge("Active Nodes", _activeNodeCount, _activeNodeCount > 0 ? ColorActive : ColorInactive);

            int rootTotal = 0;
            if (_treeRoot != null)
            {
                foreach (var child in _treeRoot.Children) rootTotal += child.TotalCount;
            }
            DrawStatBadge("Root Total", rootTotal, rootTotal > 0 ? ColorActive : ColorInactive);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
        }

        private void DrawStatBadge(string label, int value, Color? valueColor = null)
        {
            GUILayout.Label($"{label}: ", EditorStyles.miniLabel);
            GUIStyle badgeStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13 };
            Color savedColor = GUI.color;
            if (valueColor.HasValue) GUI.color = valueColor.Value;
            GUILayout.Label(value.ToString(), badgeStyle, GUILayout.MinWidth(30));
            GUI.color = savedColor;
            GUILayout.Space(10);
        }

        // ==================== Tree ====================

        // Column layout (pixels from left of row)
        private const float ColumnPath = 0;
        private const float ColumnSelf = 300;
        private const float ColumnTotal = 360;
        private const float ColumnType = 420;
        private const float ColumnListeners = 500;

        private void DrawTreeView()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("Path", GUILayout.MinWidth(ColumnSelf - ColumnPath));
            GUILayout.Space(ColumnTotal - ColumnSelf - 60 + 20);
            GUILayout.Label("Self", GUILayout.Width(ColumnTotal - ColumnSelf));
            GUILayout.Label("Total", GUILayout.Width(ColumnType - ColumnTotal));
            GUILayout.Label("Type", GUILayout.Width(ColumnListeners - ColumnType));
            GUILayout.Label("Listeners", GUILayout.Width(60));
            EditorGUILayout.EndHorizontal();

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

            if (_treeRoot == null || _treeRoot.Children.Count == 0)
            {
                EditorGUILayout.HelpBox("No paths registered. Call RedDotPathsRegistration.RegisterAll() at startup.", MessageType.Info);
            }
            else
            {
                int rowIndex = 0;
                foreach (var child in _treeRoot.Children)
                {
                    DrawTreeNodeRecursive(child, ref rowIndex);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        // Data column positions (pixels from right edge of window)
        private const float DataColumnWidth = 250; // total width of Self+Total+Type+Listeners

        private void DrawTreeNodeRecursive(TreeNode node, ref int rowIndex)
        {
            if (!string.IsNullOrEmpty(_searchFilter)
                && !node.FullPath.ToLowerInvariant().Contains(_searchFilter.ToLowerInvariant()))
            {
                foreach (var child in node.Children) DrawTreeNodeRecursive(child, ref rowIndex);
                return;
            }

            Color savedColor = GUI.color;
            bool isActive = node.TotalCount > 0;
            bool hasChildren = node.Children.Count > 0;

            // Row rect
            Rect rowRect = EditorGUILayout.BeginHorizontal();
            Color bgColor = isActive ? ColorRowHighlight
                : (rowIndex % 2 == 0 ? ColorRowEven : ColorRowOdd);
            EditorGUI.DrawRect(rowRect, bgColor);
            rowIndex++;

            // Tree controls
            GUILayout.Space(node.Depth * 14f);
            if (hasChildren)
            {
                string arrow = node.IsExpanded ? "▼" : "▶";
                if (GUILayout.Button(arrow, EditorStyles.label, GUILayout.Width(14), GUILayout.Height(16)))
                {
                    node.IsExpanded = !node.IsExpanded;
                    if (node.IsExpanded) _collapsedPaths.Remove(node.FullPath);
                    else _collapsedPaths.Add(node.FullPath);
                }
            }
            else GUILayout.Space(14f);

            GUI.color = isActive ? ColorActive : ColorInactive;
            GUILayout.Label("●", GUILayout.Width(16));
            GUI.color = savedColor;

            GUIStyle nameStyle = new GUIStyle(EditorStyles.label) { fontStyle = isActive ? FontStyle.Bold : FontStyle.Normal };
            GUILayout.Label(node.Name, nameStyle);

            EditorGUILayout.EndHorizontal();

            // Draw data columns at absolute positions (from row right edge, independent of tree indent)
            float rightEdge = rowRect.xMax;
            float columnX = rightEdge - DataColumnWidth;
            float y = rowRect.y;
            float h = rowRect.height;

            Rect selfRect = new Rect(columnX, y, 55, h);
            Rect totalRect = new Rect(columnX + 55, y, 55, h);
            Rect typeRect = new Rect(columnX + 110, y, 75, h);
            Rect lsnRect = new Rect(columnX + 185, y, 65, h);

            GUI.color = node.SelfCount > 0 ? ColorActive : ColorInactive;
            GUI.Label(selfRect, node.SelfCount > 0 ? node.SelfCount.ToString() : "-");

            GUI.color = isActive ? ColorActive : ColorInactive;
            GUI.Label(totalRect, isActive ? node.TotalCount.ToString() : "-");

            GUI.color = node.DisplayType != 0 ? ColorActive : ColorInactive;
            GUI.Label(typeRect, node.DisplayType != 0 ? node.DisplayType.ToString() : "-");

            GUI.color = node.ListenerCount > 0 ? ColorActive : ColorInactive;
            GUI.Label(lsnRect, node.ListenerCount > 0 ? node.ListenerCount.ToString() : "-");

            GUI.color = savedColor;

            if (hasChildren && node.IsExpanded)
            {
                foreach (var child in node.Children) DrawTreeNodeRecursive(child, ref rowIndex);
            }
        }

        // ==================== Data ====================

        private void LoadDisplayNames()
        {
            if (_pathDefinition == null)
            {
                _pathDefinition = AssetDatabase.LoadAssetAtPath<RedDotPathDefinition>(
                    "Assets/Scripts/RedDot/RedDotPathDefinition.asset");
            }
            if (_pathDefinition == null) return;

            _hashToDisplayName.Clear();
            foreach (var entry in _pathDefinition.Paths)
            {
                int lastUnderscoreIndex = entry.Path.LastIndexOf('_');
                string shortName = lastUnderscoreIndex >= 0
                    ? entry.Path.Substring(lastUnderscoreIndex + 1)
                    : entry.Path;
                _hashToDisplayName[entry.Hash] = shortName;
            }
        }

        private void BuildTree()
        {
            RedDotTrie trie = RedDotManager.Instance.Trie;
            RedDotDataStore dataStore = GetDataStore();
            if (dataStore == null) return;

            _totalNodeCount = trie.NodeCount - 1;
            _activeNodeCount = 0;
            _treeRoot = new TreeNode { Name = "Root", FullPath = "", Depth = -1 };

            Dictionary<int, TreeNode> nodeMap = new Dictionary<int, TreeNode>();

            for (int nodeIndex = 1; nodeIndex < trie.NodeCount; nodeIndex++)
            {
                int pathHash = trie.GetPathHash(nodeIndex);

                string fullPath = _hashToDisplayName.TryGetValue(pathHash, out string fullName)
                    ? fullName : $"0x{pathHash:X8}";

                string displayName = fullPath;
                int lastUnderscoreIndex = displayName.LastIndexOf('_');
                if (lastUnderscoreIndex >= 0)
                {
                    displayName = displayName.Substring(lastUnderscoreIndex + 1);
                }

                int totalCount = dataStore.GetTotalCount(nodeIndex);
                if (totalCount > 0) _activeNodeCount++;

                nodeMap[pathHash] = new TreeNode
                {
                    Name = displayName,
                    FullPath = fullPath,
                    PathHash = pathHash,
                    Depth = CalculateDepth(trie, nodeIndex),
                    SelfCount = dataStore.GetSelfCount(nodeIndex),
                    TotalCount = totalCount,
                    ListenerCount = dataStore.ListenerCount(nodeIndex),
                    DisplayType = dataStore.GetHighestType(nodeIndex),
                    IsExpanded = !_collapsedPaths.Contains(fullPath)
                };
            }

            // Build parent-child links
            foreach (var pair in nodeMap)
            {
                int nodeIndex = trie.FindIndex(pair.Key);
                int parentIndex = trie.GetParentIndex(nodeIndex);
                int parentPathHash = parentIndex != RedDotTrie.INVALID_INDEX ? trie.GetPathHash(parentIndex) : 0;

                if (parentPathHash == 0 || !nodeMap.ContainsKey(parentPathHash))
                {
                    _treeRoot.Children.Add(pair.Value);
                }
                else
                {
                    nodeMap[parentPathHash].Children.Add(pair.Value);
                }
            }
        }

        private static RedDotDataStore GetDataStore()
        {
            System.Reflection.FieldInfo field = typeof(RedDotManager).GetField("_data",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            return field?.GetValue(RedDotManager.Instance) as RedDotDataStore;
        }

        private static int CalculateDepth(RedDotTrie trie, int nodeIndex)
        {
            int depth = 0;
            while (nodeIndex > 0)
            {
                nodeIndex = trie.GetParentIndex(nodeIndex);
                if (nodeIndex > 0) depth++;
            }
            return depth;
        }
    }
}
