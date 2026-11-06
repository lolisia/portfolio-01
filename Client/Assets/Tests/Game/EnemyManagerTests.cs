using System.Collections;
using aisilol.Game;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace aisilol.Tests.Game
{
    public class EnemyManagerTests
    {
        private readonly TestHelper _helper = new();
        private EnemyManager _manager;
        private GameObject _player;
        private GameCamera _gameCamera;
        private GameObject _enemyPrefab;

        [SetUp]
        public void SetUp()
        {
            _helper.CreateScene();

            _player = new GameObject("Player");
            _gameCamera = TestHelper.CreateCamera("GameCamera").gameObject.AddComponent<GameCamera>();

            _enemyPrefab = _helper.CreateTemplate(PrimitiveType.Cube, "Enemy");
            _enemyPrefab.AddComponent<Enemy>();
            _enemyPrefab.GetComponent<MeshRenderer>().sharedMaterial = _helper.CreateMaterial(enableInstancing: true);

            _manager = new GameObject("EnemyManager").AddComponent<EnemyManager>();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => _helper.UnloadScene();

        [UnityTest]
        public IEnumerator Update_SpawnsBySpawnRate()
        {
            _manager.Initialize(_player, _gameCamera, _enemyPrefab);
            Assert.AreEqual(0, _manager.EnemyCount);

            // 첫 프레임은 Spawn 거리 계산만 수행
            yield return null;
            Assert.AreEqual(0, _manager.EnemyCount);

            _manager.SpawnPerSecond = 0;
            yield return TestHelper.WaitFrames(3);
            Assert.AreEqual(0, _manager.EnemyCount);

            _manager.SpawnPerSecond = 100000;
            yield return TestHelper.WaitFrames(3);
            Assert.Greater(_manager.EnemyCount, 0);
        }

        [UnityTest]
        public IEnumerator Zoom_KeepsSpawningWithRecalculatedDistance()
        {
            _manager.SpawnPerSecond = 100000;
            _manager.Initialize(_player, _gameCamera, _enemyPrefab);
            yield return TestHelper.WaitFrames(2);

            var count = _manager.EnemyCount;
            _gameCamera.Zoom(-1);
            yield return null;

            Assert.Greater(_manager.EnemyCount, count);
        }

        [UnityTest]
        public IEnumerator Update_HorizonVisible_DoesNotSpawn()
        {
            _gameCamera.transform.rotation = Quaternion.LookRotation(Vector3.forward);
            _manager.SpawnPerSecond = 100000;
            _manager.Initialize(_player, _gameCamera, _enemyPrefab);
            yield return TestHelper.WaitFrames(3);

            Assert.AreEqual(0, _manager.EnemyCount);
        }

        [UnityTest]
        public IEnumerator Update_PlayerDestroyed_StopsSpawning()
        {
            _manager.SpawnPerSecond = 100000;
            _manager.Initialize(_player, _gameCamera, _enemyPrefab);
            yield return TestHelper.WaitFrames(2);

            var count = _manager.EnemyCount;
            Object.Destroy(_player);
            yield return TestHelper.WaitFrames(2);

            Assert.AreEqual(count, _manager.EnemyCount);
        }

        [UnityTest]
        public IEnumerator Update_ManyEnemies_RendersInMultipleBatches()
        {
            _manager.SpawnPerSecond = 100000;
            _manager.Initialize(_player, _gameCamera, _enemyPrefab);

            // Draw Call 1회 최대 Instance 수(1023)를 넘을 때까지 진행
            while (_manager.EnemyCount <= 1100)
                yield return null;

            yield return null;
            Assert.Greater(_manager.EnemyCount, 1100);
        }

        [UnityTest]
        public IEnumerator QueueDamage_KillsEnemyWhenHealthRunsOut()
        {
            _manager.SpawnPerSecond = 100000;
            _manager.Initialize(_player, _gameCamera, _enemyPrefab);
            yield return TestHelper.WaitFrames(2);

            _manager.SpawnPerSecond = 0;
            yield return null;
            var count = _manager.EnemyCount;
            Assert.Greater(count, 0);

            // 기본 체력 2. 1 데미지로는 살아 있고, 2번째에 사망. 범위 밖 인덱스는 무시
            _manager.QueueDamage(0, 1);
            _manager.QueueDamage(-1, 100);
            _manager.QueueDamage(count, 100);
            yield return null;
            Assert.AreEqual(count, _manager.EnemyCount);
            Assert.AreEqual(0, _manager.KillCount);

            _manager.QueueDamage(0, 1);
            _manager.QueueDamage(0, 1);     // 이미 사망한 Enemy는 다시 처치하지 않음
            yield return null;
            Assert.AreEqual(count - 1, _manager.EnemyCount);
            Assert.AreEqual(1, _manager.KillCount);
        }

        [UnityTest]
        public IEnumerator HealthMultiplier_AppliesToSpawnedEnemies()
        {
            _manager.HealthMultiplier = 3;
            _manager.SpawnPerSecond = 100000;
            _manager.Initialize(_player, _gameCamera, _enemyPrefab);
            yield return TestHelper.WaitFrames(2);

            _manager.SpawnPerSecond = 0;
            yield return null;

            // 체력 2 * 3 = 6
            _manager.QueueDamage(0, 5.9f);
            yield return null;
            Assert.AreEqual(0, _manager.KillCount);

            _manager.QueueDamage(0, 0.1f);
            yield return null;
            Assert.AreEqual(1, _manager.KillCount);
        }

        [UnityTest]
        public IEnumerator Update_EnemyReachesPlayer_CountsContact()
        {
            _enemyPrefab.GetComponent<Enemy>().moveSpeed = new Vector2(1000, 1000);
            _manager.SpawnPerSecond = 100000;
            _manager.Initialize(_player, _gameCamera, _enemyPrefab);
            yield return TestHelper.WaitFrames(2);

            _manager.SpawnPerSecond = 0;
            yield return TestHelper.WaitUntil(() => _manager.ContactCount > 0);

            Assert.Greater(_manager.ContactCount, 0);
            Assert.AreEqual(10, _manager.ContactDamage);
            Assert.AreEqual(1, _manager.ExperiencePerKill);
        }

        [UnityTest]
        public IEnumerator OnDestroy_ReleasesEnemies()
        {
            _manager.SpawnPerSecond = 100000;
            _manager.Initialize(_player, _gameCamera, _enemyPrefab);
            yield return TestHelper.WaitFrames(3);
            Assert.Greater(_manager.EnemyCount, 0);

            Object.Destroy(_manager.gameObject);
            yield return null;

            Assert.AreEqual(0, _manager.EnemyCount);
        }

        [UnityTest]
        public IEnumerator OnDestroy_WithoutInitialize_DoesNotThrow()
        {
            Object.Destroy(_manager.gameObject);
            yield return null;

            Assert.AreEqual(0, _manager.EnemyCount);
        }

        public enum InvalidSetup
        {
            NullPlayer,
            NullCamera,
            NullPrefab,
            CameraWithoutCamera,
            PrefabWithoutMeshFilter,
            PrefabWithoutMesh,
            PrefabWithoutMeshRenderer,
            PrefabWithoutMaterial,
            PrefabWithoutEnemy,
            InstancingDisabled,
            ZeroHeight,
        }

        [UnityTest]
        public IEnumerator Initialize_InvalidSetup_LogsErrorAndSpawnsNothing(
            [Values] InvalidSetup setup)
        {
            var player = _player;
            var gameCamera = _gameCamera;
            var enemyPrefab = _enemyPrefab;
            var nullError = TestHelper.Pattern("인자가 null 입니다");

            switch (setup)
            {
                case InvalidSetup.NullPlayer:
                    player = null;
                    LogAssert.Expect(LogType.Error, nullError);
                    break;

                case InvalidSetup.NullCamera:
                    gameCamera = null;
                    LogAssert.Expect(LogType.Error, nullError);
                    break;

                case InvalidSetup.NullPrefab:
                    enemyPrefab = null;
                    LogAssert.Expect(LogType.Error, nullError);
                    break;

                case InvalidSetup.CameraWithoutCamera:
                    gameCamera = new GameObject("NoCamera").AddComponent<GameCamera>();
                    LogAssert.Expect(LogType.Error, TestHelper.Pattern("Camera Component가 존재하지 않습니다"));
                    break;

                case InvalidSetup.PrefabWithoutMeshFilter:
                    enemyPrefab = _helper.CreateTemplate("Empty");
                    LogAssert.Expect(LogType.Error, TestHelper.Pattern("MeshFilter Component가 존재하지 않습니다"));
                    break;

                case InvalidSetup.PrefabWithoutMesh:
                    enemyPrefab.GetComponent<MeshFilter>().sharedMesh = null;
                    LogAssert.Expect(LogType.Error, nullError);
                    break;

                case InvalidSetup.PrefabWithoutMeshRenderer:
                    Object.DestroyImmediate(enemyPrefab.GetComponent<MeshRenderer>());
                    LogAssert.Expect(LogType.Error, TestHelper.Pattern("MeshRenderer Component가 존재하지 않습니다"));
                    break;

                case InvalidSetup.PrefabWithoutMaterial:
                    enemyPrefab.GetComponent<MeshRenderer>().sharedMaterial = null;
                    LogAssert.Expect(LogType.Error, nullError);
                    break;

                case InvalidSetup.PrefabWithoutEnemy:
                    Object.DestroyImmediate(enemyPrefab.GetComponent<Enemy>());
                    LogAssert.Expect(LogType.Error, TestHelper.Pattern("Enemy Component가 존재하지 않습니다"));
                    break;

                case InvalidSetup.InstancingDisabled:
                    enemyPrefab.GetComponent<MeshRenderer>().sharedMaterial = _helper.CreateMaterial(enableInstancing: false);
                    LogAssert.Expect(LogType.Error, TestHelper.Pattern("GPU Instancing이 꺼져 있습니다"));
                    break;

                case InvalidSetup.ZeroHeight:
                    enemyPrefab.transform.localScale = new Vector3(1, 0, 1);
                    LogAssert.Expect(LogType.Error, TestHelper.Pattern("Enemy의 높이가 0 입니다"));
                    break;
            }

            _manager.SpawnPerSecond = 100000;
            _manager.Initialize(player, gameCamera, enemyPrefab);
            yield return TestHelper.WaitFrames(3);

            Assert.AreEqual(0, _manager.EnemyCount);
        }

        // 격자를 구성하고 MoveJob을 index 0에 대해 실행
        private static void RunMoveJob(float3[] positions, float speed, float3 target, float stopDistance, out float3 result, out Matrix4x4 matrix)
        {
            using var positionArray = new NativeArray<float3>(positions, Allocator.TempJob);
            using var speeds = new NativeArray<float>(Enumerable.Repeat(speed, positions.Length).ToArray(), Allocator.TempJob);
            using var nextPositions = new NativeArray<float3>(positions.Length, Allocator.TempJob);
            using var matrices = new NativeArray<Matrix4x4>(positions.Length, Allocator.TempJob);
            using var grid = new NativeParallelMultiHashMap<int2, int>(positions.Length, Allocator.TempJob);

            const float cellSize = 1;
            new SpatialGrid.BuildJob
            {
                positions = positionArray,
                grid = grid.AsParallelWriter(),
                inverseCellSize = 1 / cellSize,
            }.Run(positions.Length);

            var job = new EnemyManager.MoveJob
            {
                positions = positionArray,
                speeds = speeds,
                grid = grid,
                nextPositions = nextPositions,
                matrices = matrices,
                inverseCellSize = 1 / cellSize,
                separationRadius = cellSize,
                target = target,
                stopDistance = stopDistance,
                rotation = quaternion.identity,
                scale = new float3(1, 2, 1),
                deltaTime = 0.5f,
            };

            job.Execute(0);
            result = nextPositions[0];
            matrix = matrices[0];
        }

        [Test]
        public void MoveJob_MovesTowardTargetOnGroundPlane()
        {
            RunMoveJob(new[] { new float3(10, 0.5f, 0) }, 2, new float3(0, 3, 0), 0.1f, out var position, out var matrix);

            // 속도 2 * 0.5초 = 1만큼 이동, 높이는 유지
            Assert.AreEqual(9, position.x, 1e-5f);
            Assert.AreEqual(0.5f, position.y);
            Assert.AreEqual(0, position.z);
            Assert.AreEqual((Vector3)position, (Vector3)matrix.GetColumn(3));
            Assert.AreEqual(new Vector3(1, 2, 1), matrix.lossyScale);
        }

        [Test]
        public void MoveJob_WithinStopDistance_StaysInPlace()
        {
            RunMoveJob(new[] { new float3(0.05f, 0, 0) }, 2, float3.zero, 0.1f, out var position, out _);

            Assert.AreEqual(new float3(0.05f, 0, 0), position);
        }

        [Test]
        public void MoveJob_OverlappingNeighbor_PushesAway()
        {
            // 이웃이 오른쪽 0.5m(분리 반경 1m의 절반)에 있으면 왼쪽으로 0.5 * 속도 1 * 0.5초 = 0.25 이동
            RunMoveJob(new[] { new float3(0, 0, 0), new float3(0.5f, 0, 0) }, 0, float3.zero, 0.1f, out var position, out _);

            Assert.AreEqual(-0.25f, position.x, 1e-5f);
            Assert.AreEqual(0, position.z, 1e-5f);
        }

        [Test]
        public void MoveJob_SamePosition_SeparatesByIndexOrder()
        {
            RunMoveJob(new[] { new float3(0, 0, 0), new float3(0, 0, 0) }, 0, float3.zero, 0.1f, out var position, out _);

            Assert.Less(position.x, 0);
        }

        [Test]
        public void ContactJob_CountsEnemiesWithinRadius()
        {
            using var positions = new NativeArray<float3>(new[] { new float3(0.1f, 0, 0), new float3(0, 0, -0.3f), new float3(2, 0, 0) }, Allocator.TempJob);
            using var grid = new NativeParallelMultiHashMap<int2, int>(3, Allocator.TempJob);
            using var result = new NativeReference<int>(Allocator.TempJob);

            new SpatialGrid.BuildJob { positions = positions, grid = grid.AsParallelWriter(), inverseCellSize = 4 }.Run(positions.Length);
            new EnemyManager.ContactJob
            {
                positions = positions,
                grid = grid,
                result = result,
                inverseCellSize = 4,
                center = float2.zero,
                radius = 0.3f,
            }.Run();

            Assert.AreEqual(2, result.Value);
        }
    }
}
