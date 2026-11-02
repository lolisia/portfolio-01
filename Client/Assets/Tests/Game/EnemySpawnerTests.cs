using System.Collections;
using System.Text.RegularExpressions;
using aisilol.Game;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace aisilol.Tests.Game
{
    public class EnemySpawnerTests
    {
        private readonly TestHelper _helper = new();
        private EnemySpawner _spawner;
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

            _spawner = new GameObject("EnemySpawner").AddComponent<EnemySpawner>();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => _helper.UnloadScene();

        [UnityTest]
        public IEnumerator Update_SpawnsEnemiesEveryFrame()
        {
            _spawner.Initialize(_player, _gameCamera, _enemyPrefab);
            Assert.AreEqual(0, _spawner.EnemyCount);

            // 첫 프레임은 Spawn 거리 계산만 수행
            yield return null;
            Assert.AreEqual(0, _spawner.EnemyCount);

            yield return TestHelper.WaitFrames(3);
            Assert.AreEqual(6, _spawner.EnemyCount);
        }

        [UnityTest]
        public IEnumerator Zoom_KeepsSpawningWithRecalculatedDistance()
        {
            _spawner.Initialize(_player, _gameCamera, _enemyPrefab);
            yield return TestHelper.WaitFrames(2);

            var count = _spawner.EnemyCount;
            _gameCamera.Zoom(-1);
            yield return null;

            Assert.AreEqual(count + 2, _spawner.EnemyCount);
        }

        [UnityTest]
        public IEnumerator Update_HorizonVisible_DoesNotSpawn()
        {
            _gameCamera.transform.rotation = Quaternion.LookRotation(Vector3.forward);
            _spawner.Initialize(_player, _gameCamera, _enemyPrefab);
            yield return TestHelper.WaitFrames(3);

            Assert.AreEqual(0, _spawner.EnemyCount);
        }

        [UnityTest]
        public IEnumerator Update_PlayerDestroyed_StopsSpawning()
        {
            _spawner.Initialize(_player, _gameCamera, _enemyPrefab);
            yield return TestHelper.WaitFrames(2);

            var count = _spawner.EnemyCount;
            Object.Destroy(_player);
            yield return TestHelper.WaitFrames(2);

            Assert.AreEqual(count, _spawner.EnemyCount);
        }

        [UnityTest]
        public IEnumerator Update_ManyEnemies_RendersInMultipleBatches()
        {
            _spawner.Initialize(_player, _gameCamera, _enemyPrefab);

            // Draw Call 1회 최대 Instance 수(1023)를 넘을 때까지 진행
            while (_spawner.EnemyCount <= 1100)
                yield return null;

            yield return null;
            Assert.Greater(_spawner.EnemyCount, 1100);
        }

        [UnityTest]
        public IEnumerator OnDestroy_ReleasesEnemies()
        {
            _spawner.Initialize(_player, _gameCamera, _enemyPrefab);
            yield return TestHelper.WaitFrames(3);
            Assert.Greater(_spawner.EnemyCount, 0);

            Object.Destroy(_spawner.gameObject);
            yield return null;

            Assert.AreEqual(0, _spawner.EnemyCount);
        }

        [UnityTest]
        public IEnumerator OnDestroy_WithoutInitialize_DoesNotThrow()
        {
            Object.Destroy(_spawner.gameObject);
            yield return null;

            Assert.AreEqual(0, _spawner.EnemyCount);
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
            var nullError = new Regex("인자가 null 입니다");

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
                    LogAssert.Expect(LogType.Error, new Regex("Camera Component가 존재하지 않습니다"));
                    break;

                case InvalidSetup.PrefabWithoutMeshFilter:
                    enemyPrefab = _helper.CreateTemplate("Empty");
                    LogAssert.Expect(LogType.Error, new Regex("MeshFilter Component가 존재하지 않습니다"));
                    break;

                case InvalidSetup.PrefabWithoutMesh:
                    enemyPrefab.GetComponent<MeshFilter>().sharedMesh = null;
                    LogAssert.Expect(LogType.Error, nullError);
                    break;

                case InvalidSetup.PrefabWithoutMeshRenderer:
                    Object.DestroyImmediate(enemyPrefab.GetComponent<MeshRenderer>());
                    LogAssert.Expect(LogType.Error, new Regex("MeshRenderer Component가 존재하지 않습니다"));
                    break;

                case InvalidSetup.PrefabWithoutMaterial:
                    enemyPrefab.GetComponent<MeshRenderer>().sharedMaterial = null;
                    LogAssert.Expect(LogType.Error, nullError);
                    break;

                case InvalidSetup.PrefabWithoutEnemy:
                    Object.DestroyImmediate(enemyPrefab.GetComponent<Enemy>());
                    LogAssert.Expect(LogType.Error, new Regex("Enemy Component가 존재하지 않습니다"));
                    break;

                case InvalidSetup.InstancingDisabled:
                    enemyPrefab.GetComponent<MeshRenderer>().sharedMaterial = _helper.CreateMaterial(enableInstancing: false);
                    LogAssert.Expect(LogType.Error, new Regex("GPU Instancing이 꺼져 있습니다"));
                    break;

                case InvalidSetup.ZeroHeight:
                    enemyPrefab.transform.localScale = new Vector3(1, 0, 1);
                    LogAssert.Expect(LogType.Error, new Regex("Enemy의 높이가 0 입니다"));
                    break;
            }

            _spawner.Initialize(player, gameCamera, enemyPrefab);
            yield return TestHelper.WaitFrames(3);

            Assert.AreEqual(0, _spawner.EnemyCount);
        }

        [Test]
        public void MoveJob_MovesTowardTargetOnGroundPlane()
        {
            using var positions = new NativeArray<float3>(new[] { new float3(10, 0.5f, 0) }, Allocator.Temp);
            using var speeds = new NativeArray<float>(new[] { 2f }, Allocator.Temp);
            using var matrices = new NativeArray<Matrix4x4>(1, Allocator.Temp);

            var job = new EnemySpawner.MoveJob
            {
                positions = positions,
                speeds = speeds,
                matrices = matrices,
                target = new float3(0, 3, 0),
                rotation = quaternion.identity,
                scale = new float3(1, 2, 1),
                deltaTime = 0.5f,
            };

            job.Execute(0);

            // 속도 2 * 0.5초 = 1만큼 이동, 높이는 유지
            Assert.AreEqual(9, positions[0].x, 1e-5f);
            Assert.AreEqual(0.5f, positions[0].y);
            Assert.AreEqual(0, positions[0].z);
            Assert.AreEqual((Vector3)positions[0], (Vector3)matrices[0].GetColumn(3));
            Assert.AreEqual(new Vector3(1, 2, 1), matrices[0].lossyScale);
        }

        [Test]
        public void MoveJob_WithinStopDistance_StaysInPlace()
        {
            using var positions = new NativeArray<float3>(new[] { new float3(0.05f, 0, 0) }, Allocator.Temp);
            using var speeds = new NativeArray<float>(new[] { 2f }, Allocator.Temp);
            using var matrices = new NativeArray<Matrix4x4>(1, Allocator.Temp);

            var job = new EnemySpawner.MoveJob
            {
                positions = positions,
                speeds = speeds,
                matrices = matrices,
                target = float3.zero,
                rotation = quaternion.identity,
                scale = new float3(1),
                deltaTime = 0.5f,
            };

            job.Execute(0);

            Assert.AreEqual(new float3(0.05f, 0, 0), positions[0]);
            Assert.AreEqual(Matrix4x4.Translate(new Vector3(0.05f, 0, 0)), matrices[0]);
        }
    }
}
