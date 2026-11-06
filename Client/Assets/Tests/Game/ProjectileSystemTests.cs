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
    public class ProjectileSystemTests
    {
        private readonly TestHelper _helper = new();
        private GameObject _player;
        private EnemyManager _enemies;
        private GameObject _projectilePrefab;
        private ProjectileSystem _projectiles;
        private PlayerStats _stats;

        [SetUp]
        public void SetUp()
        {
            _helper.CreateScene();

            _player = new GameObject("Player");
            var gameCamera = TestHelper.CreateCamera("GameCamera").gameObject.AddComponent<GameCamera>();

            var enemyPrefab = _helper.CreateTemplate(PrimitiveType.Cube, "Enemy");
            enemyPrefab.AddComponent<Enemy>();
            enemyPrefab.GetComponent<MeshRenderer>().sharedMaterial = _helper.CreateMaterial(enableInstancing: true);

            _enemies = new GameObject("EnemyManager").AddComponent<EnemyManager>();
            _enemies.Initialize(_player, gameCamera, enemyPrefab);

            _projectilePrefab = _helper.CreateTemplate(PrimitiveType.Sphere, "Projectile");
            _projectilePrefab.transform.localScale = Vector3.one * 0.2f;
            _projectilePrefab.GetComponent<MeshRenderer>().sharedMaterial = _helper.CreateMaterial(enableInstancing: true);

            _stats = new PlayerStats();
            _stats.SetBase(StatType.Damage, 100);
            _stats.SetBase(StatType.FireInterval, 0.01f);
            _stats.SetBase(StatType.ProjectileCount, 1);
            _stats.SetBase(StatType.ProjectileSpeed, 50);
            _stats.SetBase(StatType.Range, 1000);

            _projectiles = new GameObject("ProjectileSystem").AddComponent<ProjectileSystem>();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => _helper.UnloadScene();

        // Enemy를 count개 이상 만든 뒤 생성 중지
        private IEnumerator SpawnEnemies(int count)
        {
            _enemies.SpawnPerSecond = 100000;
            while (_enemies.EnemyCount < count)
                yield return null;

            _enemies.SpawnPerSecond = 0;
        }

        [UnityTest]
        public IEnumerator Update_FiresAtEnemiesAndKillsThem()
        {
            _projectiles.Initialize(_player, _enemies, _projectilePrefab, _stats);
            yield return SpawnEnemies(10);

            yield return TestHelper.WaitUntil(() => _enemies.KillCount > 0);

            Assert.Greater(_enemies.KillCount, 0);
        }

        [UnityTest]
        public IEnumerator Fire_ProjectileCount_FiresSpreadAtOnce()
        {
            _stats.SetBase(StatType.ProjectileCount, 3);
            _stats.SetBase(StatType.ProjectileSpeed, 1);
            _stats.SetBase(StatType.FireInterval, 100);
            _projectiles.Initialize(_player, _enemies, _projectilePrefab, _stats);
            yield return SpawnEnemies(1);

            while (_projectiles.ProjectileCount == 0)
                yield return null;

            Assert.AreEqual(3, _projectiles.ProjectileCount);
        }

        [UnityTest]
        public IEnumerator Fire_NoEnemyInRange_DoesNotFire()
        {
            _stats.SetBase(StatType.Range, 0.1f);
            _projectiles.Initialize(_player, _enemies, _projectilePrefab, _stats);
            yield return SpawnEnemies(10);
            yield return TestHelper.WaitFrames(3);

            Assert.AreEqual(0, _projectiles.ProjectileCount);
        }

        [UnityTest]
        public IEnumerator Fire_ZeroSpeed_DoesNotFire()
        {
            _stats.SetBase(StatType.ProjectileSpeed, 0);
            _projectiles.Initialize(_player, _enemies, _projectilePrefab, _stats);
            yield return SpawnEnemies(10);
            yield return TestHelper.WaitFrames(3);

            Assert.AreEqual(0, _projectiles.ProjectileCount);
        }

        [UnityTest]
        public IEnumerator Update_ProjectileExpires_IsRemoved()
        {
            // 사거리 / 속도 = 수명. 첫 발사 후 다시 쏘지 않도록 간격을 길게
            _stats.SetBase(StatType.Range, 30);
            _stats.SetBase(StatType.ProjectileSpeed, 1000);
            _stats.SetBase(StatType.FireInterval, 100);
            _projectiles.Initialize(_player, _enemies, _projectilePrefab, _stats);
            yield return SpawnEnemies(1);

            while (_projectiles.ProjectileCount == 0)
                yield return null;

            yield return TestHelper.WaitUntil(() => _projectiles.ProjectileCount == 0);

            Assert.AreEqual(0, _projectiles.ProjectileCount);
        }

        [UnityTest]
        public IEnumerator OnDestroy_ReleasesProjectiles()
        {
            _stats.SetBase(StatType.ProjectileSpeed, 1);
            _projectiles.Initialize(_player, _enemies, _projectilePrefab, _stats);
            yield return SpawnEnemies(1);

            while (_projectiles.ProjectileCount == 0)
                yield return null;

            Object.Destroy(_projectiles.gameObject);
            yield return null;

            Assert.AreEqual(0, _projectiles.ProjectileCount);
        }

        [UnityTest]
        public IEnumerator OnDestroy_WithoutInitialize_DoesNotThrow()
        {
            Object.Destroy(_projectiles.gameObject);
            yield return null;

            Assert.AreEqual(0, _projectiles.ProjectileCount);
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
        public IEnumerator Initialize_InvalidSetup_LogsErrorAndDoesNotFire([Values] InvalidSetup setup)
        {
            var player = _player;
            var enemies = _enemies;
            var prefab = _projectilePrefab;
            var stats = _stats;

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

            _projectiles.Initialize(player, enemies, prefab, stats);
            yield return SpawnEnemies(10);
            yield return TestHelper.WaitFrames(3);

            Assert.AreEqual(0, _projectiles.ProjectileCount);
        }

        private struct MoveResult
        {
            public float3 position;
            public float lifetime;
            public int pierce;
            public EnemyDamage[] damages;
        }

        // 원점에서 +X 방향으로 0.5초 동안 10m/s 이동하는 투사체 1개로 MoveJob 실행
        private static MoveResult RunMoveJob(float3[] enemyPositions, int pierce = 1, float age = 0.1f)
        {
            using var enemies = new NativeArray<float3>(enemyPositions, Allocator.TempJob);
            using var grid = new NativeParallelMultiHashMap<int2, int>(enemyPositions.Length, Allocator.TempJob);
            using var queue = new NativeQueue<EnemyDamage>(Allocator.TempJob);
            using var positions = new NativeArray<float3>(new[] { float3.zero }, Allocator.TempJob);
            using var velocities = new NativeArray<float3>(new[] { new float3(10, 0, 0) }, Allocator.TempJob);
            using var lifetimes = new NativeArray<float>(new[] { 1f }, Allocator.TempJob);
            using var ages = new NativeArray<float>(new[] { age }, Allocator.TempJob);
            using var pierces = new NativeArray<int>(new[] { pierce }, Allocator.TempJob);
            using var matrices = new NativeArray<Matrix4x4>(1, Allocator.TempJob);

            new SpatialGrid.BuildJob { positions = enemies, grid = grid.AsParallelWriter(), inverseCellSize = 1 }.Run(enemies.Length);

            new ProjectileSystem.MoveJob
            {
                enemyPositions = enemies,
                grid = grid,
                damages = queue.AsParallelWriter(),
                positions = positions,
                velocities = velocities,
                lifetimes = lifetimes,
                ages = ages,
                pierces = pierces,
                matrices = matrices,
                inverseCellSize = 1,
                hitRadius = 0.5f,
                damage = 3,
                rotation = quaternion.identity,
                scale = new float3(1),
                deltaTime = 0.5f,
            }.Run(1);

            using var damages = queue.ToArray(Allocator.Temp);
            return new MoveResult
            {
                position = positions[0],
                lifetime = lifetimes[0],
                pierce = pierces[0],
                damages = damages.ToArray(),
            };
        }

        [Test]
        public void MoveJob_PassesThroughEnemy_HitsNearestOnSegment()
        {
            // 한 프레임 이동 거리(5m)가 Enemy 크기보다 커도 사이에 있는 Enemy를 맞힘. 관통 1이면 가장 가까운 Enemy만
            var result = RunMoveJob(new[] { new float3(3, 0, 0), new float3(1, 0, 0.2f) });

            Assert.AreEqual(1, result.damages.Length);
            Assert.AreEqual(1, result.damages[0].index);
            Assert.AreEqual(3, result.damages[0].amount);
            Assert.AreEqual(1, result.position.x, 1e-5f);
            Assert.AreEqual(0, result.lifetime);
            Assert.AreEqual(0, result.pierce);
        }

        [Test]
        public void MoveJob_Pierce_HitsSeveralAndKeepsFlying()
        {
            var result = RunMoveJob(new[] { new float3(3, 0, 0), new float3(1, 0, 0), new float3(2, 0, 3) }, pierce: 3);

            CollectionAssert.AreEquivalent(new[] { 0, 1 }, result.damages.Select(damage => damage.index));
            Assert.AreEqual(1, result.pierce);
            Assert.AreEqual(5, result.position.x, 1e-5f);
            Assert.AreEqual(0.5f, result.lifetime, 1e-5f);
        }

        [Test]
        public void MoveJob_PierceRunsOut_StopsAtLastHit()
        {
            var result = RunMoveJob(new[] { new float3(4, 0, 0), new float3(1, 0, 0), new float3(2, 0, 0) }, pierce: 2);

            CollectionAssert.AreEquivalent(new[] { 1, 2 }, result.damages.Select(damage => damage.index));
            Assert.AreEqual(2, result.position.x, 1e-5f);
            Assert.AreEqual(0, result.lifetime);
        }

        [Test]
        public void MoveJob_EnemyAtStart_HitOnlyOnFirstFrame()
        {
            // 지난 프레임에 맞힌 Enemy(시작점이 이미 안에 있음)는 다시 맞히지 않음
            Assert.AreEqual(0, RunMoveJob(new[] { new float3(0.2f, 0, 0) }, pierce: 3).damages.Length);
            Assert.AreEqual(1, RunMoveJob(new[] { new float3(0.2f, 0, 0) }, pierce: 3, age: 0).damages.Length);
        }

        [Test]
        public void MoveJob_NoEnemyOnPath_MovesAndAges()
        {
            var result = RunMoveJob(new[] { new float3(2, 0, 1) });

            Assert.AreEqual(0, result.damages.Length);
            Assert.AreEqual(5, result.position.x, 1e-5f);
            Assert.AreEqual(0.5f, result.lifetime, 1e-5f);
        }

        [Test]
        public void FindNearestJob_ReturnsNearestWithinRange()
        {
            using var positions = new NativeArray<float3>(new[] { new float3(5, 0, 0), new float3(0, 0, -2), new float3(3, 0, 0) }, Allocator.TempJob);
            using var result = new NativeReference<int>(Allocator.TempJob);

            var job = new ProjectileSystem.FindNearestJob { positions = positions, result = result, origin = float2.zero, maxDistance = 10 };
            job.Run();
            Assert.AreEqual(1, result.Value);

            job.maxDistance = 1;
            job.Run();
            Assert.AreEqual(-1, result.Value);
        }
    }
}
