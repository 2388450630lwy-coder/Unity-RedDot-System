using System;
using System.Collections.Generic;
using UnityEngine;

namespace RedDot
{
    /// <summary>
    /// 红点路径条目 —— 定义一条红点路径及其元数据
    /// </summary>
    [Serializable]
    public struct RedDotPathEntry
    {
        /// <summary>路径字符串，如 "Root_Bag_Item"</summary>
        [Tooltip("路径字符串，用 _ 分隔层级")]
        public string Path;

        /// <summary>
        /// 稳定递增 ID，由编辑器分配一次后永不变更（即使路径重命名也保持不变）。
        /// 运行时以此值作为节点唯一键。
        /// </summary>
        [Tooltip("编辑器分配的稳定递增 ID，运行时唯一键")]
        public int StableId;

        [Tooltip("路径说明")]
        public string Comment;

        public RedDotPathEntry(string path, int stableId, string comment = "")
        {
            Path     = path;
            StableId = stableId;
            Comment  = comment;
        }
    }

    /// <summary>
    /// 红点路径定义 —— ScriptableObject，在编辑器中可视化配置所有红点路径。
    ///
    /// 使用方式：
    /// 1. 在 Project 窗口右键 → Create → RedDot → Path Definition 创建配置
    /// 2. 在 Inspecotr 中添加/编辑路径条目
    /// 3. 点击 "Generate Constants" 生成 RedDotPaths.cs 和注册代码
    /// 4. 拖入 RedDotManager 的 PathDefinitions 列表
    /// </summary>
    [CreateAssetMenu(
        fileName = "RedDotPathDefinition",
        menuName = "RedDot/Path Definition")]
    public class RedDotPathDefinition : ScriptableObject
    {
        [Tooltip("所有红点路径定义")]
        public List<RedDotPathEntry> Paths = new List<RedDotPathEntry>();

        /// <summary>
        /// 下一个可用的 StableId，序列化到 SO，只增不减，确保已删除路径的 ID 不被复用。
        /// </summary>
        [Tooltip("下一个可分配的 StableId（自动维护，请勿手动修改）")]
        public int NextStableId = 1;

        [Tooltip("生成的常量类名")]
        public string ClassName = "RedDotPaths";

        [Tooltip("生成的常量命名空间")]
        public string Namespace = "RedDot";

        [Tooltip("输出路径（相对于 Assets/）")]
        public string OutputPath = "Scripts/RedDot/Generated/RedDotPaths.cs";

        [Tooltip("注册代码输出路径（相对于 Assets/）")]
        public string RegistrationOutputPath = "Scripts/RedDot/Generated/RedDotPathRegistration.cs";

        /// <summary>
        /// 规范化路径分隔符：运行时和生成代码统一使用 _ 作为层级分隔符。
        /// </summary>
        public static string NormalizePath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Trim().Replace('/', '_');
        }

        /// <summary>
        /// 强制从 1 开始对所有路径重新分配 StableId（按当前列表顺序）。
        /// 此操作会使所有旧 ID 失效，需重新生成代码并更新场景/Prefab 中的序列化引用。
        /// </summary>
        public void RegenerateAllIds()
        {
            NextStableId = 1;
            for (int i = 0; i < Paths.Count; i++)
            {
                var entry = Paths[i];
                Paths[i] = new RedDotPathEntry(NormalizePath(entry.Path), NextStableId++, entry.Comment);
            }
        }

        /// <summary>
        /// 为尚未分配 StableId（值为 0）的路径按序分配 ID。
        /// 已有 ID 的路径保持不变，保证 ID 稳定性。
        /// </summary>
        public void AssignMissingIds()
        {
            for (int i = 0; i < Paths.Count; i++)
            {
                var entry = Paths[i];
                string normalizedPath = NormalizePath(entry.Path);
                if (entry.StableId != 0)
                {
                    if (normalizedPath != entry.Path)
                        Paths[i] = new RedDotPathEntry(normalizedPath, entry.StableId, entry.Comment);
                    continue;
                }
                Paths[i] = new RedDotPathEntry(normalizedPath, NextStableId++, entry.Comment);
            }
        }

        /// <summary>
        /// 校验路径合法性：检查路径唯一性、ID 分配情况、ID 唯一性及父路径完整性。
        /// </summary>
        public bool Validate(out List<string> errors)
        {
            errors = new List<string>();
            var idSet   = new HashSet<int>();
            var pathSet = new HashSet<string>();

            for (int i = 0; i < Paths.Count; i++)
            {
                var entry = Paths[i];
                string normalizedPath = NormalizePath(entry.Path);

                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    errors.Add($"Entry {i}: path is empty");
                    continue;
                }

                if (!pathSet.Add(normalizedPath))
                    errors.Add($"Entry {i}: duplicate path '{normalizedPath}'");

                if (entry.StableId <= 0)
                    errors.Add($"Entry {i}: path '{normalizedPath}' 未分配 StableId（请点击「分配 ID」）");
                else if (!idSet.Add(entry.StableId))
                    errors.Add($"Entry {i}: duplicate StableId {entry.StableId} on path '{normalizedPath}'");

                var segments = normalizedPath.Split('_');
                for (int j = 0; j < segments.Length; j++)
                {
                    if (string.IsNullOrEmpty(segments[j]))
                        errors.Add($"Entry {i}: empty segment at position {j} in path '{normalizedPath}'");
                }
            }

            for (int i = 0; i < Paths.Count; i++)
            {
                string normalizedPath = NormalizePath(Paths[i].Path);
                int lastSep = normalizedPath.LastIndexOf('_');
                if (lastSep <= 0) continue;

                string parentPath = normalizedPath.Substring(0, lastSep);
                if (!pathSet.Contains(parentPath))
                    errors.Add($"Entry {i}: missing parent path '{parentPath}' for '{normalizedPath}'");
            }

            return errors.Count == 0;
        }
    }
}
