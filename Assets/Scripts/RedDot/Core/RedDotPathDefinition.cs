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
        /// <summary>路径字符串，如 "MainUI/Bag/Item"</summary>
        [Tooltip("路径字符串，用 / 分隔层级")]
        public string Path;

        /// <summary>路径的 int hash 值（由编辑器工具自动生成）</summary>
        [Tooltip("自动生成的 hash 值")]
        public int Hash;

        /// <summary>注释说明</summary>
        [Tooltip("路径说明")]
        public string Comment;

        public RedDotPathEntry(string path, int hash, string comment = "")
        {
            Path = path;
            Hash = hash;
            Comment = comment;
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
        /// <summary>路径条目列表</summary>
        [Tooltip("所有红点路径定义")]
        public List<RedDotPathEntry> Paths = new List<RedDotPathEntry>();

        /// <summary>生成的常量类名</summary>
        [Tooltip("生成的常量类名")]
        public string ClassName = "RedDotPaths";

        /// <summary>生成的常量的命名空间</summary>
        [Tooltip("生成的常量命名空间")]
        public string Namespace = "RedDot";

        /// <summary>生成的脚本输出路径（相对于 Assets/）</summary>
        [Tooltip("输出路径（相对于 Assets/）")]
        public string OutputPath = "Scripts/RedDot/Generated/RedDotPaths.cs";

        /// <summary>注册代码输出路径</summary>
        [Tooltip("注册代码输出路径（相对于 Assets/）")]
        public string RegistrationOutputPath = "Scripts/RedDot/Generated/RedDotPathRegistration.cs";

        // ==================== 工具 ====================

        /// <summary>
        /// 规范化路径分隔符：运行时和生成代码统一使用 _ 作为层级分隔符。
        /// </summary>
        public static string NormalizePath(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? string.Empty
                : path.Trim().Replace('/', '_');
        }

        // ==================== 编辑时计算 ====================

        /// <summary>
        /// 为所有路径重新计算 hash 值（编辑器工具调用）
        /// </summary>
        public void RecalculateHashes()
        {
            // 使用 for 而非 foreach：Mono 的 List<T> setter 会递增内部版本号，
            // 导致 foreach 枚举器抛出 InvalidOperationException
            for (int i = 0; i < Paths.Count; i++)
            {
                var entry = Paths[i];
                // 规范化 / → _ 保证一致性
                string normalizedPath = NormalizePath(entry.Path);
                int hash = RedDotHash.Compute(normalizedPath);
                Paths[i] = new RedDotPathEntry(normalizedPath, hash, entry.Comment);
            }
        }

        /// <summary>
        /// 校验路径合法性
        /// </summary>
        public bool Validate(out List<string> errors)
        {
            errors = new List<string>();
            var hashSet = new HashSet<int>();
            var pathSet = new HashSet<string>();

            for (int i = 0; i < Paths.Count; i++)
            {
                var entry = Paths[i];

                // 规范化路径
                string normalizedPath = NormalizePath(entry.Path);

                // 空路径检查
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    errors.Add($"Entry {i}: path is empty");
                    continue;
                }

                // 重复路径检查（用规范化路径）
                if (!pathSet.Add(normalizedPath))
                {
                    errors.Add($"Entry {i}: duplicate path '{normalizedPath}'");
                }

                // hash 冲突检查（概率极低但值得报告）
                int expectedHash = RedDotHash.Compute(normalizedPath);
                if (entry.Hash != expectedHash)
                {
                    errors.Add($"Entry {i}: stale hash for path '{normalizedPath}' (stored=0x{entry.Hash:X8}, expected=0x{expectedHash:X8})");
                }

                if (expectedHash == 0)
                {
                    errors.Add($"Entry {i}: hash for path '{normalizedPath}' is reserved zero");
                }

                if (!hashSet.Add(expectedHash))
                {
                    errors.Add($"Entry {i}: hash collision for path '{normalizedPath}' (hash=0x{expectedHash:X8})");
                }

                // 空段检查
                var segments = normalizedPath.Split('_');
                for (int j = 0; j < segments.Length; j++)
                {
                    if (string.IsNullOrEmpty(segments[j]))
                    {
                        errors.Add($"Entry {i}: empty segment at position {j} in path '{normalizedPath}'");
                    }
                }
            }

            for (int i = 0; i < Paths.Count; i++)
            {
                string normalizedPath = NormalizePath(Paths[i].Path);
                int lastSep = normalizedPath.LastIndexOf('_');
                if (lastSep <= 0) continue;

                string parentPath = normalizedPath.Substring(0, lastSep);
                if (!pathSet.Contains(parentPath))
                {
                    errors.Add($"Entry {i}: missing parent path '{parentPath}' for '{normalizedPath}'");
                }
            }

            return errors.Count == 0;
        }
    }
}
