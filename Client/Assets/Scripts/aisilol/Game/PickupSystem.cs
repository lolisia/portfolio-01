using System;
using aisilol.Common;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace aisilol.Game
{
    // Enemy가 사망한 자리에 경험치 Object와 HP 포션을 떨어뜨리고, 자석 범위에 들어오면 플레이어에게 끌어와 획득
    // 가까이 모인 같은 단계의 경험치 Object 10개는 다음 단계 1개로 병합. 가치는 1 → 10 → 100 ... 으로 10배씩 증가
    //
    // Update     : 이동 / 획득 판정 Job 예약
    // LateUpdate : EnemyManager가 사망을 처리한 뒤 실행. 획득 제거 → 드롭 생성 → 병합 → 렌더링. 목록 변경은 모두 Burst Job
    [DefaultExecutionOrder(20)]
    public class PickupSystem : MonoBehaviour
    {
        private const int InitialCapacity = 256;
        private const int JobBatchCount = 64;
        private const int MergeCount = 10;              // 이 개수가 모이면 병합
        private const float MergeCellSize = 0.5f;       // 0단계 병합 범위. 같은 셀 안에 있으면 "가까운" 것으로 판정
        private const float MergeCellGrowth = 3;        // 단계가 오를수록 수가 적어지므로 병합 범위를 단계마다 3배씩 넓힘
        private const float MergeInterval = 0.2f;       // 병합 검사 주기(초)
        private const float CollectRadius = 0.1f;       // 플레이어 중심에서 이 거리 안이면 획득
        private const float PullSpeed = 3;              // 자석으로 끌려올 때 기본 속도(m/s)
        private const float PullAcceleration = 4;       // 멀수록 빠르게. 거리(m)당 추가 속도
        private const float TierScaleStep = 0.6f;       // 병합 단계마다 크기 증가 비율

        // 병합 단계별 색상. 마지막 색 이후 단계는 마지막 색과 크기를 사용
        private static readonly Color[] TierColors =
        {
            new(0.2f, 0.9f, 1f),    // 1
            new(0.3f, 1f, 0.3f),    // 10
            new(1f, 0.9f, 0.2f),    // 100
            new(1f, 0.5f, 0.1f),    // 1,000
            new(1f, 0.2f, 0.9f),    // 10,000
            new(1f, 1f, 1f),        // 100,000 이상
        };

        // Burst Job에서 static readonly 배열을 읽지 않도록 상수로 유지. TierColors 길이 - 1
        internal const int MaxVisualTier = 5;

        private GameObject _player;
        private EnemyManager _enemies;
        private PlayerStats _stats;
        private InstancedMesh _experienceMesh;
        private InstancedMesh _potionMesh;
        private InstanceRenderer _experienceRenderer;
        private InstanceRenderer _potionRenderer;
        private Material[] _tierMaterials;
        private float _potionHeal;

        private PickupData _experience;
        private PickupData _potions;
        private NativeReference<float> _collected;
        private NativeReference<Unity.Mathematics.Random> _random;
        private JobHandle _jobHandle;
        private float _mergeTimer;

        private int _collectedExperience;
        private float _collectedHealing;

        // Job이 실행 중일 때도 읽을 수 있도록 NativeList 길이 대신 별도로 관리
        public int ExperienceObjectCount { get; private set; }
        public int PotionCount { get; private set; }

        // Enemy 1마리가 사망할 때 HP 포션이 나올 확률. GameManager가 플레이어 HP 비율에 따라 매 프레임 갱신
        public float PotionDropChance { get; set; }

        public void Initialize(GameObject player, EnemyManager enemies, GameObject experiencePrefab, GameObject potionPrefab, float potionHeal, PlayerStats stats)
        {
            if (!Utility.IsValid(player, this))
                return;

            if (!Utility.IsValid(enemies, this))
                return;

            if (!Utility.IsValid(experiencePrefab, this))
                return;

            if (!Utility.IsValid(potionPrefab, this))
                return;

            if (!Utility.IsValid(stats, this))
                return;

            if (!InstancedMesh.TryCreate(experiencePrefab, out var experienceMesh))
                return;

            if (!InstancedMesh.TryCreate(potionPrefab, out var potionMesh))
                return;

            _player = player;
            _enemies = enemies;
            _stats = stats;
            _experienceMesh = experienceMesh;
            _potionMesh = potionMesh;
            _potionHeal = potionHeal;

            // 단계마다 색만 다른 Material. Instancing 배치는 Material 단위이므로 단계별로 따로 렌더링
            _tierMaterials = new Material[TierColors.Length];
            var tierMeshes = new InstancedMesh[TierColors.Length];
            for (var tier = 0; tier < TierColors.Length; tier++)
            {
                var material = new Material(experienceMesh.Material) { name = $"{experienceMesh.Material.name}_Tier{tier}" };
                material.SetColor("_BaseColor", TierColors[tier]);
                material.SetColor("_Color", TierColors[tier]);
                _tierMaterials[tier] = material;
                tierMeshes[tier] = experienceMesh.WithMaterial(material);
            }

            _experienceRenderer = new InstanceRenderer(tierMeshes);
            _potionRenderer = new InstanceRenderer(potionMesh);

            _experience = new PickupData(InitialCapacity);
            _potions = new PickupData(InitialCapacity);
            _collected = new NativeReference<float>(Allocator.Persistent);
            _random = new NativeReference<Unity.Mathematics.Random>(new Unity.Mathematics.Random((uint)Environment.TickCount | 1), Allocator.Persistent);
        }

        // 마지막 호출 이후 획득한 경험치 / 회복량을 반환하고 초기화
        public int TakeExperience()
        {
            var value = _collectedExperience;
            _collectedExperience = 0;
            return value;
        }

        public float TakeHealing()
        {
            var value = _collectedHealing;
            _collectedHealing = 0;
            return value;
        }

        private void Update()
        {
            if (!_experience.IsCreated || !_player)
                return;

            var playerPosition = ((float3)_player.transform.position).xz;
            var magnetRange = Mathf.Max(_stats.Get(StatType.Magnet), CollectRadius);
            var deltaTime = Time.deltaTime;

            var experienceHandle = ScheduleMove(_experience, _experienceMesh, playerPosition, magnetRange, deltaTime);
            var potionHandle = ScheduleMove(_potions, _potionMesh, playerPosition, magnetRange, deltaTime);
            _jobHandle = JobHandle.CombineDependencies(experienceHandle, potionHandle);
        }

        private void LateUpdate()
        {
            _jobHandle.Complete();

            if (!_experience.IsCreated)
                return;

            _collectedExperience += (int)math.round(RemoveCollected(_experience));
            _collectedHealing += RemoveCollected(_potions);

            SpawnDrops();

            _mergeTimer -= Time.deltaTime;
            if (_mergeTimer <= 0)
            {
                _mergeTimer = MergeInterval;
                Merge();
            }

            ExperienceObjectCount = _experience.positions.Length;
            PotionCount = _potions.positions.Length;

            var camera = _enemies ? _enemies.CullingCamera : null;
            _experienceRenderer.Render(_experience.matrices.AsArray(), _experience.tiers.AsArray(), camera);
            _potionRenderer.Render(_potions.matrices.AsArray(), camera);
        }

        private void OnDestroy()
        {
            _jobHandle.Complete();
            ExperienceObjectCount = PotionCount = 0;

            _experience.Dispose();
            _potions.Dispose();

            if (_collected.IsCreated)
                _collected.Dispose();

            if (_random.IsCreated)
                _random.Dispose();

            _experienceRenderer?.Dispose();
            _experienceRenderer = null;

            _potionRenderer?.Dispose();
            _potionRenderer = null;

            if (_tierMaterials == null)
                return;

            foreach (var material in _tierMaterials)
                Destroy(material);

            _tierMaterials = null;
        }

        private static JobHandle ScheduleMove(PickupData data, InstancedMesh mesh, float2 player, float magnetRange, float deltaTime)
        {
            return new MoveJob
            {
                positions = data.positions.AsArray(),
                tiers = data.tiers.AsArray(),
                attracted = data.attracted.AsArray(),
                collected = data.collected.AsArray(),
                matrices = data.matrices.AsArray(),
                player = player,
                magnetRange = magnetRange,
                collectRadius = CollectRadius,
                deltaTime = deltaTime,
                rotation = mesh.Rotation,
                scale = mesh.Scale,
            }.Schedule(data.positions.Length, JobBatchCount);
        }

        // 획득했거나 병합으로 가치가 0이 된 Object를 제거하고, 획득한 가치의 합을 반환
        private float RemoveCollected(PickupData data)
        {
            new RemoveJob { data = data, collectedTotal = _collected }.Run();
            return _collected.Value;
        }

        private void SpawnDrops()
        {
            if (!_enemies || !_enemies.IsReady)
                return;

            new SpawnDropsJob
            {
                deaths = _enemies.DeathPositions,
                experience = _experience,
                potions = _potions,
                random = _random,
                experienceValue = _enemies.ExperiencePerKill,
                potionChance = Mathf.Clamp01(PotionDropChance),
                potionHeal = _potionHeal,
                experienceRotation = _experienceMesh.Rotation,
                experienceScale = _experienceMesh.Scale,
                potionRotation = _potionMesh.Rotation,
                potionScale = _potionMesh.Scale,
            }.Run();
        }

        private void Merge()
        {
            if (_experience.positions.Length < MergeCount)
                return;

            new MergeJob
            {
                positions = _experience.positions.AsArray(),
                values = _experience.values.AsArray(),
                tiers = _experience.tiers.AsArray(),
                attracted = _experience.attracted.AsArray(),
                matrices = _experience.matrices.AsArray(),
                cellSize = MergeCellSize,
                cellGrowth = MergeCellGrowth,
                rotation = _experienceMesh.Rotation,
                scale = _experienceMesh.Scale,
            }.Run();

            // 병합으로 가치가 0이 된 Object 제거. 같은 프레임에 렌더링되지 않도록 바로 정리
            RemoveCollected(_experience);
        }

        internal static float4x4 CalcMatrix(float3 position, int tier, quaternion rotation, float3 scale)
        {
            return float4x4.TRS(position, rotation, scale * (1 + TierScaleStep * math.min(tier, MaxVisualTier)));
        }

        // 한 종류의 드롭 목록. 인덱스가 같은 원소끼리 하나의 Object. Job에 그대로 넘길 수 있는 구조체
        internal struct PickupData : IDisposable
        {
            public NativeList<float3> positions;
            public NativeList<float> values;
            public NativeList<int> tiers;
            public NativeList<byte> attracted;      // 자석에 한 번 걸리면 획득할 때까지 계속 끌려옴
            public NativeList<byte> collected;
            public NativeList<Matrix4x4> matrices;

            public bool IsCreated => positions.IsCreated;

            public PickupData(int capacity)
            {
                positions = new NativeList<float3>(capacity, Allocator.Persistent);
                values = new NativeList<float>(capacity, Allocator.Persistent);
                tiers = new NativeList<int>(capacity, Allocator.Persistent);
                attracted = new NativeList<byte>(capacity, Allocator.Persistent);
                collected = new NativeList<byte>(capacity, Allocator.Persistent);
                matrices = new NativeList<Matrix4x4>(capacity, Allocator.Persistent);
            }

            public void Add(float3 position, float value, int tier, float4x4 matrix)
            {
                positions.Add(position);
                values.Add(value);
                tiers.Add(tier);
                attracted.Add(0);
                collected.Add(0);
                matrices.Add(matrix);
            }

            public void RemoveAtSwapBack(int index)
            {
                positions.RemoveAtSwapBack(index);
                values.RemoveAtSwapBack(index);
                tiers.RemoveAtSwapBack(index);
                attracted.RemoveAtSwapBack(index);
                collected.RemoveAtSwapBack(index);
                matrices.RemoveAtSwapBack(index);
            }

            public void Dispose()
            {
                if (!IsCreated)
                    return;

                positions.Dispose();
                values.Dispose();
                tiers.Dispose();
                attracted.Dispose();
                collected.Dispose();
                matrices.Dispose();
            }
        }

        // 자석 범위에 들어오면 플레이어 쪽으로 이동. 멀수록 빠르게 끌려와 범위가 넓어도 오래 걸리지 않음
        [BurstCompile]
        internal struct MoveJob : IJobParallelFor
        {
            public NativeArray<float3> positions;
            [ReadOnly] public NativeArray<int> tiers;
            public NativeArray<byte> attracted;
            [WriteOnly] public NativeArray<byte> collected;
            [WriteOnly] public NativeArray<Matrix4x4> matrices;
            public float2 player;
            public float magnetRange;
            public float collectRadius;
            public float deltaTime;
            public quaternion rotation;
            public float3 scale;

            public void Execute(int index)
            {
                var position = positions[index];
                var offset = player - position.xz;
                var distance = math.length(offset);

                if (attracted[index] == 0 && distance <= magnetRange)
                    attracted[index] = 1;

                if (attracted[index] != 0 && distance > 0)
                {
                    var step = math.min((PullSpeed + distance * PullAcceleration) * deltaTime, distance);
                    position.xz += offset * (step / distance);
                    distance -= step;
                    positions[index] = position;
                    matrices[index] = CalcMatrix(position, tiers[index], rotation, scale);
                }

                collected[index] = (byte)(distance <= collectRadius ? 1 : 0);
            }
        }

        // 획득한 Object는 가치를 더하고 제거, 병합으로 가치가 0이 된 Object는 그냥 제거
        [BurstCompile]
        internal struct RemoveJob : IJob
        {
            public PickupData data;
            public NativeReference<float> collectedTotal;

            public void Execute()
            {
                var total = 0f;
                for (var index = data.positions.Length - 1; index >= 0; --index)
                {
                    var collected = data.collected[index] != 0;
                    if (!collected && data.values[index] > 0)
                        continue;

                    if (collected)
                        total += data.values[index];

                    data.RemoveAtSwapBack(index);
                }

                collectedTotal.Value = total;
            }
        }

        // 이번 프레임에 사망한 Enemy 위치마다 경험치 Object, 확률로 HP 포션 생성
        [BurstCompile]
        internal struct SpawnDropsJob : IJob
        {
            [ReadOnly] public NativeArray<float3> deaths;
            public PickupData experience;
            public PickupData potions;
            public NativeReference<Unity.Mathematics.Random> random;
            public float experienceValue;
            public float potionChance;
            public float potionHeal;
            public quaternion experienceRotation;
            public float3 experienceScale;
            public quaternion potionRotation;
            public float3 potionScale;

            public void Execute()
            {
                var rng = random.Value;

                foreach (var position in deaths)
                {
                    if (experienceValue > 0)
                        experience.Add(position, experienceValue, 0, CalcMatrix(position, 0, experienceRotation, experienceScale));

                    if (potionChance > 0 && rng.NextFloat() < potionChance)
                        potions.Add(position, potionHeal, 0, float4x4.TRS(position, potionRotation, potionScale));
                }

                random.Value = rng;
            }
        }

        // 같은 셀 + 같은 단계인 Object를 정렬로 묶고, 10개씩 다음 단계 1개로 병합. 나머지 9개는 가치를 0으로 만들어 제거 대상으로 표시
        // 셀 크기는 단계마다 cellGrowth배. 끌려오는 중인 Object는 병합하지 않음
        [BurstCompile]
        internal struct MergeJob : IJob
        {
            public NativeArray<float3> positions;
            public NativeArray<float> values;
            public NativeArray<int> tiers;
            [ReadOnly] public NativeArray<byte> attracted;
            public NativeArray<Matrix4x4> matrices;
            public float cellSize;
            public float cellGrowth;
            public quaternion rotation;
            public float3 scale;

            private struct Entry : IComparable<Entry>
            {
                public int3 key;    // 셀 x, 셀 y, 단계
                public int index;

                public int CompareTo(Entry other)
                {
                    if (key.z != other.key.z) return key.z.CompareTo(other.key.z);
                    if (key.x != other.key.x) return key.x.CompareTo(other.key.x);
                    if (key.y != other.key.y) return key.y.CompareTo(other.key.y);
                    return index.CompareTo(other.index);
                }
            }

            public void Execute()
            {
                var entries = new NativeList<Entry>(positions.Length, Allocator.Temp);
                for (var index = 0; index < positions.Length; index++)
                {
                    if (attracted[index] != 0 || values[index] <= 0)
                        continue;

                    var tier = tiers[index];
                    var inverseCellSize = 1 / (cellSize * math.pow(cellGrowth, tier));
                    var cell = SpatialGrid.CellOf(positions[index].xz, inverseCellSize);
                    entries.Add(new Entry { key = new int3(cell, tier), index = index });
                }

                entries.Sort();

                var start = 0;
                while (start < entries.Length)
                {
                    var end = start + 1;
                    while (end < entries.Length && entries[end].key.Equals(entries[start].key))
                        ++end;

                    for (var group = start; group + MergeCount <= end; group += MergeCount)
                        MergeGroup(entries, group);

                    start = end;
                }
            }

            private void MergeGroup(NativeList<Entry> entries, int start)
            {
                var target = entries[start].index;
                var total = 0f;
                var center = float3.zero;

                for (var offset = 0; offset < MergeCount; offset++)
                {
                    var index = entries[start + offset].index;
                    total += values[index];
                    center += positions[index];

                    if (index != target)
                        values[index] = 0;
                }

                center /= MergeCount;
                var tier = tiers[target] + 1;

                positions[target] = center;
                values[target] = total;
                tiers[target] = tier;
                matrices[target] = CalcMatrix(center, tier, rotation, scale);
            }
        }
    }
}
