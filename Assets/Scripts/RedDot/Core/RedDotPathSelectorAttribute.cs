using System;
using UnityEngine;

namespace RedDot
{
    /// <summary>
    /// 标记 int 字段为红点路径选择器。挂载后 Inspector 中自动显示
    /// RedDotPaths 下拉菜单，无需手动填写 hash 值。
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class RedDotPathSelectorAttribute : PropertyAttribute { }
}
