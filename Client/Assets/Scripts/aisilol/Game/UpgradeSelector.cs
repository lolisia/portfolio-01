using System.Collections.Generic;

namespace aisilol.Game
{
    public static class UpgradeSelector
    {
        // 최대 레벨에 도달하지 않은 강화 중 서로 다른 것을 최대 count개 무작위로 선택
        public static void Pick(IReadOnlyList<UpgradeDefinition> pool, PlayerStats stats, int count, ref Unity.Mathematics.Random random, List<UpgradeDefinition> result)
        {
            result.Clear();

            if (pool == null)
                return;

            foreach (var upgrade in pool)
            {
                if (stats.CanApply(upgrade) && !result.Contains(upgrade))
                    result.Add(upgrade);
            }

            // 앞에서부터 count개만 섞는 부분 Fisher-Yates
            var pickCount = System.Math.Min(count, result.Count);
            for (var index = 0; index < pickCount; index++)
            {
                var swap = random.NextInt(index, result.Count);
                (result[index], result[swap]) = (result[swap], result[index]);
            }

            result.RemoveRange(pickCount, result.Count - pickCount);
        }
    }
}
