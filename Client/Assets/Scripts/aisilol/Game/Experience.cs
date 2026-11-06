using UnityEngine;

namespace aisilol.Game
{
    public sealed class Experience
    {
        private const float BaseRequired = 10;
        private const float Exponent = 1.5f;

        public int Level { get; private set; } = 1;
        public int Current { get; private set; }
        public float Progress => (float)Current / Required;

        private int Required => RequiredFor(Level);

        // 레벨 n에서 다음 레벨까지 필요한 경험치. 10, 28, 52, 80, 112 ...
        public static int RequiredFor(int level)
        {
            return Mathf.Max(1, Mathf.RoundToInt(BaseRequired * Mathf.Pow(level, Exponent)));
        }

        // 오른 레벨 수를 반환. 한 번에 여러 레벨이 오를 수 있음
        public int Add(int amount)
        {
            if (amount <= 0)
                return 0;

            Current += amount;

            var levelUps = 0;
            while (Current >= Required)
            {
                Current -= Required;
                ++Level;
                ++levelUps;
            }

            return levelUps;
        }
    }
}
