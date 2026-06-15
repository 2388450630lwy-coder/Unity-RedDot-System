namespace RedDot
{
    /// <summary>
    /// 红点类型。父节点聚合所有子节点类型位，优先级 IsNew > CanUpdate > Tips > Normal > Number。
    /// </summary>
    public enum RedDotType
    {
        Normal    = 1 << 0,
        Tips      = 1 << 1,
        CanUpdate = 1 << 2,
        IsNew     = 1 << 3,
        Number    = 1 << 4,
    }

    public static class RedDotTypeHelper
    {
        /// <summary>按优先级降序排列</summary>
        public static readonly RedDotType[] PriorityOrder =
        {
            RedDotType.IsNew,
            RedDotType.CanUpdate,
            RedDotType.Tips,
            RedDotType.Normal,
            RedDotType.Number,
        };
    }
}
