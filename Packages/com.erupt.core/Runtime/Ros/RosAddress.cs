namespace Erupt.Ros
{
    /// <summary>The form of a ROS address as the user enters it.</summary>
    public static class RosAddress
    {
        /// <summary>The longest text that can be an IPv4 address (<c>255.255.255.255</c>).</summary>
        public const int MaxLength = 15;

        /// <summary>
        /// Whether <paramref name="text"/> is an IPv4 address in dotted form: four decimal
        /// numbers from 0 to 255. <paramref name="canonical"/> is the address without leading
        /// zeros (<c>010.0.0.5</c> is <c>10.0.0.5</c>: the user means decimal, and a parser
        /// that reads a leading zero as octal would connect somewhere else).
        /// </summary>
        public static bool TryParseIPv4(string text, out string canonical)
        {
            canonical = null;
            if (string.IsNullOrEmpty(text) || text.Length > MaxLength) return false;

            string[] parts = text.Split('.');
            if (parts.Length != 4) return false;

            var numbers = new int[4];
            for (int i = 0; i < 4; i++)
            {
                string part = parts[i];
                if (part.Length == 0 || part.Length > 3) return false;
                int value = 0;
                foreach (char c in part)
                {
                    if (c < '0' || c > '9') return false;
                    value = value * 10 + (c - '0');
                }
                if (value > 255) return false;
                numbers[i] = value;
            }

            canonical = $"{numbers[0]}.{numbers[1]}.{numbers[2]}.{numbers[3]}";
            return true;
        }
    }
}
