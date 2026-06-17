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
    /// PropertyDrawer for [RedDotPathSelector] — 可搜索的路径下拉菜单。
    /// 支持 long 序列化字段（FNV-1a 64-bit hash）。
    /// </summary>
    [CustomPropertyDrawer(typeof(RedDotPathSelectorAttribute))]
    public class RedDotPathSelectorDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            // 支持 int（向后兼容）和 long 字段
            if (property.propertyType != SerializedPropertyType.Integer)
            {
                EditorGUI.PropertyField(position, property, label);
                return;
            }

            long currentHash = property.longValue;
            string currentLabel = GetLabel(currentHash);

            var labelRect = new Rect(position.x, position.y, EditorGUIUtility.labelWidth, position.height);
            var fieldRect = new Rect(position.x + EditorGUIUtility.labelWidth, position.y,
                position.width - EditorGUIUtility.labelWidth, position.height);

            EditorGUI.PrefixLabel(labelRect, label);

            if (EditorGUI.DropdownButton(fieldRect, new GUIContent(currentLabel), FocusType.Keyboard))
            {
                var dropdown = new RedDotPathDropdown(new AdvancedDropdownState(), currentHash,
                    selectedHash =>
                    {
                        property.longValue = selectedHash;
                        property.serializedObject.ApplyModifiedProperties();
                    });
                dropdown.Show(fieldRect);
            }
        }

        private static string GetLabel(long hash)
        {
            if (hash == 0L) return "— None —";

            Type type = FindRedDotPathsType();
            if (type != null)
            {
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    if (field.FieldType == typeof(long) && field.IsLiteral)
                    {
                        long h = (long)field.GetValue(null);
                        if (h == hash) return field.Name;
                    }
                }
            }

            return $"Unknown (0x{hash:X16})";
        }

        private static Type FindRedDotPathsType()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("RedDot.RedDotPaths");
                if (t != null) return t;
            }
            return null;
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            return EditorGUIUtility.singleLineHeight;
        }
    }

    /// <summary>
    /// 可搜索的红点路径下拉菜单（基于 AdvancedDropdown）。
    /// AdvancedDropdownItem.id 只有 int 宽度，因此用列表索引作为 id，选中后再查 hash。
    /// </summary>
    internal class RedDotPathDropdown : AdvancedDropdown
    {
        private readonly long           _currentHash;
        private readonly Action<long>   _onSelected;
        private          List<PathItem> _items;

        public RedDotPathDropdown(AdvancedDropdownState state, long currentHash, Action<long> onSelected)
            : base(state)
        {
            _currentHash = currentHash;
            _onSelected  = onSelected;
            minimumSize  = new Vector2(300, 400);
        }

        protected override AdvancedDropdownItem BuildRoot()
        {
            var root = new AdvancedDropdownItem("RedDot Paths");
            _items = new List<PathItem>();

            // 索引 0 保留给 None
            _items.Add(new PathItem { Name = "— None —", Hash = 0L });
            var none = new AdvancedDropdownItem("— None —") { id = 0 };
            root.AddChild(none);

            Type type = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = asm.GetType("RedDot.RedDotPaths");
                if (type != null) break;
            }

            if (type == null) return root;

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(long) && f.IsLiteral)
                .Select(f => new PathItem { Name = f.Name, Hash = (long)f.GetValue(null) })
                .OrderBy(p => p.Name.Split('_').Length)
                .ThenBy(p => p.Name)
                .ToList();

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

                // 用列表索引作为 id，避免 long hash 超出 int 范围
                int  itemIndex = _items.Count;
                _items.Add(item);

                string leafLabel = $"{parts[parts.Length - 1]}   ({item.Name})";
                var leaf = new AdvancedDropdownItem(leafLabel) { id = itemIndex };

                if (item.Hash == _currentHash)
                    leaf.enabled = false;

                parent.AddChild(leaf);
            }

            return root;
        }

        protected override void ItemSelected(AdvancedDropdownItem item)
        {
            if (item.id >= 0 && item.id < _items.Count)
                _onSelected?.Invoke(_items[item.id].Hash);
        }

        private class PathItem
        {
            public string Name;
            public long   Hash;
        }
    }
}
