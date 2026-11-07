using aisilol.Common;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace aisilol.Game
{
    // 가장 가까운 Enemy를 향해 자동 발사. 투사체도 GameObject 없이 데이터로 이동/충돌/렌더링
    //
    // Update     : EnemyManager가 예약한 격자 Job 뒤에 이동 + 충돌 Job 예약. 맞힌 Enemy는 EnemyManager의 데미지 큐에 기록
    // LateUpdate : EnemyManager가 데미지를 적용한 뒤 실행. 수명이 끝났거나 관통 수를 다 쓴 투사체 제거 → 발사 → 렌더링
    [DefaultExecutionOrder(10)]
    public class ProjectileSystem : MonoBehaviour
    {
        private const int InitialCapacity = 256;
        private const int JobBatchCount = 64;
        private const float SpreadAngle = 10;   // 여러 발을 쏠 때 투사체 사이 각도(도)

        private GameObject _player;
        private EnemyManager _enemies;
        private PlayerStats _stats;
        private InstancedMesh _mesh;
        private InstanceRenderer _renderer;
        private float _hitRadius;

        private NativeList<float3> _positions;
        private NativeList<float3> _velocities;
        private NativeList<float> _lifetimes;
        private NativeList<float> _ages;
        private NativeList<int> _pierces;       // 남은 관통 수. 0이 되면 제거
        private NativeList<Matrix4x4> _matrices;
        private NativeReference<float4> _target;    // xyz: 사거리 안 가장 가까운 Enemy 위치, w: 1이면 찾음
        private bool _targetScheduled;              // 이번 프레임 목표 탐색 Job을 예약했는지
        private JobHandle _jobHandle;

        private float _cooldown;

        public int ProjectileCount { get; private set; }

        public void Initialize(GameObject player, EnemyManager enemies, GameObject projectilePrefab, PlayerStats stats)
        {
            if (!Utility.IsValid(player, this))
                return;

            if (!Utility.IsValid(enemies, this))
                return;

            if (!Utility.IsValid(projectilePrefab, this))
                return;

            if (!Utility.IsValid(stats, this))
                return;

            if (!InstancedMesh.TryCreate(projectilePrefab, out var mesh))
                return;

            _player = player;
            _enemies = enemies;
            _stats = stats;
            _mesh = mesh;
            _renderer = new InstanceRenderer(mesh);
            _hitRadius = Mathf.Max(mesh.Size.x, mesh.Size.z) * 0.5f + enemies.Radius;

            _positions = new NativeList<float3>(InitialCapacity, Allocator.Persistent);
            _velocities = new NativeList<float3>(InitialCapacity, Allocator.Persistent);
            _lifetimes = new NativeList<float>(InitialCapacity, Allocator.Persistent);
            _ages = new NativeList<float>(InitialCapacity, Allocator.Persistent);
            _pierces = new NativeList<int>(InitialCapacity, Allocator.Persistent);
            _matrices = new NativeList<Matrix4x4>(InitialCapacity, Allocator.Persistent);
            _target = new NativeReference<float4>(Allocator.Persistent);
        }

        private void Update()
        {
            _targetScheduled = false;

            if (!_positions.IsCreated || !_player || !_enemies || !_enemies.IsGridScheduled)
                return;

            // 목표는 이번 프레임 시작 위치 기준. LateUpdate에서 Enemy가 제거되어 인덱스가 바뀌므로 위치를 저장
            var targetHandle = new FindNearestJob
            {
                positions = _enemies.Positions,
                grid = _enemies.Grid,
                inverseCellSize = _enemies.InverseCellSize,
                origin = ((float3)_player.transform.position).xz,
                maxDistance = _stats.Get(StatType.Range),
                result = _target,
            }.Schedule(_enemies.GridHandle);

            _targetScheduled = true;
            _jobHandle = targetHandle;
            _enemies.AddDependency(targetHandle);

            if (_positions.Length == 0)
                return;

            var moveHandle = new MoveJob
            {
                enemyPositions = _enemies.Positions,
                grid = _enemies.Grid,
                damages = _enemies.DamageWriter,
                positions = _positions.AsArray(),
                velocities = _velocities.AsArray(),
                lifetimes = _lifetimes.AsArray(),
                ages = _ages.AsArray(),
                pierces = _pierces.AsArray(),
                matrices = _matrices.AsArray(),
                inverseCellSize = _enemies.InverseCellSize,
                hitRadius = _hitRadius,
                damage = _stats.Get(StatType.Damage),
                rotation = _mesh.Rotation,
                scale = _mesh.Scale,
                deltaTime = Time.deltaTime,
            }.Schedule(_positions.Length, JobBatchCount, _enemies.DamageDependency);

            _jobHandle = JobHandle.CombineDependencies(targetHandle, moveHandle);
            _enemies.AddDamageWriter(moveHandle);
        }

        private void LateUpdate()
        {
            _jobHandle.Complete();

            if (!_positions.IsCreated)
                return;

            RemoveExpired();
            Fire(Time.deltaTime);

            ProjectileCount = _positions.Length;
            _renderer.UseGpu = _enemies && _enemies.UseGpuRendering;
            _renderer.Render(_matrices.AsArray(), _enemies ? _enemies.CullingCamera : null);
        }

        private void OnDestroy()
        {
            _jobHandle.Complete();
            ProjectileCount = 0;

            if (_positions.IsCreated)
                _positions.Dispose();

            if (_velocities.IsCreated)
                _velocities.Dispose();

            if (_lifetimes.IsCreated)
                _lifetimes.Dispose();

            if (_ages.IsCreated)
                _ages.Dispose();

            if (_pierces.IsCreated)
                _pierces.Dispose();

            if (_matrices.IsCreated)
                _matrices.Dispose();

            if (_target.IsCreated)
                _target.Dispose();

            _renderer?.Dispose();
            _renderer = null;
        }

        private void RemoveExpired()
        {
            for (var index = _lifetimes.Length - 1; index >= 0; --index)
            {
                if (_lifetimes[index] > 0)
                    continue;

                _positions.RemoveAtSwapBack(index);
                _velocities.RemoveAtSwapBack(index);
                _lifetimes.RemoveAtSwapBack(index);
                _ages.RemoveAtSwapBack(index);
                _pierces.RemoveAtSwapBack(index);
                _matrices.RemoveAtSwapBack(index);
            }
        }

        private void Fire(float deltaTime)
        {
            _cooldown -= deltaTime;
            // 사거리 안에 Enemy가 없으면 대기하다가 들어오는 즉시 발사
            if (_cooldown > 0 || !_player || !_targetScheduled || _target.Value.w <= 0)
                return;

            var origin = (float3)_player.transform.position;
            var range = _stats.Get(StatType.Range);

            var speed = _stats.Get(StatType.ProjectileSpeed);
            if (speed <= 0)
                return;

            var target = _target.Value.xyz;
            var direction = math.normalizesafe((target - origin).xz, new float2(0, 1));
            var count = Mathf.Max(1, Mathf.FloorToInt(_stats.Get(StatType.ProjectileCount)));
            var lifetime = range / speed;
            var pierce = Mathf.Max(1, Mathf.FloorToInt(_stats.Get(StatType.Pierce)));

            // 투사체 높이는 Enemy 중심 높이에 맞춤
            origin.y = target.y;

            for (var index = 0; index < count; index++)
            {
                var angle = math.radians((index - (count - 1) * 0.5f) * SpreadAngle);
                math.sincos(angle, out var sin, out var cos);
                var rotated = new float2(direction.x * cos - direction.y * sin, direction.x * sin + direction.y * cos);

                _positions.Add(origin);
                _velocities.Add(new float3(rotated.x, 0, rotated.y) * speed);
                _lifetimes.Add(lifetime);
                _ages.Add(0);
                _pierces.Add(pierce);
                _matrices.Add(float4x4.TRS(origin, _mesh.Rotation, _mesh.Scale));
            }

            _cooldown = Mathf.Max(_stats.Get(StatType.FireInterval), 0.01f);
        }

        // 이번 프레임 이동 구간(선분)과 겹치는 Enemy를 가까운 순서로 남은 관통 수만큼 맞힘. 빠른 투사체가 Enemy를 건너뛰지 않음
        [BurstCompile]
        internal struct MoveJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> enemyPositions;
            [ReadOnly] public NativeParallelMultiHashMap<int2, int> grid;
            public NativeQueue<EnemyDamage>.ParallelWriter damages;

            public NativeArray<float3> positions;
            [ReadOnly] public NativeArray<float3> velocities;
            public NativeArray<float> lifetimes;
            public NativeArray<float> ages;
            public NativeArray<int> pierces;
            [WriteOnly] public NativeArray<Matrix4x4> matrices;

            public float inverseCellSize;
            public float hitRadius;
            public float damage;
            public quaternion rotation;
            public float3 scale;
            public float deltaTime;

            public void Execute(int index)
            {
                var start = positions[index];
                var end = start + velocities[index] * deltaTime;
                var lifetime = lifetimes[index] - deltaTime;
                var pierce = pierces[index];

                var visitor = new SegmentHitVisitor
                {
                    positions = enemyPositions,
                    start = start.xz,
                    delta = (end - start).xz,
                    radius = hitRadius,
                    maxHits = math.min(pierce, SegmentHitVisitor.MaxHitsPerFrame),
                    firstFrame = ages[index] <= 0,
                };

                var extent = new float2(hitRadius);
                SpatialGrid.Query(grid, inverseCellSize, math.min(start.xz, end.xz) - extent, math.max(start.xz, end.xz) + extent, ref visitor);

                for (var hit = 0; hit < visitor.hitIndices.Length; hit++)
                    damages.Enqueue(new EnemyDamage { index = visitor.hitIndices[hit], amount = damage });

                pierce -= visitor.hitIndices.Length;
                if (pierce <= 0)
                {
                    // 마지막으로 맞힌 지점에서 멈추고 제거
                    end = start + (end - start) * visitor.hitTs[visitor.hitTs.Length - 1];
                    lifetime = 0;
                }

                positions[index] = end;
                lifetimes[index] = lifetime;
                ages[index] += deltaTime;
                pierces[index] = pierce;
                matrices[index] = float4x4.TRS(end, rotation, scale);
            }
        }

        // 선분에서 Enemy 중심까지 가장 가까운 지점이 radius 이내인 Enemy를 선분 시작에 가까운 순서로 최대 maxHits개 수집
        // 지난 프레임에 맞힌 Enemy를 다시 맞히지 않도록, 발사 직후가 아니면 시작점이 이미 안에 있는 Enemy는 제외
        internal struct SegmentHitVisitor : SpatialGrid.IVisitor
        {
            public const int MaxHitsPerFrame = 8;

            public NativeArray<float3> positions;
            public float2 start;
            public float2 delta;
            public float radius;
            public int maxHits;
            public bool firstFrame;
            public FixedList64Bytes<int> hitIndices;
            public FixedList64Bytes<float> hitTs;

            public bool Visit(int index)
            {
                var center = positions[index].xz;
                var sqrRadius = radius * radius;

                if (!firstFrame && math.distancesq(start, center) <= sqrRadius)
                    return true;

                var lengthSq = math.lengthsq(delta);
                var t = lengthSq > 0 ? math.saturate(math.dot(center - start, delta) / lengthSq) : 0;
                if (math.distancesq(start + delta * t, center) > sqrRadius)
                    return true;

                var count = hitTs.Length;
                if (count >= maxHits && t >= hitTs[count - 1])
                    return true;

                if (count >= maxHits)
                {
                    hitTs.RemoveAt(count - 1);
                    hitIndices.RemoveAt(count - 1);
                    --count;
                }

                var at = count;
                while (at > 0 && hitTs[at - 1] > t)
                    --at;

                hitTs.Insert(at, t);
                hitIndices.Insert(at, index);
                return true;
            }
        }

        // 사거리 안에서 가장 가까운 Enemy 위치. 결과 w가 1이면 찾음, 0이면 없음
        // 격자에서 origin 셀부터 한 겹씩 넓혀 가며 찾고, 남은 바깥 겹이 찾은 거리보다 멀면 중단
        // 사거리가 커서 검사할 셀 수가 Enemy 수보다 많으면 전체 순회가 더 빠르므로 전체 순회
        [BurstCompile]
        internal struct FindNearestJob : IJob
        {
            [ReadOnly] public NativeArray<float3> positions;
            [ReadOnly] public NativeParallelMultiHashMap<int2, int> grid;
            public NativeReference<float4> result;
            public float inverseCellSize;
            public float2 origin;
            public float maxDistance;

            public void Execute()
            {
                var visitor = new NearestVisitor
                {
                    positions = positions,
                    origin = origin,
                    nearest = -1,
                    nearestSqr = maxDistance * maxDistance,
                };

                var maxRing = (int)math.min(math.ceil(maxDistance * inverseCellSize), int.MaxValue / 4f);
                var side = 2L * maxRing + 1;
                if (side * side > positions.Length)
                {
                    for (var index = 0; index < positions.Length; index++)
                        visitor.Visit(index);
                }
                else
                {
                    SearchRings(ref visitor, maxRing);
                }

                result.Value = visitor.nearest >= 0 ? new float4(positions[visitor.nearest], 1) : float4.zero;
            }

            private void SearchRings(ref NearestVisitor visitor, int maxRing)
            {
                var center = SpatialGrid.CellOf(origin, inverseCellSize);
                var cellSize = 1 / inverseCellSize;

                for (var ring = 0; ring <= maxRing; ring++)
                {
                    if (ring == 0)
                    {
                        SpatialGrid.VisitCell(grid, center, ref visitor);
                    }
                    else
                    {
                        for (var x = -ring; x <= ring; x++)
                        {
                            SpatialGrid.VisitCell(grid, center + new int2(x, -ring), ref visitor);
                            SpatialGrid.VisitCell(grid, center + new int2(x, ring), ref visitor);
                        }

                        for (var y = -ring + 1; y < ring; y++)
                        {
                            SpatialGrid.VisitCell(grid, center + new int2(-ring, y), ref visitor);
                            SpatialGrid.VisitCell(grid, center + new int2(ring, y), ref visitor);
                        }
                    }

                    // 다음 겹의 셀은 origin에서 최소 ring * cellSize 떨어져 있음
                    var boundary = ring * cellSize;
                    if (visitor.nearest >= 0 && visitor.nearestSqr <= boundary * boundary)
                        return;
                }
            }
        }

        internal struct NearestVisitor : SpatialGrid.IVisitor
        {
            public NativeArray<float3> positions;
            public float2 origin;
            public int nearest;
            public float nearestSqr;

            public bool Visit(int index)
            {
                var sqrDistance = math.distancesq(positions[index].xz, origin);
                if (sqrDistance <= nearestSqr)
                {
                    nearest = index;
                    nearestSqr = sqrDistance;
                }

                return true;
            }
        }
    }
}
