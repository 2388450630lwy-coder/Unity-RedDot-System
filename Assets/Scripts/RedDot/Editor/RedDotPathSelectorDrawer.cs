using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;

namespace RedDot.Editor
{
    /// <summary>
    /// PropertyDrawer for [RedDotPathSelector] — 可搜索的路径下拉菜单
    /// </summary>
    [CustomPropertyDrawer(typeof(RedDotPathSelectorAttribute))]
    public class RedDotPathSelectorDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            if (property.propertyType != SerializedPropertyType.Integer)
            {
                EditorGUI.PropertyField(position, property, label);
                return;
            }

            int currentHash = property.intValue;
            string currentLabel = GetLabel(currentHash);

            // 标准 label + field 布局
            var labelRect = new Rect(position.x, position.y, EditorGUIUtility.labelWidth, position.height);
            var fieldRect = new Rect(position.x + EditorGUIUtility.labelWidth, position.y,
                position.width - EditorGUIUtility.labelWidth, position.height);

            EditorGUI.PrefixLabel(labelRect, label);

            if (EditorGUI.DropdownButton(fieldRect, new GUIContent(currentLabel), FocusType.Keyboard))
            {
                var dropdown = new RedDotPathDropdown(new AdvancedDropdownState(), currentHash,
                    selectedHash =>
                    {
                        property.intValue = selectedHash;
                        property.serializedObject.ApplyModifiedProperties();
                    });
                dropdown.Show(fieldRect);
            }
        }

        private static string GetLabel(int hash)
        {
            if (hash == 0) return "— None —";

            // Search for RedDotPaths type across all assemblies
            Type type = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetType("RedDot.RedDotPaths");
                if (type != null) break;
            }

            if (type != null)
            {
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (field.FieldType == typeof(int) && field.IsLiteral)
                    {
                        int h = (int)field.GetValue(null);
                        if (h == hash) return field.Name;
                    }
                }
            }

            return $"Unknown (0x{hash:X8})";
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUIUtility.singleLineHeight;
        }
    }

    /// <summary>
    /// 可搜索的红点路径下拉菜单（基于 AdvancedDropdown）
    /// </summary>
    internal class RedDotPathDropdown : AdvancedDropdown
    {
        private readonly int _currentHash;
        private readonly Action<int> _onSelected;
        private List<PathItem> _items;

        public RedDotPathDropdown(AdvancedDropdownState state, int currentHash, Action<int> onSelected)
            : base(state)
        {
            _currentHash = currentHash;
            _onSelected = onSelected;
            minimumSize = new Vector2(300, 400);
        }

        protected override AdvancedDropdownItem BuildRoot()
        {
            var root = new AdvancedDropdownItem("RedDot Paths");
            _items = new List<PathItem>();

            // 查找 RedDotPaths 类型
            Type type = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetType("RedDot.RedDotPaths");
                if (type != null) break;
            }

            if (type == null) return root;

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(int) && f.IsLiteral)
                .Select(f => new PathItem { Name = f.Name, Hash = (int)f.GetValue(null) })
                .OrderBy(p => p.Name.Split('_').Length)
                .ThenBy(p => p.Name)
                .ToList();

            // 构建树形结构
            var nodeMap = new Dictionary<string, AdvancedDropdownItem>();
            foreach (var item in fields)
            {
                var parts = item.Name.Split('_');
                AdvancedDropdownItem parent = root;
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    string key = string.Join("_", parts, 0, i + 1);
                    if (!nodeMap.TryGetValue(key, out var mid))
                    {
                        mid = new AdvancedDropdownItem(parts[i]);
                        parent.AddChild(mid);
                        nodeMap[key] = mid;
                    }
                    parent = mid;
                }

                // 末段名 + 完整路径，搜索时能匹配任意层级
                string label = $"{parts[parts.Length - 1]}   ({item.Name})";
                var leaf = new AdvancedDropdownItem(label)
                {
                    id = item.Hash
                };
                parent.AddChild(leaf);
                _items.Add(item);

                // 高亮当前选中
                if (item.Hash == _currentHash)
                {
                    leaf.enabled = false;
                }
            }

            // 添加 None 选项
            var none = new AdvancedDropdownItem("— None —") { id = 0 };
            root.AddChild(none);
            _items.Insert(0, new PathItem { Name = "— None —", Hash = 0 });

            return root;
        }

        protected override void ItemSelected(AdvancedDropdownItem item)
        {
            _onSelected?.Invoke(item.id);
        }

        private class PathItem
        {
            public string Name;
            public int Hash;
        }
    }
}
