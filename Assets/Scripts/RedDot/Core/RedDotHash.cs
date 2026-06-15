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
    }
}
