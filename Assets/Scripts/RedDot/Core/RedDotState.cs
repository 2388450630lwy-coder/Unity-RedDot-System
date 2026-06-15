namespace RedDot
{
    /// <summary>
    /// 红点节点状态快照（readonly struct，栈上分配）。监听器统一接收此类型，避免 UI 二次查询。
    /// </summary>
    public readonly struct RedDotState
    {
        public readonly int PathHash;
        public readonly int SelfCount;
        public readonly int TotalCount;
        public readonly RedDotType EffectiveType;

        public bool Visible => TotalCount > 0;

        public RedDotState(int pathHash, int selfCount, int totalCount, RedDotType effectiveType)
        {
            PathHash = pathHash;
            SelfCount = selfCount;
            TotalCount = totalCount;
            EffectiveType = effectiveType;
        }
    }
}
