using System.Collections;
using System.Linq;
using aisilol.Game;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace aisilol.Tests.Game
{
    public class WeaponSystemTests
    {
        private readonly TestHelper _helper = new();
        private GameObject _player;
        private GameCamera _gameCamera;
        private GameObject _enemyPrefab;
        private EnemyManager _enemies;
        private GameObject _bladePrefab;
        private WeaponSystem _weapons;
        private PlayerStats _stats;

        [SetUp]
        public void SetUp()
        {
            _helper.CreateScene();

            _player = new GameObject("Player");
            _gameCamera = TestHelper.CreateCamera("GameCamera").gameObject.AddComponent<GameCamera>();

            _enemyPrefab = _helper.CreateTemplate(PrimitiveType.Cube, "Enemy");
            _enemyPrefab.AddComponent<Enemy>();
            _enemyPrefab.GetComponent<MeshRenderer>().sharedMaterial = _helper.CreateMaterial(enableInstancing: true);

            _enemies = new GameObject("EnemyManager").AddComponent<EnemyManager>();
            _enemies.MinSpawnFps = 0;
            _enemies.SpawnPerSecond = 0;

            _bladePrefab = _helper.CreateTemplate(PrimitiveType.Sphere, "Blade");
            _bladePrefab.transform.localScale = Vector3.one * 0.2f;
            _bladePrefab.GetComponent<MeshRenderer>().sharedMaterial = _helper.CreateMaterial(enableInstancing: true);

            _stats = new PlayerStats();
            _stats.SetBase(StatType.Damage, 100);

            _weapons = new GameObject("WeaponSystem").AddComponent<WeaponSystem>();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => _helper.UnloadScene();

        // 기본은 생성 직후 플레이어 주변으로 모이도록 빠르게 이동. EnemyManager는 Initialize 시점의 Prefab 설정을 읽음
        private void Initialize(float enemySpeed = 1000)
        {
            _enemyPrefab.GetComponent<Enemy>().moveSpeed = new Vector2(enemySpeed, enemySpeed);
            _enemies.Initialize(_player, _gameCamera, _enemyPrefab);
            _weapons.Initialize(_player, _enemies, _bladePrefab, _stats);
        }

        // Enemy를 count개 이상 만든 뒤 생성을 멈추고 플레이어 주변에 모일 때까지 대기
        private IEnumerator GatherEnemies(int count)
        {
            _enemies.SpawnPerSecond = 100000;
            while (_enemies.EnemyCount < count)
                yield return null;

            _enemies.SpawnPerSecond = 0;
            yield return TestHelper.WaitUntil(() => false, 0.3);
        }

        [UnityTest]
        public IEnumerator NoWeaponStats_DealsNoDamage()
        {
            Initialize();
            yield return GatherEnemies(50);

            Assert.AreEqual(0, _enemies.KillCount);
            Assert.AreEqual(0, _weapons.BladeCount);
            Assert.AreEqual(0, _weapons.ChainStrikes);
            Assert.IsFalse(_weapons.IsAuraVisible);
            Assert.IsFalse(_weapons.IsBoltVisible);
        }

        [UnityTest]
        public IEnumerator Aura_KillsEnemiesAroundPlayerAndShowsRing()
        {
            _stats.SetBase(StatType.AuraRadius, 3);
            Initialize();
            yield return GatherEnemies(50);
            yield return TestHelper.WaitUntil(() => _enemies.KillCount > 0);

            Assert.Greater(_enemies.KillCount, 0);
            Assert.AreEqual(3, _weapons.AuraRadius, 1e-5f);
            Assert.IsTrue(_weapons.IsAuraVisible);

            // 반경이 0이 되면 숨김
            _stats.SetBase(StatType.AuraRadius, 0);
            yield return TestHelper.WaitFrames(2);
            Assert.IsFalse(_weapons.IsAuraVisible);
        }

        [UnityTest]
        public IEnumerator Orbit_BladesCircleAndKillEnemies()
        {
            _stats.SetBase(StatType.OrbitCount, 4);
            Initialize(enemySpeed: 10);
            yield return null;
            Assert.AreEqual(4, _weapons.BladeCount);

            yield return GatherEnemies(200);
            yield return TestHelper.WaitUntil(() => _enemies.KillCount > 0);
            Assert.Greater(_enemies.KillCount, 0);

            _stats.SetBase(StatType.OrbitCount, 100);
            yield return null;
            Assert.AreEqual(WeaponSystem.MaxBlades, _weapons.BladeCount);
        }

        [UnityTest]
        public IEnumerator Chain_StrikesUpToChainCountAndShowsBolt()
        {
            // 모인 뒤에 첫 번개가 나가도록 능력치는 나중에 설정
            Initialize();
            yield return GatherEnemies(100);

            _stats.SetBase(StatType.ChainCount, 5);
            yield return TestHelper.WaitUntil(() => _weapons.ChainStrikes > 0);

            Assert.AreEqual(1, _weapons.ChainStrikes);
            Assert.AreEqual(5, _weapons.LastChainHits);
            Assert.IsTrue(_weapons.IsBoltVisible);

            // 잠시 보인 뒤 사라짐
            yield return TestHelper.WaitUntil(() => !_weapons.IsBoltVisible);
            Assert.IsFalse(_weapons.IsBoltVisible);
        }

        [UnityTest]
        public IEnumerator Chain_NoEnemyInRange_DoesNotStrike()
        {
            _stats.SetBase(StatType.ChainCount, 3);
            Initialize();
            yield return TestHelper.WaitUntil(() => false, 0.3);

            Assert.AreEqual(0, _weapons.ChainStrikes);
            Assert.AreEqual(0, _weapons.LastChainHits);
            Assert.IsFalse(_weapons.IsBoltVisible);
        }

        [UnityTest]
        public IEnumerator OnDestroy_RemovesEffects()
        {
            _stats.SetBase(StatType.AuraRadius, 1);
            _stats.SetBase(StatType.OrbitCount, 2);
            Initialize();
            yield return TestHelper.WaitFrames(2);
            Assert.IsNotNull(GameObject.Find("Aura"));

            Object.Destroy(_weapons.gameObject);
            yield return null;

            Assert.IsNull(GameObject.Find("Aura"));
            Assert.IsNull(GameObject.Find("ChainBolt"));
        }

        [UnityTest]
        public IEnumerator OnDestroy_WithoutInitialize_DoesNotThrow()
        {
            Object.Destroy(_weapons.gameObject);
            yield return null;

            Assert.AreEqual(0, _weapons.BladeCount);
        }

        public enum InvalidSetup
        {
            NullPlayer,
            NullEnemies,
            NullPrefab,
            NullStats,
            InstancingDisabled,
        }

        [UnityTest]
        public IEnumerator Initialize_InvalidSetup_LogsErrorAndDoesNothing([Values] InvalidSetup setup)
        {
            var player = _player;
            var enemies = _enemies;
            var prefab = _bladePrefab;
            var stats = _stats;
            _stats.SetBase(StatType.OrbitCount, 4);

            switch (setup)
            {
                case InvalidSetup.NullPlayer: player = null; break;
                case InvalidSetup.NullEnemies: enemies = null; break;
                case InvalidSetup.NullPrefab: prefab = null; break;
                case InvalidSetup.NullStats: stats = null; break;
                case InvalidSetup.InstancingDisabled:
                    prefab.GetComponent<MeshRenderer>().sharedMaterial = _helper.CreateMaterial(enableInstancing: false);
                    break;
            }

            LogAssert.Expect(LogType.Error, TestHelper.Pattern(setup == InvalidSetup.InstancingDisabled ? "GPU Instancing이 꺼져 있습니다" : "인자가 null 입니다"));

            _enemies.Initialize(_player, _gameCamera, _enemyPrefab);
            _weapons.Initialize(player, enemies, prefab, stats);
            yield return TestHelper.WaitFrames(3);

            Assert.AreEqual(0, _weapons.BladeCount);
        }

        // 셀 크기 1m 격자. 결과는 데미지 큐 내용
        private static EnemyDamage[] RunWithGrid(float3[] enemyPositions, System.Func<NativeArray<float3>, NativeParallelMultiHashMap<int2, int>, NativeQueue<EnemyDamage>, JobHandle> schedule)
        {
            using var positions = new NativeArray<float3>(enemyPositions, Allocator.TempJob);
            using var grid = new NativeParallelMultiHashMap<int2, int>(enemyPositions.Length, Allocator.TempJob);
            using var queue = new NativeQueue<EnemyDamage>(Allocator.TempJob);

            new SpatialGrid.BuildJob { positions = positions, grid = grid.AsParallelWriter(), inverseCellSize = 1 }.Run(positions.Length);
            schedule(positions, grid, queue).Complete();

            using var damages = queue.ToArray(Allocator.Temp);
            return damages.ToArray();
        }

        [Test]
        public void AreaDamageJob_DamagesOnlyInsideRadius()
        {
            var damages = RunWithGrid(new[] { new float3(0.5f, 0, 0), new float3(0, 5, 1.5f), new float3(3, 0, 0) }, (positions, grid, queue) => new WeaponSystem.AreaDamageJob
            {
                positions = positions,
                grid = grid,
                damages = queue.AsParallelWriter(),
                inverseCellSize = 1,
                center = float2.zero,
                radius = 2,
                amount = 7,
            }.Schedule());

            CollectionAssert.AreEquivalent(new[] { 0, 1 }, damages.Select(damage => damage.index));
            Assert.IsTrue(damages.All(damage => System.Math.Abs(damage.amount - 7) < 1e-5f));
        }

        [Test]
        public void BladeJob_DamagesEnemiesTouchingEachBlade()
        {
            using var blades = new NativeArray<float3>(new[] { float3.zero, new float3(5, 0, 0) }, Allocator.TempJob);
            var bladeArray = blades;

            var damages = RunWithGrid(new[] { new float3(0.5f, 0, 0), new float3(5, 0, 0.5f), new float3(9, 0, 9) }, (positions, grid, queue) => new WeaponSystem.BladeJob
            {
                blades = bladeArray,
                positions = positions,
                grid = grid,
                damages = queue.AsParallelWriter(),
                inverseCellSize = 1,
                radius = 1,
                amount = 2,
            }.Schedule(bladeArray.Length, 1));

            CollectionAssert.AreEquivalent(new[] { 0, 1 }, damages.Select(damage => damage.index));
        }

        private static (EnemyDamage[] damages, float3[] points) RunChain(float3[] enemyPositions, int maxTargets)
        {
            using var points = new NativeList<float3>(Allocator.TempJob);
            var pointList = points;

            var damages = RunWithGrid(enemyPositions, (positions, grid, queue) => new WeaponSystem.ChainJob
            {
                positions = positions,
                grid = grid,
                damages = queue.AsParallelWriter(),
                points = pointList,
                inverseCellSize = 1,
                origin = new float3(0, 1, 0),
                firstRange = 1.5f,
                jumpRange = 1.5f,
                maxTargets = maxTargets,
                amount = 4,
            }.Schedule());

            return (damages, points.AsArray().ToArray());
        }

        [Test]
        public void ChainJob_JumpsToNearestUnhitEnemyWithinRange()
        {
            // 0 → 1.2 → 2 → 3 순서. 10m 떨어진 Enemy는 범위 밖이라 멈춤. 맞힌 Enemy로 되돌아가지 않음
            var enemyPositions = new[] { new float3(1, 0, 0), new float3(2, 0, 0), new float3(3, 0, 0), new float3(10, 0, 0), new float3(1.2f, 0, 0) };
            var (damages, points) = RunChain(enemyPositions, 10);

            CollectionAssert.AreEqual(new[] { 0, 4, 1, 2 }, damages.Select(damage => damage.index));
            Assert.AreEqual(5, points.Length);
            Assert.AreEqual(new float3(0, 1, 0), points[0]);
            Assert.AreEqual(enemyPositions[2], points[4]);
        }

        [Test]
        public void ChainJob_StopsAtMaxTargets()
        {
            var (damages, points) = RunChain(new[] { new float3(1, 0, 0), new float3(2, 0, 0), new float3(3, 0, 0) }, 2);

            Assert.AreEqual(2, damages.Length);
            Assert.AreEqual(3, points.Length);
        }

        [Test]
        public void ChainJob_NoEnemyInFirstRange_OnlyOrigin()
        {
            var (damages, points) = RunChain(new[] { new float3(5, 0, 0) }, 3);

            Assert.AreEqual(0, damages.Length);
            Assert.AreEqual(1, points.Length);
        }
    }
}
