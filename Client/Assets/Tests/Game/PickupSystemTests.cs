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
    public class PickupSystemTests
    {
        private readonly TestHelper _helper = new();
        private GameObject _player;
        private Enemy _enemy;
        private EnemyManager _enemies;
        private GameCamera _gameCamera;
        private GameObject _enemyPrefab;
        private GameObject _experiencePrefab;
        private GameObject _potionPrefab;
        private PickupSystem _pickups;
        private PlayerStats _stats;

        [SetUp]
        public void SetUp()
        {
            _helper.CreateScene();

            _player = new GameObject("Player");
            _gameCamera = TestHelper.CreateCamera("GameCamera").gameObject.AddComponent<GameCamera>();

            var enemyPrefab = _helper.CreateTemplate(PrimitiveType.Cube, "Enemy");
            enemyPrefab.transform.localScale = Vector3.one * 0.1f;     // 실제 Enemy와 같은 크기. 좁은 영역에 많이 모일 수 있음
            _enemy = enemyPrefab.AddComponent<Enemy>();
            enemyPrefab.GetComponent<MeshRenderer>().sharedMaterial = _helper.CreateMaterial(enableInstancing: true);

            _enemyPrefab = enemyPrefab;
            _enemies = new GameObject("EnemyManager").AddComponent<EnemyManager>();
            _enemies.MinSpawnFps = 0;
            _enemies.SpawnPerSecond = 0;

            _experiencePrefab = _helper.CreateTemplate(PrimitiveType.Sphere, "ExperienceOrb");
            _experiencePrefab.GetComponent<MeshRenderer>().sharedMaterial = _helper.CreateMaterial(enableInstancing: true);
            _potionPrefab = _helper.CreateTemplate(PrimitiveType.Capsule, "Potion");
            _potionPrefab.GetComponent<MeshRenderer>().sharedMaterial = _helper.CreateMaterial(enableInstancing: true);

            _stats = new PlayerStats();
            _pickups = new GameObject("PickupSystem").AddComponent<PickupSystem>();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => _helper.UnloadScene();

        // EnemyManager는 Initialize 시점의 Enemy Prefab 설정을 읽으므로, 테스트에서 설정을 바꾼 뒤 호출
        private void Initialize()
        {
            _enemies.Initialize(_player, _gameCamera, _enemyPrefab);
            _pickups.Initialize(_player, _enemies, _experiencePrefab, _potionPrefab, 25, _stats);
        }

        // Enemy를 count개 만들어 모두 처치. 사망한 위치에 드롭이 생성됨
        private IEnumerator KillEnemies(int count)
        {
            _enemies.SpawnPerSecond = 100000;
            while (_enemies.EnemyCount < count)
                yield return null;

            _enemies.SpawnPerSecond = 0;
            for (var index = 0; index < _enemies.EnemyCount; index++)
                _enemies.QueueDamage(index, 1000);

            yield return null;
        }

        [UnityTest]
        public IEnumerator EnemyDeath_DropsExperienceOutOfMagnetRange()
        {
            _stats.SetBase(StatType.Magnet, 0.1f);
            Initialize();
            yield return KillEnemies(5);
            yield return TestHelper.WaitFrames(3);

            // 화면 밖에서 처치되어 자석 범위 밖이므로 그대로 남음
            Assert.Greater(_pickups.ExperienceObjectCount, 0);
            Assert.AreEqual(0, _pickups.PotionCount);
            Assert.AreEqual(0, _pickups.TakeExperience());
        }

        [UnityTest]
        public IEnumerator Magnet_PullsAndCollectsDrops()
        {
            _stats.SetBase(StatType.Magnet, 10000);
            Initialize();
            _pickups.PotionDropChance = 1;
            yield return KillEnemies(5);

            var kills = _enemies.KillCount;
            var experience = 0;
            var healing = 0f;
            yield return TestHelper.WaitUntil(() =>
            {
                experience += _pickups.TakeExperience();
                healing += _pickups.TakeHealing();
                return _pickups.ExperienceObjectCount == 0 && _pickups.PotionCount == 0;
            });

            // 경험치 1 * 처치 수, 포션 회복량 25 * 처치 수
            Assert.AreEqual(kills, experience);
            Assert.AreEqual(25 * kills, healing, 1e-3f);
        }

        [UnityTest]
        public IEnumerator Merge_TenNearbyBecomeOne()
        {
            // 자석 범위는 획득 거리(0.1m)뿐. Enemy가 플레이어 주변에 모인 뒤 처치하면 드롭이 좁은 영역에 쌓임
            _enemy.moveSpeed = new Vector2(1000, 1000);
            Initialize();

            _enemies.SpawnPerSecond = 100000;
            while (_enemies.EnemyCount < 200)
                yield return null;

            _enemies.SpawnPerSecond = 0;
            yield return TestHelper.WaitUntil(() => false, 1);

            for (var index = 0; index < _enemies.EnemyCount; index++)
                _enemies.QueueDamage(index, 1000);

            yield return TestHelper.WaitUntil(() => _enemies.KillCount > 0 && _pickups.ExperienceObjectCount < _enemies.KillCount, 2);

            Assert.Less(_pickups.ExperienceObjectCount, _enemies.KillCount);
        }

        [UnityTest]
        public IEnumerator OnDestroy_WithoutInitialize_DoesNotThrow()
        {
            Object.Destroy(_pickups.gameObject);
            yield return null;

            Assert.AreEqual(0, _pickups.ExperienceObjectCount);
        }

        public enum InvalidSetup
        {
            NullPlayer,
            NullEnemies,
            NullExperiencePrefab,
            NullPotionPrefab,
            NullStats,
            InstancingDisabled,
        }

        [UnityTest]
        public IEnumerator Initialize_InvalidSetup_LogsErrorAndDropsNothing([Values] InvalidSetup setup)
        {
            var player = _player;
            var enemies = _enemies;
            var experiencePrefab = _experiencePrefab;
            var potionPrefab = _potionPrefab;
            var stats = _stats;

            switch (setup)
            {
                case InvalidSetup.NullPlayer: player = null; break;
                case InvalidSetup.NullEnemies: enemies = null; break;
                case InvalidSetup.NullExperiencePrefab: experiencePrefab = null; break;
                case InvalidSetup.NullPotionPrefab: potionPrefab = null; break;
                case InvalidSetup.NullStats: stats = null; break;
                case InvalidSetup.InstancingDisabled:
                    potionPrefab.GetComponent<MeshRenderer>().sharedMaterial = _helper.CreateMaterial(enableInstancing: false);
                    break;
            }

            LogAssert.Expect(LogType.Error, TestHelper.Pattern(setup == InvalidSetup.InstancingDisabled ? "GPU Instancing이 꺼져 있습니다" : "인자가 null 입니다"));

            _enemies.Initialize(_player, _gameCamera, _enemyPrefab);
            _pickups.Initialize(player, enemies, experiencePrefab, potionPrefab, 25, stats);
            yield return KillEnemies(5);
            yield return TestHelper.WaitFrames(2);

            Assert.AreEqual(0, _pickups.ExperienceObjectCount);
        }

        [Test]
        public void MoveJob_InsideMagnet_PullsTowardPlayerAndCollects()
        {
            using var positions = new NativeArray<float3>(new[] { new float3(1, 0, 0), new float3(0.05f, 0, 0), new float3(5, 0, 0) }, Allocator.TempJob);
            using var tiers = new NativeArray<int>(3, Allocator.TempJob);
            using var attracted = new NativeArray<byte>(3, Allocator.TempJob);
            using var collected = new NativeArray<byte>(3, Allocator.TempJob);
            using var matrices = new NativeArray<Matrix4x4>(3, Allocator.TempJob);

            new PickupSystem.MoveJob
            {
                positions = positions,
                tiers = tiers,
                attracted = attracted,
                collected = collected,
                matrices = matrices,
                player = float2.zero,
                magnetRange = 2,
                collectRadius = 0.1f,
                deltaTime = 0.1f,
                rotation = quaternion.identity,
                scale = new float3(1),
            }.Run(3);

            // 1m: 자석 범위 안. (3 + 1 * 4) * 0.1 = 0.7 이동
            Assert.AreEqual(1, attracted[0]);
            Assert.AreEqual(0.3f, positions[0].x, 1e-5f);
            Assert.AreEqual(0, collected[0]);

            // 0.05m: 획득 범위 안
            Assert.AreEqual(1, collected[1]);

            // 5m: 자석 범위 밖이라 그대로
            Assert.AreEqual(0, attracted[2]);
            Assert.AreEqual(5, positions[2].x);
        }

        [Test]
        public void MoveJob_AlreadyAttracted_KeepsPullingOutsideMagnet()
        {
            using var positions = new NativeArray<float3>(new[] { new float3(5, 0, 0) }, Allocator.TempJob);
            using var tiers = new NativeArray<int>(1, Allocator.TempJob);
            using var attracted = new NativeArray<byte>(new byte[] { 1 }, Allocator.TempJob);
            using var collected = new NativeArray<byte>(1, Allocator.TempJob);
            using var matrices = new NativeArray<Matrix4x4>(1, Allocator.TempJob);

            new PickupSystem.MoveJob
            {
                positions = positions,
                tiers = tiers,
                attracted = attracted,
                collected = collected,
                matrices = matrices,
                player = float2.zero,
                magnetRange = 1,
                collectRadius = 0.1f,
                deltaTime = 1,
                rotation = quaternion.identity,
                scale = new float3(1),
            }.Run(1);

            // 남은 거리보다 많이 움직이지 않고 플레이어 위치에서 획득
            Assert.AreEqual(0, positions[0].x, 1e-5f);
            Assert.AreEqual(1, collected[0]);
        }

        // float 값은 허용 오차 안이면 같은 값으로 판정
        private static bool Near(float value, float expected) => System.Math.Abs(value - expected) < 1e-4f;

        private static int CountNear(float[] values, float expected) => values.Count(value => Near(value, expected));

        private static int IndexNear(float[] values, float expected) => System.Array.FindIndex(values, value => Near(value, expected));

        private struct MergeResult
        {
            public float[] values;
            public int[] tiers;
            public float3[] positions;
        }

        // 셀 크기 1m, 단계마다 3배
        private static MergeResult RunMergeJob(float3[] positions, int[] tiers, byte[] attracted = null, float[] values = null)
        {
            using var positionArray = new NativeArray<float3>(positions, Allocator.TempJob);
            using var valueArray = new NativeArray<float>(values ?? Enumerable.Repeat(1f, positions.Length).ToArray(), Allocator.TempJob);
            using var tierArray = new NativeArray<int>(tiers, Allocator.TempJob);
            using var attractedArray = new NativeArray<byte>(attracted ?? new byte[positions.Length], Allocator.TempJob);
            using var matrices = new NativeArray<Matrix4x4>(positions.Length, Allocator.TempJob);

            new PickupSystem.MergeJob
            {
                positions = positionArray,
                values = valueArray,
                tiers = tierArray,
                attracted = attractedArray,
                matrices = matrices,
                cellSize = 1,
                cellGrowth = 3,
                rotation = quaternion.identity,
                scale = new float3(1),
            }.Run();

            return new MergeResult { values = valueArray.ToArray(), tiers = tierArray.ToArray(), positions = positionArray.ToArray() };
        }

        [Test]
        public void MergeJob_TenInSameCell_MergeIntoOneWithTenTimesValue()
        {
            // 같은 셀 12개 → 10개 병합 + 2개 남음
            var positions = Enumerable.Range(0, 12).Select(index => new float3(0.05f * index, 0, 0.5f)).ToArray();
            var result = RunMergeJob(positions, new int[12]);

            Assert.AreEqual(1, CountNear(result.values, 10));
            Assert.AreEqual(2, CountNear(result.values, 1));
            Assert.AreEqual(9, CountNear(result.values, 0));

            var merged = IndexNear(result.values, 10);
            Assert.AreEqual(1, result.tiers[merged]);
            Assert.AreEqual(0.225f, result.positions[merged].x, 1e-4f);     // 앞의 10개 중심
        }

        [Test]
        public void MergeJob_HigherTier_MergesOverWiderRange()
        {
            // 1단계(가치 10) 10개가 2.5m 범위에 흩어져 있음. 0단계 범위(1m)로는 셀이 다르지만 1단계 범위(3m) 안이라 병합
            var positions = Enumerable.Range(0, 10).Select(index => new float3(0.25f * index + 0.1f, 0, 1)).ToArray();
            var tiers = Enumerable.Repeat(1, 10).ToArray();
            var values = Enumerable.Repeat(10f, 10).ToArray();

            var result = RunMergeJob(positions, tiers, values: values);

            Assert.AreEqual(1, CountNear(result.values, 100));
            Assert.AreEqual(9, CountNear(result.values, 0));
            Assert.AreEqual(2, result.tiers[IndexNear(result.values, 100)]);
        }

        [Test]
        public void CalcMatrix_HigherTier_IsLargerUpToMaxVisualTier()
        {
            float ScaleOf(int tier) => ((Matrix4x4)PickupSystem.CalcMatrix(float3.zero, tier, quaternion.identity, new float3(1))).lossyScale.x;

            Assert.Greater(ScaleOf(1), ScaleOf(0));
            Assert.Greater(ScaleOf(PickupSystem.MaxVisualTier), ScaleOf(PickupSystem.MaxVisualTier - 1));
            Assert.AreEqual(ScaleOf(PickupSystem.MaxVisualTier), ScaleOf(PickupSystem.MaxVisualTier + 3), 1e-5f);
        }

        [Test]
        public void MergeJob_DifferentCellTierOrAttracted_DoesNotMerge()
        {
            // 셀 0에 5개 + 셀 1에 5개, 같은 셀이지만 단계가 다른 10개, 끌려오는 중인 10개
            var positions = Enumerable.Range(0, 10).Select(index => new float3(index < 5 ? 0.5f : 1.5f, 0, 0.5f))
                .Concat(Enumerable.Repeat(new float3(0.5f, 0, 2.5f), 10))
                .Concat(Enumerable.Repeat(new float3(0.5f, 0, 4.5f), 10))
                .ToArray();
            var tiers = new int[30];
            for (var index = 10; index < 20; index += 2)
                tiers[index] = 1;

            var attracted = new byte[30];
            for (var index = 20; index < 30; index++)
                attracted[index] = 1;

            var result = RunMergeJob(positions, tiers, attracted);

            Assert.AreEqual(result.values.Length, CountNear(result.values, 1));
        }
    }
}
