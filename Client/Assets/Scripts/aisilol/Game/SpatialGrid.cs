using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace aisilol.Game
{
    // XZ 평면을 같은 크기의 셀로 나누고, 셀 좌표 → 인덱스 목록으로 저장하는 균일 격자
    // 범위 검색은 범위에 걸친 셀만 확인하므로 전체 목록을 순회하지 않음. Burst Job 안에서 사용
    internal static class SpatialGrid
    {
        // 셀 안의 인덱스를 하나씩 전달받음. false를 반환하면 검색 중단
        public interface IVisitor
        {
            bool Visit(int index);
        }

        public static int2 CellOf(float2 position, float inverseCellSize)
        {
            return (int2)math.floor(position * inverseCellSize);
        }

        // min ~ max 영역에 걸친 셀의 인덱스를 모두 방문. 실제 거리 판정은 visitor가 수행
        public static void Query<T>(NativeParallelMultiHashMap<int2, int> grid, float inverseCellSize, float2 min, float2 max, ref T visitor)
            where T : struct, IVisitor
        {
            var minCell = CellOf(min, inverseCellSize);
            var maxCell = CellOf(max, inverseCellSize);

            for (var x = minCell.x; x <= maxCell.x; ++x)
            {
                for (var y = minCell.y; y <= maxCell.y; ++y)
                {
                    if (!grid.TryGetFirstValue(new int2(x, y), out var index, out var iterator))
                        continue;

                    do
                    {
                        if (!visitor.Visit(index))
                            return;
                    }
                    while (grid.TryGetNextValue(out index, ref iterator));
                }
            }
        }

        [BurstCompile]
        public struct BuildJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> positions;
            public NativeParallelMultiHashMap<int2, int>.ParallelWriter grid;
            public float inverseCellSize;

            public void Execute(int index)
            {
                grid.Add(CellOf(positions[index].xz, inverseCellSize), index);
            }
        }

        // center에서 radius 이내인 인덱스 개수
        public struct CountVisitor : IVisitor
        {
            public NativeArray<float3> positions;
            public float2 center;
            public float radius;
            public int count;

            public bool Visit(int index)
            {
                if (math.distancesq(positions[index].xz, center) <= radius * radius)
                    ++count;

                return true;
            }
        }
    }
}
