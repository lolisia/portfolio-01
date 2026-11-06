using System.Collections.Generic;
using aisilol.Common;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace aisilol.Game
{
    internal struct EnemyDamage
    {
        public int index;
        public float amount;
    }

    // Enemy는 GameObject 없이 위치/속도/체력 데이터로만 관리하고, GPU Instancing으로 일괄 렌더링
    // Enemy Prefab은 Mesh / Material / Scale / 능력치를 읽어오는 템플릿으로만 사용
    //
    // Update     : 생성 → 격자 구성 Job → 이동 / 플레이어 접촉 Job 예약. 다른 시스템도 격자를 읽는 Job을 이어서 예약
    // LateUpdate : 모든 Job 완료 → 위치 버퍼 교체 → 데미지 적용 / 사망 제거 → 렌더링
    public class EnemyManager : MonoBehaviour
    {
        private const float MaxSpawnDistance = 200;     // 지평선이 보이는 경우 무한히 멀어지지 않도록 제한하는 최대 거리
        private const int InitialCapacity = 1024;
        private const int JobBatchCount = 64;
        private const int MaxEnemyCount = 30000;
        private const float SeparationSpeed = 1;        // 완전히 겹쳤을 때 서로 밀어내는 속도(m/s)
        private const int MaxSeparationNeighbors = 16;  // 밀집 지역에서 Enemy 1개당 확인하는 이웃 수 제한

        private GameObject _player;
        private Camera _gameCamera;
        private InstancedMesh _mesh;

        private Vector2 _speedRange;
        private float _maxHealth;
        private float _halfHeight;
        private float _radius;
        private float _contactRadius;
        private float _stopDistance;
        private float _inverseCellSize;

        private static readonly Vector3[] ViewportCorners = { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0), };

        // 이동 Job은 _positions(이번 프레임 시작 위치)를 읽고 _nextPositions에 기록. 이웃 위치를 읽는 동안 덮어쓰지 않기 위한 이중 버퍼
        private NativeList<float3> _positions;
        private NativeList<float3> _nextPositions;
        private NativeList<float> _speeds;
        private NativeList<float> _healths;
        private NativeList<Matrix4x4> _matrices;
        private NativeParallelMultiHashMap<int2, int> _grid;
        private NativeQueue<EnemyDamage> _damageQueue;
        private NativeReference<int> _contactCount;
        private readonly List<EnemyDamage> _pendingDamages = new();

        private JobHandle _gridHandle;
        private JobHandle _jobHandle;   // 이 시스템과 외부 시스템이 이번 프레임에 예약한 Job 전체

        private float _spawnEnemyDistance;
        private float _spawnAccumulator;

        // Job이 실행 중일 때도 읽을 수 있도록 NativeList 길이 대신 별도로 관리
        public int EnemyCount { get; private set; }
        public int KillCount { get; private set; }
        public int ContactCount { get; private set; }   // 플레이어와 닿아 있는 Enemy 수
        public float ContactDamage { get; private set; }
        public int ExperiencePerKill { get; private set; }

        public float SpawnPerSecond { get; set; } = 60;
        public float HealthMultiplier { get; set; } = 1;

        internal bool IsReady => _positions.IsCreated;

        // 이번 프레임의 격자가 예약되어 있는지. true인 동안만 Positions / Grid를 읽는 Job을 예약할 수 있음
        internal bool IsGridScheduled { get; private set; }
        internal NativeArray<float3> Positions => _positions.AsArray();
        internal NativeParallelMultiHashMap<int2, int> Grid => _grid;
        internal JobHandle GridHandle => _gridHandle;
        internal float InverseCellSize => _inverseCellSize;
        internal float Radius => _radius;
        internal NativeQueue<EnemyDamage>.ParallelWriter DamageWriter => _damageQueue.AsParallelWriter();

        public void Initialize(GameObject player, GameCamera gameCamera, GameObject enemyPrefab)
        {
            if (!Utility.IsValid(player, this))
                return;

            if (!Utility.IsValid(gameCamera, this))
                return;

            if (!Utility.IsValid(enemyPrefab, this))
                return;

            if (!Utility.TryGetComponent<Camera>(gameCamera.gameObject, out var cameraComponent, this))
                return;

            if (!InstancedMesh.TryCreate(enemyPrefab, out var mesh))
                return;

            if (!Utility.TryGetComponent<Enemy>(enemyPrefab, out var enemy, enemyPrefab))
                return;

            _halfHeight = mesh.Size.y * 0.5f;
            if (_halfHeight <= 0)
            {
                Log.Error($"Enemy의 높이가 0 입니다. [Size : {mesh.Size}]", enemyPrefab);
                return;
            }

            _mesh = mesh;
            _speedRange = enemy.moveSpeed;
            _maxHealth = enemy.maxHealth;
            ContactDamage = enemy.contactDamage;
            ExperiencePerKill = enemy.experience;

            // 셀 크기는 Enemy 지름. 밀어내기 검색 범위(지름)가 주변 셀 안에 들어옴
            _radius = Mathf.Max(mesh.Size.x, mesh.Size.z) * 0.5f;
            _inverseCellSize = 1 / Mathf.Max(_radius * 2, 0.01f);

            var playerRadius = 0f;
            if (player.TryGetComponent<MeshFilter>(out var playerMeshFilter) && playerMeshFilter.sharedMesh)
            {
                var playerSize = Vector3.Scale(playerMeshFilter.sharedMesh.bounds.size, player.transform.localScale);
                playerRadius = Mathf.Max(playerSize.x, playerSize.z) * 0.5f;
            }

            // 접촉 판정 거리보다 조금 안쪽까지 다가와서 멈춤
            _contactRadius = _radius + playerRadius;
            _stopDistance = _contactRadius * 0.8f;

            _player = player;

            _gameCamera = cameraComponent;
            gameCamera.onZoomChanged += CalcSpawnEnemyDistance;

            _positions = new NativeList<float3>(InitialCapacity, Allocator.Persistent);
            _nextPositions = new NativeList<float3>(InitialCapacity, Allocator.Persistent);
            _speeds = new NativeList<float>(InitialCapacity, Allocator.Persistent);
            _healths = new NativeList<float>(InitialCapacity, Allocator.Persistent);
            _matrices = new NativeList<Matrix4x4>(InitialCapacity, Allocator.Persistent);
            _grid = new NativeParallelMultiHashMap<int2, int>(InitialCapacity, Allocator.Persistent);
            _damageQueue = new NativeQueue<EnemyDamage>(Allocator.Persistent);
            _contactCount = new NativeReference<int>(Allocator.Persistent);
        }

        // 메인 스레드에서 데미지 예약. LateUpdate에서 Job이 기록한 데미지와 함께 적용
        internal void QueueDamage(int index, float amount)
        {
            _pendingDamages.Add(new EnemyDamage { index = index, amount = amount });
        }

        // 격자를 읽는 외부 Job. LateUpdate에서 함께 완료한 뒤 데미지를 적용
        internal void AddDependency(JobHandle handle)
        {
            _jobHandle = JobHandle.CombineDependencies(_jobHandle, handle);
        }

        private void Update()
        {
            if (!_positions.IsCreated || !_player)
                return;

            Spawn();

            var count = _positions.Length;
            var positions = _positions.AsArray();
            var playerPosition = (float3)_player.transform.position;

            _grid.Clear();
            if (_grid.Capacity < count)
                _grid.Capacity = count;

            _gridHandle = new SpatialGrid.BuildJob
            {
                positions = positions,
                grid = _grid.AsParallelWriter(),
                inverseCellSize = _inverseCellSize,
            }.Schedule(count, JobBatchCount);

            var moveHandle = new MoveJob
            {
                positions = positions,
                speeds = _speeds.AsArray(),
                grid = _grid,
                nextPositions = _nextPositions.AsArray(),
                matrices = _matrices.AsArray(),
                inverseCellSize = _inverseCellSize,
                separationRadius = _radius * 2,
                target = playerPosition,
                stopDistance = _stopDistance,
                rotation = _mesh.Rotation,
                scale = _mesh.Scale,
                deltaTime = Time.deltaTime,
            }.Schedule(count, JobBatchCount, _gridHandle);

            var contactHandle = new ContactJob
            {
                positions = positions,
                grid = _grid,
                inverseCellSize = _inverseCellSize,
                center = playerPosition.xz,
                radius = _contactRadius,
                result = _contactCount,
            }.Schedule(_gridHandle);

            _jobHandle = JobHandle.CombineDependencies(_jobHandle, moveHandle, contactHandle);
            IsGridScheduled = true;
        }

        private void LateUpdate()
        {
            _jobHandle.Complete();
            _jobHandle = default;

            if (!_positions.IsCreated)
                return;

            if (IsGridScheduled)
            {
                (_positions, _nextPositions) = (_nextPositions, _positions);
                ContactCount = _contactCount.Value;
                IsGridScheduled = false;
            }

            ApplyDamages();

            _mesh.Render(_matrices.AsArray());
        }

        private void OnDestroy()
        {
            _jobHandle.Complete();
            EnemyCount = 0;

            if (_positions.IsCreated)
                _positions.Dispose();

            if (_nextPositions.IsCreated)
                _nextPositions.Dispose();

            if (_speeds.IsCreated)
                _speeds.Dispose();

            if (_healths.IsCreated)
                _healths.Dispose();

            if (_matrices.IsCreated)
                _matrices.Dispose();

            if (_grid.IsCreated)
                _grid.Dispose();

            if (_damageQueue.IsCreated)
                _damageQueue.Dispose();

            if (_contactCount.IsCreated)
                _contactCount.Dispose();
        }

        // 모든 데미지를 먼저 적용한 뒤 사망한 Enemy를 제거. 데미지의 인덱스는 제거 전 기준
        private void ApplyDamages()
        {
            var anyDead = false;

            while (_damageQueue.TryDequeue(out var damage))
                anyDead |= ApplyDamage(damage);

            foreach (var damage in _pendingDamages)
                anyDead |= ApplyDamage(damage);

            _pendingDamages.Clear();

            if (!anyDead)
                return;

            // 뒤에서부터 제거하면 끝에서 옮겨 온 Enemy는 이미 확인한 Enemy
            for (var index = _healths.Length - 1; index >= 0; --index)
            {
                if (_healths[index] > 0)
                    continue;

                _positions.RemoveAtSwapBack(index);
                _nextPositions.RemoveAtSwapBack(index);
                _speeds.RemoveAtSwapBack(index);
                _healths.RemoveAtSwapBack(index);
                _matrices.RemoveAtSwapBack(index);
                ++KillCount;
            }

            EnemyCount = _positions.Length;
        }

        // 이번 데미지로 사망했으면 true. 이미 사망한 Enemy는 무시
        private bool ApplyDamage(EnemyDamage damage)
        {
            if ((uint)damage.index >= (uint)_healths.Length)
                return false;

            var health = _healths[damage.index];
            if (health <= 0)
                return false;

            health -= damage.amount;
            _healths[damage.index] = health;
            return health <= 0;
        }

        private void Spawn()
        {
            // Spawn Distance가 아직 계산 전이면 Skip
            if (Mathf.Approximately(_spawnEnemyDistance, MaxSpawnDistance) || _spawnEnemyDistance == 0)
            {
                CalcSpawnEnemyDistance();
                return;
            }

            // 프레임 속도와 관계없이 초당 생성 수를 유지하도록 소수점 이하는 다음 프레임으로 이월
            _spawnAccumulator += SpawnPerSecond * Time.deltaTime;
            var count = (int)_spawnAccumulator;
            _spawnAccumulator -= count;

            count = Mathf.Min(count, MaxEnemyCount - _positions.Length);
            var health = _maxHealth * HealthMultiplier;

            for (var index = 0; index < count; index++)
            {
                var position = CalcSpawnPosition();
                _positions.Add(position);
                _nextPositions.Add(position);
                _speeds.Add(UnityEngine.Random.Range(_speedRange.x, _speedRange.y));
                _healths.Add(health);
                _matrices.Add(default);    // MoveJob에서 매 프레임 채움
            }

            EnemyCount = _positions.Length;
        }

        private void CalcSpawnEnemyDistance()
        {
            var origin = _player.transform.position;
            origin.y = _halfHeight;

            _spawnEnemyDistance = CalcMaxSpawnDistance(origin);
        }

        // 플레이어 기준 랜덤 방향으로, 최대 Spawn 거리만큼 떨어진 지점을 반환
        private Vector3 CalcSpawnPosition()
        {
            var angle = UnityEngine.Random.Range(0, Mathf.PI * 2);
            var direction = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));

            var origin = _player.transform.position;
            origin.y = _halfHeight;

            return origin + direction * _spawnEnemyDistance;
        }

        // 화면 영역 중 플레이어에게서 가장 먼 지점까지의 거리. 이 거리 밖은 어느 방향이든 화면 밖
        // 화면 영역은 볼록 사각형이므로 가장 먼 지점은 화면 네 모서리 중 하나
        private float CalcMaxSpawnDistance(Vector3 origin)
        {
            var maxSqrDistance = 0f;

            var plane = new Plane(Vector3.up, origin);

            foreach (var corner in ViewportCorners)
            {
                var ray = _gameCamera.ViewportPointToRay(corner);

                // 평면에 닿지 않는 모서리(지평선 위)는 최대 거리까지 보이는 것으로 간주
                if (!plane.Raycast(ray, out var enter))
                    return MaxSpawnDistance;

                var offset = ray.GetPoint(enter) - origin;
                offset.y = 0;
                maxSqrDistance = Mathf.Max(maxSqrDistance, offset.sqrMagnitude);
            }

            return Mathf.Min(Mathf.Sqrt(maxSqrDistance), MaxSpawnDistance);
        }

        // 플레이어 방향 이동 + 주변 Enemy와 겹치지 않도록 밀어내기
        [BurstCompile]
        internal struct MoveJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> positions;
            [ReadOnly] public NativeArray<float> speeds;
            [ReadOnly] public NativeParallelMultiHashMap<int2, int> grid;
            [WriteOnly] public NativeArray<float3> nextPositions;
            [WriteOnly] public NativeArray<Matrix4x4> matrices;
            public float inverseCellSize;
            public float separationRadius;
            public float3 target;
            public float stopDistance;
            public quaternion rotation;
            public float3 scale;
            public float deltaTime;

            public void Execute(int index)
            {
                var position = positions[index];

                // 높이는 Spawn 시점 값을 유지. 멈추는 거리를 지나치지 않도록 이동량 제한
                var direction = (target - position).xz;
                var distance = math.length(direction);
                var step = math.min(speeds[index] * deltaTime, distance - stopDistance);
                var move = step > 0 ? direction * (step / distance) : float2.zero;

                var separation = new SeparationVisitor
                {
                    positions = positions,
                    self = index,
                    center = position.xz,
                    radius = separationRadius,
                };

                var extent = new float2(separationRadius);
                SpatialGrid.Query(grid, inverseCellSize, position.xz - extent, position.xz + extent, ref separation);
                position.xz += move + separation.push * (SeparationSpeed * deltaTime);
                nextPositions[index] = position;
                matrices[index] = float4x4.TRS(position, rotation, scale);
            }
        }

        // 겹친 깊이에 비례해 서로 멀어지는 방향의 합. 완전히 겹치면 길이 1
        internal struct SeparationVisitor : SpatialGrid.IVisitor
        {
            public NativeArray<float3> positions;
            public int self;
            public float2 center;
            public float radius;
            public float2 push;
            private int _neighbors;

            public bool Visit(int index)
            {
                if (index == self)
                    return true;

                var offset = center - positions[index].xz;
                var sqrDistance = math.lengthsq(offset);
                if (sqrDistance >= radius * radius)
                    return true;

                // 같은 위치면 방향을 정할 수 없으므로 인덱스 순서로 좌우를 나눔
                if (sqrDistance < 1e-10f)
                {
                    push += new float2(self < index ? -1 : 1, 0);
                }
                else
                {
                    var distance = math.sqrt(sqrDistance);
                    push += offset / distance * (1 - distance / radius);
                }

                return ++_neighbors < MaxSeparationNeighbors;
            }
        }

        [BurstCompile]
        internal struct ContactJob : IJob
        {
            [ReadOnly] public NativeArray<float3> positions;
            [ReadOnly] public NativeParallelMultiHashMap<int2, int> grid;
            public NativeReference<int> result;
            public float inverseCellSize;
            public float2 center;
            public float radius;

            public void Execute()
            {
                var visitor = new SpatialGrid.CountVisitor
                {
                    positions = positions,
                    center = center,
                    radius = radius,
                };

                SpatialGrid.Query(grid, inverseCellSize, center - radius, center + radius, ref visitor);
                result.Value = visitor.count;
            }
        }
    }
}
