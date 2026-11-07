using aisilol.Common;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace aisilol.Game
{
    // 투사체 외 무기. 모두 Enemy 격자를 검색해 데미지 큐에 기록하는 Burst Job
    // 오라   : 틱마다 플레이어 주변 반경 안의 모든 Enemy에 피해. 레벨이 오르면 범위 / 틱 속도 / 틱당 피해가 함께 증가
    // 칼날   : 플레이어 주위를 도는 칼날에 닿은 Enemy에 초당 피해
    // 연쇄 번개: 주기마다 가까운 Enemy를 맞히고, 맞히지 않은 가장 가까운 Enemy로 이어서 튐
    //
    // Update     : EnemyManager가 예약한 격자 Job 뒤에 오라 → 칼날 → 연쇄 번개 Job을 순서대로 예약
    // LateUpdate : EnemyManager가 데미지를 적용한 뒤 실행. 효과 표시 → 칼날 렌더링
    [DefaultExecutionOrder(15)]
    public class WeaponSystem : MonoBehaviour
    {
        private const string EffectShaderPath = "Shaders/EffectUnlit";
        private const int JobBatchCount = 4;

        private const float AuraBaseRadius = 0.15f;         // 1레벨 범위(m)
        private const float AuraRadiusPerLevel = 0.035f;    // 레벨당 범위 증가(m)
        private const float AuraBaseTickInterval = 0.5f;    // 1레벨 틱 간격(초)
        private const float AuraTickRateGrowth = 0.15f;     // 레벨당 초당 틱 수 15% 증가
        private const float AuraBaseDamageRatio = 0.1f;     // 1레벨 틱당 피해 = Damage * 비율
        private const float AuraDamageRatioPerLevel = 0.005f; // 레벨당 틱당 피해 비율 증가
        private const float AuraPulseDuration = 0.15f;      // 틱마다 오라가 밝아지는 시간(초)
        private const float OrbitRadius = 1.6f;         // 칼날이 도는 반경(m)
        private const float OrbitSpeed = 3;             // rad/s
        private const float OrbitDamageRatio = 4;       // 칼날에 닿아 있는 동안 초당 피해 = Damage * 비율
        private const float BladeScale = 2;             // 투사체 Mesh 대비 칼날 크기
        internal const int MaxBlades = 32;

        private const float ChainInterval = 1;          // 연쇄 번개 주기(초)
        private const float ChainRange = 5;             // 첫 대상을 찾는 거리(m)
        private const float ChainJumpRange = 3;         // 다음 대상으로 튀는 거리(m)
        private const float ChainDamageRatio = 2;       // 1회 피해 = Damage * 비율
        internal const int MaxChainTargets = 30;        // FixedList128Bytes<int>에 들어가는 수
        private const float BoltDuration = 0.2f;        // 번개가 보이는 시간(초)

        private static readonly Color AuraColor = new(0.3f, 0.7f, 1f, 0.35f);
        private static readonly Color BoltColor = new(0.7f, 0.85f, 1f, 1f);
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int RingId = Shader.PropertyToID("_Ring");

        // GPU 렌더링 시 칼날이 빠르게 회전
        private static readonly VertexAnimation BladeAnimation = new() { spin = 12 };

        private GameObject _player;
        private EnemyManager _enemies;
        private PlayerStats _stats;
        private InstancedMesh _bladeMesh;
        private InstanceRenderer _bladeRenderer;
        private float _bladeHitRadius;

        private NativeList<float3> _bladePositions;
        private NativeList<Matrix4x4> _bladeMatrices;
        private NativeList<float3> _chainPoints;    // 플레이어 위치 + 맞힌 Enemy 위치 순서
        private JobHandle _jobHandle;

        private float _orbitAngle;
        private float _auraTimer;
        private float _auraPulse;
        private float _chainCooldown;
        private bool _chainScheduled;
        private Unity.Mathematics.Random _random = new((uint)System.Environment.TickCount | 1);

        private GameObject _aura;
        private LineRenderer _bolt;
        private Material _auraMaterial;
        private Material _boltMaterial;
        private float _boltTimer;

        public int AuraLevel { get; private set; }
        public float AuraRadius { get; private set; }
        public int AuraTicks { get; private set; }      // 피해를 준 오라 틱 수
        public int BladeCount => _bladePositions.IsCreated ? _bladePositions.Length : 0;
        public int ChainStrikes { get; private set; }   // 1명 이상 맞힌 연쇄 번개 횟수
        public int LastChainHits { get; private set; }  // 마지막 연쇄 번개가 맞힌 Enemy 수
        internal bool IsAuraVisible => _aura && _aura.activeSelf;
        internal bool IsBoltVisible => _bolt && _bolt.enabled;

        public void Initialize(GameObject player, EnemyManager enemies, GameObject bladePrefab, PlayerStats stats)
        {
            if (!Utility.IsValid(player, this))
                return;

            if (!Utility.IsValid(enemies, this))
                return;

            if (!Utility.IsValid(bladePrefab, this))
                return;

            if (!Utility.IsValid(stats, this))
                return;

            if (!InstancedMesh.TryCreate(bladePrefab, out var mesh))
                return;

            _player = player;
            _enemies = enemies;
            _stats = stats;
            _bladeMesh = mesh;
            _bladeRenderer = new InstanceRenderer(BladeAnimation, mesh);
            _bladeHitRadius = Mathf.Max(mesh.Size.x, mesh.Size.z) * 0.5f * BladeScale + enemies.Radius;

            _bladePositions = new NativeList<float3>(MaxBlades, Allocator.Persistent);
            _bladeMatrices = new NativeList<Matrix4x4>(MaxBlades, Allocator.Persistent);
            _chainPoints = new NativeList<float3>(MaxChainTargets + 1, Allocator.Persistent);

            CreateEffects();
        }

        private void Update()
        {
            if (!_bladePositions.IsCreated || !_player)
                return;

            var deltaTime = Time.deltaTime;
            var center = (float3)_player.transform.position;
            UpdateBlades(center, deltaTime);

            _chainCooldown -= deltaTime;
            _chainScheduled = false;

            // 얻기 전에는 타이머를 0으로 두어 얻자마자 첫 틱
            AuraLevel = Mathf.Max(0, Mathf.FloorToInt(_stats.Get(StatType.AuraLevel)));
            AuraRadius = AuraRadiusFor(AuraLevel);
            _auraTimer = AuraLevel > 0 ? _auraTimer - deltaTime : 0;

            if (!_enemies || !_enemies.IsGridScheduled)
                return;

            var damage = _stats.Get(StatType.Damage);
            var handle = _enemies.DamageDependency;

            if (AuraLevel > 0 && _auraTimer <= 0)
            {
                // 한 프레임이 틱 간격보다 길어도 밀린 틱을 몰아서 주지 않음
                var interval = AuraTickIntervalFor(AuraLevel);
                _auraTimer += interval;
                if (_auraTimer <= 0)
                    _auraTimer = interval;

                ++AuraTicks;
                _auraPulse = AuraPulseDuration;

                handle = new AreaDamageJob
                {
                    positions = _enemies.Positions,
                    grid = _enemies.Grid,
                    damages = _enemies.DamageWriter,
                    inverseCellSize = _enemies.InverseCellSize,
                    center = center.xz,
                    radius = AuraRadius + _enemies.Radius,
                    amount = damage * AuraDamageRatioFor(AuraLevel),
                }.Schedule(handle);
            }

            if (_bladePositions.Length > 0)
            {
                handle = new BladeJob
                {
                    blades = _bladePositions.AsArray(),
                    positions = _enemies.Positions,
                    grid = _enemies.Grid,
                    damages = _enemies.DamageWriter,
                    inverseCellSize = _enemies.InverseCellSize,
                    radius = _bladeHitRadius,
                    amount = damage * OrbitDamageRatio * deltaTime,
                }.Schedule(_bladePositions.Length, JobBatchCount, handle);
            }

            var chainTargets = Mathf.Clamp(Mathf.FloorToInt(_stats.Get(StatType.ChainCount)), 0, MaxChainTargets);
            if (chainTargets > 0 && _chainCooldown <= 0)
            {
                handle = new ChainJob
                {
                    positions = _enemies.Positions,
                    grid = _enemies.Grid,
                    damages = _enemies.DamageWriter,
                    points = _chainPoints,
                    inverseCellSize = _enemies.InverseCellSize,
                    origin = center,
                    firstRange = ChainRange,
                    jumpRange = ChainJumpRange,
                    maxTargets = chainTargets,
                    amount = damage * ChainDamageRatio,
                }.Schedule(handle);

                _chainScheduled = true;
            }

            _jobHandle = handle;
            _enemies.AddDamageWriter(handle);
        }

        private void LateUpdate()
        {
            _jobHandle.Complete();
            _jobHandle = default;

            if (!_bladePositions.IsCreated)
                return;

            // 이번 프레임에 나타난 번개가 바로 사라지지 않도록 효과 갱신을 먼저 수행
            UpdateEffects(Time.deltaTime);

            if (_chainScheduled)
            {
                _chainScheduled = false;
                LastChainHits = Mathf.Max(0, _chainPoints.Length - 1);

                // 맞힌 Enemy가 없으면 주기를 기다리지 않고 다음 프레임에 다시 시도
                if (LastChainHits > 0)
                {
                    ++ChainStrikes;
                    _chainCooldown = ChainInterval;
                    ShowBolt();
                }
            }

            _bladeRenderer.UseGpu = _enemies && _enemies.UseGpuRendering;
            _bladeRenderer.Render(_bladeMatrices.AsArray(), _enemies ? _enemies.CullingCamera : null);
        }

        private void OnDestroy()
        {
            _jobHandle.Complete();

            if (_bladePositions.IsCreated)
                _bladePositions.Dispose();

            if (_bladeMatrices.IsCreated)
                _bladeMatrices.Dispose();

            if (_chainPoints.IsCreated)
                _chainPoints.Dispose();

            _bladeRenderer?.Dispose();
            _bladeRenderer = null;

            if (_aura)
                Destroy(_aura);

            if (_bolt)
                Destroy(_bolt.gameObject);

            if (_auraMaterial)
                Destroy(_auraMaterial);

            if (_boltMaterial)
                Destroy(_boltMaterial);
        }

        internal static float AuraRadiusFor(int level)
        {
            return level > 0 ? AuraBaseRadius + AuraRadiusPerLevel * (level - 1) : 0;
        }

        internal static float AuraTickIntervalFor(int level)
        {
            return AuraBaseTickInterval / (1 + AuraTickRateGrowth * Mathf.Max(0, level - 1));
        }

        internal static float AuraDamageRatioFor(int level)
        {
            return AuraBaseDamageRatio + AuraDamageRatioPerLevel * Mathf.Max(0, level - 1);
        }

        // 칼날 수만큼 같은 간격으로 플레이어 주위에 배치. 진행 방향을 바라보도록 회전
        private void UpdateBlades(float3 center, float deltaTime)
        {
            var count = Mathf.Clamp(Mathf.FloorToInt(_stats.Get(StatType.OrbitCount)), 0, MaxBlades);
            _orbitAngle = (_orbitAngle + OrbitSpeed * deltaTime) % (math.PI * 2);

            _bladePositions.ResizeUninitialized(count);
            _bladeMatrices.ResizeUninitialized(count);

            for (var index = 0; index < count; index++)
            {
                var angle = _orbitAngle + index * math.PI * 2 / count;
                math.sincos(angle, out var sin, out var cos);
                var position = center + new float3(cos, 0, sin) * OrbitRadius;
                var rotation = math.mul(quaternion.RotateY(-angle), _bladeMesh.Rotation);

                _bladePositions[index] = position;
                _bladeMatrices[index] = float4x4.TRS(position, rotation, (float3)_bladeMesh.Scale * BladeScale);
            }
        }

        // Shader를 찾지 못하면 효과 없이 피해만 적용
        private void CreateEffects()
        {
            var shader = Resources.Load<Shader>(EffectShaderPath);
            if (!shader)
                return;

            _auraMaterial = new Material(shader) { name = "Aura" };
            _auraMaterial.SetColor(BaseColorId, AuraColor);
            _auraMaterial.SetFloat(RingId, 1);

            _aura = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _aura.name = "Aura";
            Destroy(_aura.GetComponent<Collider>());
            _aura.transform.rotation = Quaternion.Euler(90, 0, 0);
            var auraRenderer = _aura.GetComponent<MeshRenderer>();
            auraRenderer.sharedMaterial = _auraMaterial;
            auraRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            auraRenderer.receiveShadows = false;
            _aura.SetActive(false);

            _boltMaterial = new Material(shader) { name = "ChainBolt" };
            _boltMaterial.SetColor(BaseColorId, BoltColor);

            _bolt = new GameObject("ChainBolt").AddComponent<LineRenderer>();
            _bolt.sharedMaterial = _boltMaterial;
            _bolt.widthMultiplier = 0.02f;
            _bolt.useWorldSpace = true;
            _bolt.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _bolt.receiveShadows = false;
            _bolt.enabled = false;
        }

        // 구간마다 중간 지점을 옆으로 흔들어 번개처럼 꺾인 선으로 표시
        private void ShowBolt()
        {
            _boltTimer = BoltDuration;
            if (!_bolt)
                return;

            var count = _chainPoints.Length;
            _bolt.positionCount = count * 2 - 1;

            for (var index = 0; index < count; index++)
            {
                _bolt.SetPosition(index * 2, _chainPoints[index]);
                if (index == count - 1)
                    continue;

                var from = _chainPoints[index];
                var to = _chainPoints[index + 1];
                var side = math.normalizesafe(math.cross(to - from, math.up())) * _random.NextFloat(-0.3f, 0.3f);
                _bolt.SetPosition(index * 2 + 1, (from + to) * 0.5f + side);
            }

            _bolt.enabled = true;
        }

        private void UpdateEffects(float deltaTime)
        {
            if (_aura)
            {
                var visible = AuraRadius > 0 && _player;
                if (_aura.activeSelf != visible)
                    _aura.SetActive(visible);

                if (visible)
                {
                    var position = _player.transform.position;
                    position.y = 0.02f;     // 바닥 바로 위
                    _aura.transform.position = position;
                    _aura.transform.localScale = Vector3.one * (AuraRadius * 2);

                    // 틱마다 잠깐 밝아짐
                    _auraPulse = Mathf.Max(0, _auraPulse - deltaTime);
                    var auraColor = AuraColor;
                    auraColor.a = Mathf.Min(1, AuraColor.a * (1 + 2 * _auraPulse / AuraPulseDuration));
                    _auraMaterial.SetColor(BaseColorId, auraColor);
                }
            }

            if (!_bolt || !_bolt.enabled)
                return;

            _boltTimer -= deltaTime;
            if (_boltTimer <= 0)
            {
                _bolt.enabled = false;
                return;
            }

            var color = BoltColor;
            color.a = _boltTimer / BoltDuration;
            _bolt.startColor = _bolt.endColor = color;
        }

        // center에서 radius 이내인 모든 Enemy에 amount 피해
        [BurstCompile]
        internal struct AreaDamageJob : IJob
        {
            [ReadOnly] public NativeArray<float3> positions;
            [ReadOnly] public NativeParallelMultiHashMap<int2, int> grid;
            public NativeQueue<EnemyDamage>.ParallelWriter damages;
            public float inverseCellSize;
            public float2 center;
            public float radius;
            public float amount;

            public void Execute()
            {
                var visitor = new DamageVisitor { positions = positions, damages = damages, center = center, radius = radius, amount = amount };
                SpatialGrid.Query(grid, inverseCellSize, center - radius, center + radius, ref visitor);
            }
        }

        // 칼날마다 닿은 Enemy에 amount 피해. 두 칼날에 동시에 닿으면 두 번 피해
        [BurstCompile]
        internal struct BladeJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> blades;
            [ReadOnly] public NativeArray<float3> positions;
            [ReadOnly] public NativeParallelMultiHashMap<int2, int> grid;
            public NativeQueue<EnemyDamage>.ParallelWriter damages;
            public float inverseCellSize;
            public float radius;
            public float amount;

            public void Execute(int index)
            {
                var center = blades[index].xz;
                var visitor = new DamageVisitor { positions = positions, damages = damages, center = center, radius = radius, amount = amount };
                SpatialGrid.Query(grid, inverseCellSize, center - radius, center + radius, ref visitor);
            }
        }

        internal struct DamageVisitor : SpatialGrid.IVisitor
        {
            public NativeArray<float3> positions;
            public NativeQueue<EnemyDamage>.ParallelWriter damages;
            public float2 center;
            public float radius;
            public float amount;

            public bool Visit(int index)
            {
                if (math.distancesq(positions[index].xz, center) <= radius * radius)
                    damages.Enqueue(new EnemyDamage { index = index, amount = amount });

                return true;
            }
        }

        // origin에서 firstRange 안의 가장 가까운 Enemy부터, 아직 맞히지 않은 jumpRange 안의 가장 가까운 Enemy로 이어서 최대 maxTargets명
        // points에는 origin과 맞힌 순서대로의 Enemy 위치를 기록
        [BurstCompile]
        internal struct ChainJob : IJob
        {
            [ReadOnly] public NativeArray<float3> positions;
            [ReadOnly] public NativeParallelMultiHashMap<int2, int> grid;
            public NativeQueue<EnemyDamage>.ParallelWriter damages;
            public NativeList<float3> points;
            public float inverseCellSize;
            public float3 origin;
            public float firstRange;
            public float jumpRange;
            public int maxTargets;
            public float amount;

            public void Execute()
            {
                points.Clear();
                points.Add(origin);

                var hits = new FixedList128Bytes<int>();
                var current = origin.xz;
                var range = firstRange;
                var limit = math.min(maxTargets, MaxChainTargets);

                while (hits.Length < limit)
                {
                    var visitor = new ChainVisitor
                    {
                        positions = positions,
                        hits = hits,
                        center = current,
                        rangeSqr = range * range,
                        nearest = -1,
                        nearestSqr = float.MaxValue,
                    };

                    SpatialGrid.Query(grid, inverseCellSize, current - range, current + range, ref visitor);
                    if (visitor.nearest < 0)
                        break;

                    hits.Add(visitor.nearest);
                    damages.Enqueue(new EnemyDamage { index = visitor.nearest, amount = amount });
                    points.Add(positions[visitor.nearest]);
                    current = positions[visitor.nearest].xz;
                    range = jumpRange;
                }
            }
        }

        internal struct ChainVisitor : SpatialGrid.IVisitor
        {
            public NativeArray<float3> positions;
            public FixedList128Bytes<int> hits;
            public float2 center;
            public float rangeSqr;
            public int nearest;
            public float nearestSqr;

            public bool Visit(int index)
            {
                var sqrDistance = math.distancesq(positions[index].xz, center);
                if (sqrDistance > rangeSqr || sqrDistance >= nearestSqr)
                    return true;

                for (var hit = 0; hit < hits.Length; hit++)
                {
                    if (hits[hit] == index)
                        return true;
                }

                nearest = index;
                nearestSqr = sqrDistance;
                return true;
            }
        }
    }
}
