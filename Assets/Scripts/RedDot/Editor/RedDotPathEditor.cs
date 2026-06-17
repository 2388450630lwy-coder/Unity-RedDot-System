using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace RedDot.Editor
{
    public class RedDotPathEditor : EditorWindow
    {
        private class TreeNode
        {
            public string Name;
            public string FullPath;
            public int Index = -1;
            public List<TreeNode> Children = new();
            public bool Expanded = true;
            public int Depth;
            public bool IsLastChild;
        }

        private RedDotPathDefinition _def;
        private Vector2 _treeScroll, _detailScroll;
        private string _search = "";

        // 添加表单
        private string _addName = "";
        private string _addComment = "";
        private string _addParent = ""; // 空 = 根级别

        private TreeNode _root;
        private string _selectedPath;
        private Dictionary<string, TreeNode> _nodeMap = new();
        private HashSet<string> _collapsed = new();
        private string _toast;
        private double _toastUntil;
        private int _visibleRows;
        private bool _isDirty;

        private GUIStyle _sectionLabel;
        private GUIStyle _branchName;
        private GUIStyle _leafName;

        private static readonly Color SelBgColor = new(0.22f, 0.42f, 0.72f, 0.45f);
        private static readonly Color RowBgColor = new(0, 0, 0, 0.04f);
        private static readonly Color SearchMatchBg = new(0.22f, 0.5f, 0.1f, 0.22f);

        [MenuItem("Tools/RedDot/红点路径编辑器")]
        public static void Open()
        {
            var w = GetWindow<RedDotPathEditor>("红点路径编辑器");
            w.minSize = new Vector2(750, 500);
            w.Show();
        }

        private void OnEnable()
        {
            string guid = EditorPrefs.GetString("RedDot.PathDefinition.GUID", "");
            if (!string.IsNullOrEmpty(guid))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                _def = AssetDatabase.LoadAssetAtPath<RedDotPathDefinition>(p);
            }
        }

        private void OnGUI()
        {
            EnsureStyles();
            DrawHeader();
            if (_def == null) { DrawEmptyState(); return; }

            RebuildTree();

            // 选中节点变化 → 自动填入父路径
            var sel = SelectedNode;
            if (sel != null && sel.Index >= 0 && _selectedPath != _addParent)
            {
                // 不强制覆盖用户手动改的父路径
            }

            DrawToolbar();

            // 左侧树占 40%，最少 200px，最多不限
            float leftRatio = 0.40f;
            float treeWidth = Mathf.Max(200f, position.width * leftRatio);
            // 如果窗口较宽，左侧不宜过大
            if (treeWidth > 420f) treeWidth = 420f;

            EditorGUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            DrawTreePanel(treeWidth);
            DrawDivider();
            DrawRightPanel();
            EditorGUILayout.EndHorizontal();

            DrawToast();
            DrawGenerationFoldout();
        }

        // ═══ 顶栏 ═══

        private void DrawHeader()
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("红点路径编辑器", EditorStyles.boldLabel, GUILayout.Width(110));

            EditorGUI.BeginChangeCheck();
            _def = (RedDotPathDefinition)EditorGUILayout.ObjectField(
                _def, typeof(RedDotPathDefinition), false, GUILayout.MinWidth(160), GUILayout.ExpandWidth(true));
            if (EditorGUI.EndChangeCheck())
            {
                _root = null; _selectedPath = null; _isDirty = false;
                if (_def != null)
                {
                    EditorPrefs.SetString("RedDot.PathDefinition.GUID",
                        AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_def)));
                    Toast($"已加载 {_def.Paths.Count} 条路径");
                }
                else EditorPrefs.DeleteKey("RedDot.PathDefinition.GUID");
            }

            if (_isDirty) { var c = GUI.color; GUI.color = new Color(1f, 0.7f, 0.2f); GUILayout.Label("●", GUILayout.Width(14)); GUI.color = c; }
            if (_def != null && GUILayout.Button("保存", EditorStyles.miniButton, GUILayout.Width(40)))
            {
                EditorUtility.SetDirty(_def); AssetDatabase.SaveAssets(); _isDirty = false; Toast("已保存");
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawEmptyState()
        {
            GUILayout.FlexibleSpace();
            EditorGUILayout.HelpBox("尚未分配 RedDotPathDefinition。\n在 Project 中右键 → Create → RedDot → Path Definition，拖入上方字段。", MessageType.Info);
            if (GUILayout.Button("创建新的路径定义", GUILayout.Height(32))) CreateDefinition();
            GUILayout.FlexibleSpace();
        }

        // ═══ 工具栏 ═══

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            string ns = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField,
                GUILayout.MinWidth(80), GUILayout.MaxWidth(180), GUILayout.ExpandWidth(true));
            if (ns != _search) { _search = ns; if (!string.IsNullOrEmpty(_search)) ExpandAll(_root); }
            if (!string.IsNullOrEmpty(_search) && GUILayout.Button("✕", EditorStyles.toolbarButton, GUILayout.Width(20))) _search = "";

            GUILayout.Space(2);
            if (GUILayout.Button("展开", EditorStyles.toolbarButton, GUILayout.Width(36))) ExpandAll(_root);
            if (GUILayout.Button("收起", EditorStyles.toolbarButton, GUILayout.Width(36))) CollapseAll(_root);

            GUILayout.FlexibleSpace();
            GUILayout.Label($"共 {_def.Paths.Count} 条  可见 {_visibleRows}", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
            if (GUILayout.Button("排序", EditorStyles.toolbarButton, GUILayout.Width(36)))
            {
                Undo.RecordObject(_def, "排序路径");
                var s = _def.Paths.OrderBy(p => DepthOf(p.Path)).ThenBy(p => p.Path).ToList();
                _def.Paths.Clear(); _def.Paths.AddRange(s); MarkDirty(); _root = null; Toast("已排序");
            }
            EditorGUILayout.EndHorizontal();
        }

        // ═══ 左侧：路径树 ═══

        private void DrawTreePanel(float width)
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(width));
            _treeScroll = EditorGUILayout.BeginScrollView(_treeScroll);
            _visibleRows = 0;

            if (_root != null)
            {
                int idx = 0;
                foreach (var c in _root.Children) DrawNodeRow(c, ref idx);
            }

            if (_def.Paths.Count > 0 && _visibleRows == 0 && !string.IsNullOrEmpty(_search))
            {
                EditorGUILayout.Space(12);
                EditorGUILayout.HelpBox($"没有匹配 \"{_search}\" 的路径", MessageType.Info);
                if (GUILayout.Button("清除搜索")) _search = "";
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawNodeRow(TreeNode node, ref int rowIdx)
        {
            bool matchSelf = string.IsNullOrEmpty(_search)
                || node.FullPath.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0;
            bool matchChild = HasMatchingChild(node);

            if (!matchSelf && !matchChild)
            {
                if (node.Expanded) foreach (var c in node.Children) DrawNodeRow(c, ref rowIdx);
                return;
            }

            bool isSel = _selectedPath == node.FullPath;
            bool hasKids = node.Children.Count > 0;

            Color bg = isSel ? SelBgColor : (rowIdx % 2 == 1) ? RowBgColor : Color.clear;
            if (matchSelf && !string.IsNullOrEmpty(_search)) bg = Color.Lerp(bg, SearchMatchBg, 0.7f);

            Rect rr = EditorGUILayout.BeginHorizontal(GUILayout.Height(19));
            if (bg != Color.clear) EditorGUI.DrawRect(rr, bg);

            GUILayout.Space(node.Depth * 14f);
            if (node.Depth > 0)
            {
                var ls = new GUIStyle(EditorStyles.label)
                {
                    normal = { textColor = new Color(0.35f, 0.35f, 0.35f) },
                    alignment = TextAnchor.MiddleRight, padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0)
                };
                GUILayout.Label(node.IsLastChild ? "└" : "├", ls, GUILayout.Width(10));
            }

            if (hasKids)
            {
                string arrow = node.Expanded ? "▼" : "▶";
                if (GUILayout.Button(arrow, EditorStyles.label, GUILayout.Width(14), GUILayout.Height(17)))
                {
                    node.Expanded = !node.Expanded;
                    if (node.Expanded) _collapsed.Remove(node.FullPath); else _collapsed.Add(node.FullPath);
                }
            }
            else GUILayout.Space(16);

            var ns = hasKids ? _branchName : _leafName;
            if (GUILayout.Button(new GUIContent(node.Name, node.FullPath), ns,
                GUILayout.Height(17), GUILayout.ExpandWidth(true)))
            {
                _selectedPath = node.FullPath;
                _addParent = node.FullPath;
            }

            if (hasKids)
            {
                var badge = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(0.45f, 0.45f, 0.45f) }, alignment = TextAnchor.MiddleRight, fontSize = 10 };
                GUILayout.Label(node.Children.Count.ToString(), badge, GUILayout.Width(18));
            }

            if (rr.Contains(Event.current.mousePosition) && Event.current.type == EventType.MouseDown && Event.current.button == 1)
            {
                _selectedPath = node.FullPath;
                ShowNodeMenu(node);
                Event.current.Use();
            }

            EditorGUILayout.EndHorizontal();
            rowIdx++; _visibleRows++;
            if (node.Expanded) foreach (var c in node.Children) DrawNodeRow(c, ref rowIdx);
        }

        private void DrawDivider()
        {
            Rect r = EditorGUILayout.GetControlRect(GUILayout.Width(1), GUILayout.ExpandHeight(true));
            EditorGUI.DrawRect(r, new Color(0.25f, 0.25f, 0.25f));
        }

        // ═══ 右侧面板 ═══

        private TreeNode SelectedNode
            => !string.IsNullOrEmpty(_selectedPath) && _nodeMap.TryGetValue(_selectedPath, out var n) ? n : null;

        private void DrawRightPanel()
        {
            EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            _detailScroll = EditorGUILayout.BeginScrollView(_detailScroll, GUILayout.ExpandHeight(true));

            var sel = SelectedNode;

            if (sel != null && sel.Index >= 0)
                DrawNodeInspector(sel);

            // 分隔
            EditorGUILayout.Space(4);
            var sepRect = EditorGUILayout.GetControlRect(GUILayout.Height(1));
            EditorGUI.DrawRect(sepRect, new Color(0.3f, 0.3f, 0.3f, 0.5f));
            EditorGUILayout.Space(4);

            DrawAddForm(sel);

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        // ── Inspector ──

        private void DrawNodeInspector(TreeNode node)
        {
            var entry = _def.Paths[node.Index];

            // 标题行
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("节点详情", _sectionLabel);
            GUILayout.FlexibleSpace();

            var st = new HashSet<string>(); CollectSubtree(node, st);
            GUILayout.Label($"深度{node.Depth}  子{node.Children.Count}  孙{st.Count - 1}", EditorStyles.miniLabel);

            if (GUILayout.Button("取消", EditorStyles.miniButton, GUILayout.Width(36)))
            { _selectedPath = null; _addParent = ""; }
            EditorGUILayout.EndHorizontal();

            // 面包屑路径（紧凑）
            var parts = node.FullPath.Split('_');
            var pathStr = new StringBuilder();
            for (int i = 0; i < parts.Length; i++) { if (i > 0) pathStr.Append(" ▸ "); pathStr.Append(parts[i]); }
            EditorGUILayout.LabelField(pathStr.ToString(), EditorStyles.miniLabel);
            EditorGUILayout.Space(2);

            // 名称（Delayed：按回车或失焦才提交，不会每打一字就触发重命名）
            EditorGUI.BeginChangeCheck();
            string newName = EditorGUILayout.DelayedTextField("名称", node.Name);
            if (EditorGUI.EndChangeCheck() && !string.IsNullOrWhiteSpace(newName) && newName != node.Name)
            {
                Undo.RecordObject(_def, "重命名");
                if (RenameNode(node, newName))
                { _selectedPath = null; _addParent = ""; _root = null; MarkDirty(); Toast("已重命名 → " + newName); }
            }

            // Hash（左侧与注释对齐）
            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField("Hash", $"0x{entry.Hash:X16}");
            EditorGUI.EndDisabledGroup();
            if (GUILayout.Button("复制", EditorStyles.miniButton, GUILayout.Width(36)))
            { EditorGUIUtility.systemCopyBuffer = entry.Hash.ToString(); Toast("Hash 已复制"); }
            EditorGUILayout.EndHorizontal();

            // 注释
            EditorGUI.BeginChangeCheck();
            string newComment = EditorGUILayout.TextField("注释", entry.Comment);
            if (EditorGUI.EndChangeCheck() && newComment != entry.Comment)
            { Undo.RecordObject(_def, "编辑注释"); _def.Paths[node.Index] = new RedDotPathEntry(entry.Path, entry.Hash, newComment); MarkDirty(); }

            EditorGUILayout.Space(4);

            // 操作按钮（统一高度）
            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = new Color(0.38f, 0.78f, 0.38f);
            if (GUILayout.Button("添加子路径", GUILayout.Height(22)))
            { _addParent = node.FullPath; _addName = ""; _addComment = ""; GUI.FocusControl("add_name"); }
            GUI.backgroundColor = Color.white;

            GUI.backgroundColor = new Color(1f, 0.3f, 0.25f);
            if (GUILayout.Button("删除此节点", GUILayout.Height(22)))
            { DeleteNode(node); GUIUtility.ExitGUI(); }
            GUI.backgroundColor = Color.white;

            if (GUILayout.Button("复制路径", GUILayout.Height(22)))
            { EditorGUIUtility.systemCopyBuffer = node.FullPath; Toast("已复制"); }
            EditorGUILayout.EndHorizontal();
        }

        // ── 添加表单 ──

        private void DrawAddForm(TreeNode selectedNode)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // 父路径选择
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("添加到", _sectionLabel, GUILayout.Width(50));
            string display = string.IsNullOrEmpty(_addParent) ? "根节点 (Root)" : _addParent;
            if (EditorGUILayout.DropdownButton(new GUIContent(display), FocusType.Passive, GUILayout.ExpandWidth(true)))
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("根节点 (Root)"), string.IsNullOrEmpty(_addParent), () => _addParent = "");
                menu.AddSeparator("");
                foreach (var p in _def.Paths.OrderBy(x => DepthOf(x.Path)).ThenBy(x => x.Path))
                {
                    string pth = p.Path;
                    menu.AddItem(new GUIContent(pth.Replace("_", " ▸ ")), _addParent == pth, () => _addParent = pth);
                }
                menu.ShowAsContext();
            }
            bool canUseSelected = selectedNode != null && selectedNode.Index >= 0 && _addParent != selectedNode.FullPath;
            GUI.enabled = canUseSelected;
            string btnLabel = canUseSelected ? $"→ {selectedNode?.Name}" : "";
            if (GUILayout.Button(btnLabel, EditorStyles.miniButton, GUILayout.Width(60)))
            {
                if (canUseSelected) _addParent = selectedNode.FullPath;
            }
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            // 名称
            EditorGUI.BeginChangeCheck();
            _addName = EditorGUILayout.TextArea(_addName, GUILayout.Height(48), GUILayout.ExpandWidth(true));
            bool nameChanged = EditorGUI.EndChangeCheck();

            // 注释
            _addComment = EditorGUILayout.TextField("注释", _addComment, GUILayout.ExpandWidth(true));

            // 按钮
            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = new Color(0.35f, 0.78f, 0.35f);
            if (GUILayout.Button("添加", GUILayout.Height(24), GUILayout.Width(50))) DoAddPaths();
            GUI.backgroundColor = Color.white;
            if (GUILayout.Button("清空", GUILayout.Height(24), GUILayout.Width(40)))
            { _addName = ""; _addComment = ""; _addParent = ""; }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("批量导入", GUILayout.Height(24))) BatchImport();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
        }

        private void DoAddPaths()
        {
            string text = _addName.Trim();
            if (string.IsNullOrEmpty(text)) { Toast("请输入路径名称"); return; }

            Undo.RecordObject(_def, "添加路径");
            int added = 0;
            string firstNew = null;

            foreach (string line in text.Split('\n'))
            {
                string seg = line.Trim();
                if (string.IsNullOrEmpty(seg)) continue;
                seg = seg.Replace("/", "_").Replace(" ", "_").Replace("-", "_");
                string[] parts = seg.Split('_');
                string curParent = _addParent;

                for (int i = 0; i < parts.Length; i++)
                {
                    string p = parts[i];
                    if (string.IsNullOrEmpty(p)) continue;
                    string full = string.IsNullOrEmpty(curParent) ? p : $"{curParent}_{p}";

                    // 优先用路径字符串判断是否已存在，避免哈希碰撞时误判
                    if (_def.Paths.Any(x => x.Path == full)) { curParent = full; continue; }

                    long hash = RedDotHash.Compute(full);

                    // 检测哈希碰撞：不同路径产生了相同 hash
                    int colIdx = _def.Paths.FindIndex(x => x.Hash == hash);
                    if (colIdx >= 0)
                    {
                        Toast($"Hash 冲突！'{full}' 与 '{_def.Paths[colIdx].Path}' hash 相同 (0x{hash:X16})，已跳过");
                        continue;
                    }

                    string cmt = (i == parts.Length - 1) ? _addComment.Trim() : "";
                    _def.Paths.Add(new RedDotPathEntry(full, hash, cmt));
                    curParent = full; added++; firstNew ??= full;
                }
            }

            MarkDirty(); _root = null;
            _addName = ""; _addComment = "";
            Toast(added > 0 ? $"已添加 {added} 条路径" : "路径已全部存在");
            if (firstNew != null) _selectedPath = firstNew;
        }

        // ═══ 提示栏 ═══

        private void DrawToast()
        {
            if (string.IsNullOrEmpty(_toast) || EditorApplication.timeSinceStartup > _toastUntil) return;
            float a = Mathf.Clamp01((float)(_toastUntil - EditorApplication.timeSinceStartup) / 0.4f);
            var c = GUI.color; GUI.color = new Color(0.35f, 0.85f, 0.35f, a);
            EditorGUILayout.LabelField(_toast, EditorStyles.centeredGreyMiniLabel);
            GUI.color = c;
        }

        // ═══ 代码生成 ═══

        private void DrawGenerationFoldout()
        {
            EditorGUILayout.Space(2);
            GUILayout.Label("代码生成", _sectionLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("重算 Hash", GUILayout.Height(22))) { _def.RecalculateHashes(); MarkDirty(); Toast("Hash 已重算"); }
            if (GUILayout.Button("校验", GUILayout.Height(22)))
            {
                if (_def.Validate(out var e)) EditorUtility.DisplayDialog("校验通过", $"全部 {_def.Paths.Count} 条有效。", "确定");
                else EditorUtility.DisplayDialog("校验发现问题", string.Join("\n", e.Take(15)) + (e.Count > 15 ? $"\n…还有 {e.Count - 15} 条" : ""), "确定");
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            GUI.enabled = _def.Paths.Count > 0;
            if (GUILayout.Button("生成常量", GUILayout.Height(22))) Gen(GenerateConstantsCode, _def.OutputPath, "常量");
            if (GUILayout.Button("生成注册", GUILayout.Height(22))) Gen(GenerateRegistrationCode, _def.RegistrationOutputPath, "注册");
            GUI.enabled = true;
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("▶ 一键生成全部", GUILayout.Height(30)))
            {
                string ap = AssetDatabase.GetAssetPath(_def);
                if (!string.IsNullOrEmpty(ap)) _def = AssetDatabase.LoadAssetAtPath<RedDotPathDefinition>(ap);
                _def.RecalculateHashes();
                if (!_def.Validate(out var e)) { EditorUtility.DisplayDialog("校验失败", string.Join("\n", e.Take(15)), "确定"); GUI.enabled = true; return; }
                MarkDirty(); Gen(GenerateConstantsCode, _def.OutputPath, "常量"); Gen(GenerateRegistrationCode, _def.RegistrationOutputPath, "注册");
                AssetDatabase.Refresh(); Toast("代码生成完毕");
            }
        }

        private void Gen(Func<string> build, string relPath, string label)
        {
            string code = build(), full = Path.Combine(Application.dataPath, relPath);
            string dir = Path.GetDirectoryName(full); if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, code, new UTF8Encoding(true));
            Toast($"{label} → {relPath}");
        }

        // ═══ 操作 ═══

        private void BatchImport()
        {
            string text = EditorGUIUtility.systemCopyBuffer?.Trim();
            if (string.IsNullOrEmpty(text)) { Toast("剪贴板为空"); return; }
            Undo.RecordObject(_def, "批量导入");
            int added = 0, skipped = 0;
            foreach (string line in text.Split('\n'))
            {
                string path = line.Trim(); if (string.IsNullOrEmpty(path)) continue;
                path = path.Replace("/", "_").Replace(" ", "_");

                // 优先用路径字符串判断是否已存在，避免哈希碰撞时误判
                if (_def.Paths.Any(x => x.Path == path)) { skipped++; continue; }

                long hash = RedDotHash.Compute(path);

                // 检测哈希碰撞：不同路径产生了相同 hash
                int colIdx = _def.Paths.FindIndex(x => x.Hash == hash);
                if (colIdx >= 0)
                {
                    Debug.LogWarning($"[RedDotPathEditor] BatchImport Hash 冲突: '{path}' 与 '{_def.Paths[colIdx].Path}' 产生相同 hash (0x{hash:X16})，已跳过");
                    skipped++;
                    continue;
                }

                _def.Paths.Add(new RedDotPathEntry(path, hash, "")); added++;
            }
            MarkDirty(); _root = null;
            Toast($"导入 {added} 条新路径，跳过 {skipped} 条已存在");
        }

        private void DeleteNode(TreeNode node)
        {
            if (node == null || node.Index < 0) return;
            var toDelete = new HashSet<string>(); CollectSubtree(node, toDelete);
            int n = toDelete.Count;
            string msg = $"确认删除 \"{node.FullPath}\"？\n\n将永久删除 {n} 条路径：\n" + string.Join("\n", toDelete.OrderBy(x => x).Take(8));
            if (n > 8) msg += $"\n…还有 {n - 8} 条";
            if (!EditorUtility.DisplayDialog("确认删除", msg, "删除", "取消")) return;
            Undo.RecordObject(_def, $"删除 {n} 条路径");
            for (int i = _def.Paths.Count - 1; i >= 0; i--) if (toDelete.Contains(_def.Paths[i].Path)) _def.Paths.RemoveAt(i);
            MarkDirty(); _selectedPath = null; _addParent = ""; _root = null; Toast($"已删除 {n} 条路径");
        }

        private bool RenameNode(TreeNode node, string newName)
        {
            var old = _def.Paths[node.Index];
            int sep = old.Path.LastIndexOf('_');
            string pp = sep >= 0 ? old.Path.Substring(0, sep) : "";
            string nf = string.IsNullOrEmpty(pp) ? newName : $"{pp}_{newName}";
            long nh = RedDotHash.Compute(nf);
            if (_def.Paths.Any(x => x.Hash == nh)) { EditorUtility.DisplayDialog("错误", $"路径已存在：{nf}", "确定"); return false; }
            _def.Paths[node.Index] = new RedDotPathEntry(nf, nh, old.Comment);
            string op = old.Path + "_", np = nf + "_";
            for (int i = 0; i < _def.Paths.Count; i++)
            { if (i == node.Index) continue; var p = _def.Paths[i]; if (p.Path.StartsWith(op)) _def.Paths[i] = new RedDotPathEntry(np + p.Path.Substring(op.Length), RedDotHash.Compute(np + p.Path.Substring(op.Length)), p.Comment); }
            return true;
        }

        private void ShowNodeMenu(TreeNode node)
        {
            var m = new GenericMenu();
            m.AddItem(new GUIContent("在此节点下添加子路径"), false, () => { _addParent = node.FullPath; _addName = ""; GUI.FocusControl("add_name"); });
            m.AddSeparator("");
            m.AddItem(new GUIContent("复制路径"), false, () => { EditorGUIUtility.systemCopyBuffer = node.FullPath; Toast("路径已复制"); });
            if (node.Index >= 0)
            {
                m.AddItem(new GUIContent("复制 Hash"), false, () => { EditorGUIUtility.systemCopyBuffer = _def.Paths[node.Index].Hash.ToString(); Toast("Hash 已复制"); });
                m.AddItem(new GUIContent("复制 C# 常量名"), false, () => { EditorGUIUtility.systemCopyBuffer = $"{_def.ClassName}.{node.FullPath}"; Toast("已复制：" + _def.ClassName + "." + node.FullPath); });
            }
            m.AddSeparator("");
            if (node.Index >= 0) m.AddItem(new GUIContent("删除此节点（含子节点）"), false, () => DeleteNode(node));
            else m.AddDisabledItem(new GUIContent("删除"));
            m.ShowAsContext();
        }

        // ═══ 树 ═══

        private void RebuildTree()
        {
            if (_root != null) return;
            _root = new TreeNode { Name = "Root", FullPath = "", Index = -1, Depth = -1, Expanded = true };
            _nodeMap.Clear(); _nodeMap[""] = _root;
            foreach (var e in _def.Paths.OrderBy(p => DepthOf(p.Path)).ThenBy(p => p.Path))
                InsertNode(e.Path, _def.Paths.IndexOf(e));
            MarkLastChildren(_root);
        }

        private void InsertNode(string path, int idx)
        {
            if (_nodeMap.ContainsKey(path)) return;
            int sep = path.LastIndexOf('_');
            string pp = sep >= 0 ? path.Substring(0, sep) : "";
            if (!_nodeMap.ContainsKey(pp)) InsertNode(pp, -1);
            var p = _nodeMap[pp];
            var n = new TreeNode { Name = sep >= 0 ? path.Substring(sep + 1) : path, FullPath = path, Index = idx, Depth = p.Depth + 1, Expanded = !_collapsed.Contains(path) };
            p.Children.Add(n); _nodeMap[path] = n;
        }

        private static void MarkLastChildren(TreeNode n) { for (int i = 0; i < n.Children.Count; i++) { n.Children[i].IsLastChild = i == n.Children.Count - 1; MarkLastChildren(n.Children[i]); } }
        private void CollectSubtree(TreeNode n, HashSet<string> r) { if (n.Index >= 0) r.Add(n.FullPath); foreach (var c in n.Children) CollectSubtree(c, r); }
        private bool HasMatchingChild(TreeNode n) { if (string.IsNullOrEmpty(_search)) return true; foreach (var c in n.Children) if (c.FullPath.IndexOf(_search, StringComparison.OrdinalIgnoreCase) >= 0 || HasMatchingChild(c)) return true; return false; }
        private void ExpandAll(TreeNode n) { if (n == null) return; n.Expanded = true; _collapsed.Remove(n.FullPath); foreach (var c in n.Children) ExpandAll(c); }
        private void CollapseAll(TreeNode n) { if (n == null) return; n.Expanded = false; _collapsed.Add(n.FullPath); foreach (var c in n.Children) CollapseAll(c); }
        private static int DepthOf(string p) => string.IsNullOrEmpty(p) ? 0 : p.Count(c => c == '_') + 1;
        private void Toast(string m) { _toast = m; _toastUntil = EditorApplication.timeSinceStartup + 2.5; Repaint(); }
        private void MarkDirty() { _isDirty = true; EditorUtility.SetDirty(_def); }

        private void EnsureStyles()
        {
            _sectionLabel ??= new GUIStyle(EditorStyles.boldLabel) { fontSize = 12, normal = { textColor = new Color(0.7f, 0.7f, 0.7f) } };
            _branchName ??= new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(0.88f, 0.78f, 0.25f) }, fontStyle = FontStyle.Bold, padding = new RectOffset(0, 0, 0, 0) };
            _leafName ??= new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(0.78f, 0.78f, 0.78f) }, padding = new RectOffset(0, 0, 0, 0) };
        }

        private void CreateDefinition()
        {
            string f = EditorUtility.SaveFolderPanel("保存 RedDotPathDefinition", "Assets/", "");
            if (string.IsNullOrEmpty(f)) return;
            string r = "Assets" + f.Replace(Application.dataPath, ""), ap = Path.Combine(r, "RedDotPathDefinition.asset");
            var a = CreateInstance<RedDotPathDefinition>(); AssetDatabase.CreateAsset(a, ap); AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            _def = a; EditorGUIUtility.PingObject(a); Toast("已创建");
        }

        // ═══ 代码生成 ═══

        private string GenerateConstantsCode()
        {
            var sb = new StringBuilder();
            sb.AppendLine("// AUTO-GENERATED"); sb.AppendLine($"// {DateTime.Now:yyyy-MM-dd HH:mm}"); sb.AppendLine();
            if (!string.IsNullOrEmpty(_def.Namespace)) { sb.AppendLine($"namespace {_def.Namespace}"); sb.AppendLine("{"); }
            sb.AppendLine($"    public static class {_def.ClassName}"); sb.AppendLine("    {");
            string lp = "";
            foreach (var e in _def.Paths.OrderBy(p => DepthOf(p.Path)).ThenBy(p => p.Path))
            {
                int sep = e.Path.LastIndexOf('_'); string pr = sep >= 0 ? e.Path.Substring(0, sep) : "";
                if (lp != pr && !string.IsNullOrEmpty(pr)) sb.AppendLine(); lp = pr;
                string cn = e.Path.Replace("-", "_").Replace(" ", "_");
                if (!string.IsNullOrEmpty(e.Comment)) sb.AppendLine($"        /// <summary>{e.Comment}</summary>");
                sb.AppendLine($"        /// <code>{e.Path}</code>");
                sb.AppendLine($"        public const long {cn} = unchecked((long)0x{e.Hash:X16}UL);");
            }
            sb.AppendLine("    }"); if (!string.IsNullOrEmpty(_def.Namespace)) sb.AppendLine("}");
            return sb.ToString();
        }

        private string GenerateRegistrationCode()
        {
            var sb = new StringBuilder();
            sb.AppendLine("// AUTO-GENERATED"); sb.AppendLine($"// {DateTime.Now:yyyy-MM-dd HH:mm}"); sb.AppendLine();
            string cn = _def.ClassName;
            if (!string.IsNullOrEmpty(_def.Namespace)) { sb.AppendLine($"namespace {_def.Namespace}"); sb.AppendLine("{"); }
            sb.AppendLine($"    public static class {cn}Registration"); sb.AppendLine("    {");
            sb.AppendLine("        public static void RegisterAll() { RegisterAll(RedDotManager.Instance); }");
            sb.AppendLine("        public static void RegisterAll(RedDotManager mgr) { if (mgr == null) return;");
            foreach (var e in _def.Paths.OrderBy(p => DepthOf(p.Path)).ThenBy(p => p.Path))
            {
                int sep = e.Path.LastIndexOf('_');
                string pc = sep >= 0 ? $"{cn}.{e.Path.Substring(0, sep)}" : "0L";
                sb.AppendLine($"            mgr.RegisterNode({cn}.{e.Path}, {pc}, true);");
            }
            sb.AppendLine("        }"); sb.AppendLine("    }");
            if (!string.IsNullOrEmpty(_def.Namespace)) sb.AppendLine("}");
            return sb.ToString();
        }
    }
}
