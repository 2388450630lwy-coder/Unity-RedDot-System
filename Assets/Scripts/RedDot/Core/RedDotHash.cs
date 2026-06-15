namespace RedDot
{
    /// <summary>
    /// FNV-1a 32-bit 哈希。32-bit 适合当前规模；路径量很大时应升级为 64-bit。
    /// </summary>
    public static class RedDotHash
    {
        // FNV-1a 32-bit constants
        private const uint FNV_OFFSET_BASIS = 2166136261;
        private const uint FNV_PRIME = 16777619;

        public static int Compute(string value)
        {
            if (string.IsNullOrEmpty(value))
                return 0;

            uint hash = FNV_OFFSET_BASIS;
            int length = value.Length;

            for (int i = 0; i < length; i++)
            {
                char c = value[i];
                hash ^= (byte)(c & 0xFF);
                hash *= FNV_PRIME;
                hash ^= (byte)((c >> 8) & 0xFF);
                hash *= FNV_PRIME;
            }

            return unchecked((int)hash);
        }

        public static int Compute(byte[] data)
        {
            if (data == null || data.Length == 0)
                return 0;

            uint hash = FNV_OFFSET_BASIS;
            for (int i = 0; i < data.Length; i++)
            {
                hash ^= data[i];
                hash *= FNV_PRIME;
            }
            return unchecked((int)hash);
        }

        /// <summary>
        /// 动态节点：用父路径 hash + 业务 ID 计算 hash，零字符串分配。
        /// 如 ComputeDynamic(RedDotPaths.Root_Mail, 1001)。
        /// </summary>
        public static int ComputeDynamic(int parentHash, int childId)
        {
            uint hash = FNV_OFFSET_BASIS;
            hash ^= (byte)(parentHash & 0xFF); hash *= FNV_PRIME;
            hash ^= (byte)((parentHash >> 8) & 0xFF); hash *= FNV_PRIME;
            hash ^= (byte)((parentHash >> 16) & 0xFF); hash *= FNV_PRIME;
            hash ^= (byte)((parentHash >> 24) & 0xFF); hash *= FNV_PRIME;
            hash ^= (byte)(childId & 0xFF); hash *= FNV_PRIME;
            hash ^= (byte)((childId >> 8) & 0xFF); hash *= FNV_PRIME;
            hash ^= (byte)((childId >> 16) & 0xFF); hash *= FNV_PRIME;
            hash ^= (byte)((childId >> 24) & 0xFF); hash *= FNV_PRIME;
            return unchecked((int)hash);
        }
    }
}
