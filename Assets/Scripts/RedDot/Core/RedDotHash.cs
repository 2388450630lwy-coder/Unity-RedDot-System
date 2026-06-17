namespace RedDot
{
    /// <summary>
    /// FNV-1a 64-bit 哈希。
    /// 64-bit 空间下，10 亿条路径的碰撞概率约 0.001%，实际使用中可视为无碰撞。
    /// 用于静态路径和动态路径的哈希计算。
    /// 静态路径可以在editor人为规避，动态用pathhash和childid -> nodeindex，可以规避hash碰撞。
    /// </summary>
    public static class RedDotHash
    {
        private const ulong FNV64_OFFSET_BASIS = 14695981039346656037UL;
        private const ulong FNV64_PRIME        = 1099511628211UL;

        public static long Compute(string value)
        {
            if (string.IsNullOrEmpty(value))
                return 0L;

            ulong hash   = FNV64_OFFSET_BASIS;
            int   length = value.Length;

            for (int i = 0; i < length; i++)
            {
                char c = value[i];
                hash ^= (byte)(c & 0xFF);
                hash *= FNV64_PRIME;
                hash ^= (byte)((c >> 8) & 0xFF);
                hash *= FNV64_PRIME;
            }

            return unchecked((long)hash);
        }

        public static long Compute(byte[] data)
        {
            if (data == null || data.Length == 0)
                return 0L;

            ulong hash = FNV64_OFFSET_BASIS;
            for (int i = 0; i < data.Length; i++)
            {
                hash ^= data[i];
                hash *= FNV64_PRIME;
            }
            return unchecked((long)hash);
        }

        /// <summary>
        /// 动态节点：用父路径 hash + 业务 ID 计算子节点 hash，零字符串分配。
        /// 如 ComputeDynamic(RedDotPaths.Root_Mail, 1001)。
        /// </summary>
        public static long ComputeDynamic(long parentHash, int childId)
        {
            ulong hash = FNV64_OFFSET_BASIS;
            ulong ph   = unchecked((ulong)parentHash);

            hash ^= (byte)(ph & 0xFF);          hash *= FNV64_PRIME;
            hash ^= (byte)((ph >> 8)  & 0xFF);  hash *= FNV64_PRIME;
            hash ^= (byte)((ph >> 16) & 0xFF);  hash *= FNV64_PRIME;
            hash ^= (byte)((ph >> 24) & 0xFF);  hash *= FNV64_PRIME;
            hash ^= (byte)((ph >> 32) & 0xFF);  hash *= FNV64_PRIME;
            hash ^= (byte)((ph >> 40) & 0xFF);  hash *= FNV64_PRIME;
            hash ^= (byte)((ph >> 48) & 0xFF);  hash *= FNV64_PRIME;
            hash ^= (byte)((ph >> 56) & 0xFF);  hash *= FNV64_PRIME;

            hash ^= (byte)(childId & 0xFF);          hash *= FNV64_PRIME;
            hash ^= (byte)((childId >> 8)  & 0xFF);  hash *= FNV64_PRIME;
            hash ^= (byte)((childId >> 16) & 0xFF);  hash *= FNV64_PRIME;
            hash ^= (byte)((childId >> 24) & 0xFF);  hash *= FNV64_PRIME;

            return unchecked((long)hash);
        }
    }
}
