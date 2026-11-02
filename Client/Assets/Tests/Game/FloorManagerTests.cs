using System.Collections;
using System.Linq;
using System.Text.RegularExpressions;
using aisilol.Game;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace aisilol.Tests.Game
{
    public class FloorManagerTests
    {
        private const float TileSize = 10;  // Plane Primitive의 크기

        private readonly TestHelper _helper = new();
        private FloorManager _floorManager;
        private GameObject _floorPrefab;
        private GameObject _player;
        private Camera _camera;

        [SetUp]
        public void SetUp()
        {
            _helper.CreateScene();

            _floorPrefab = _helper.CreateTemplate(PrimitiveType.Plane, "Floor");
            _player = new GameObject("Player");
            _camera = TestHelper.CreateCamera();
            _floorManager = new GameObject("FloorManager").AddComponent<FloorManager>();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => _helper.UnloadScene();

        private Transform[] ActiveTiles =>
            _floorManager.transform.Cast<Transform>().Where(tile => tile.gameObject.activeSelf).ToArray();

        [UnityTest]
        public IEnumerator Update_SpawnsTilesOnGridUnderCamera()
        {
            _floorManager.Initialize(_floorPrefab, _player, _camera);
            yield return null;

            var tiles = ActiveTiles;
            Assert.IsNotEmpty(tiles);

            // 화면 중앙(원점)을 덮는 타일이 있고, 모든 타일은 격자에 맞춰 겹치지 않게 배치
            Assert.IsTrue(tiles.Any(tile => tile.position == Vector3.zero));
            foreach (var tile in tiles)
            {
                Assert.AreEqual(0, tile.position.x % TileSize, 1e-4f);
                Assert.AreEqual(0, tile.position.y);
                Assert.AreEqual(0, tile.position.z % TileSize, 1e-4f);
            }

            Assert.AreEqual(tiles.Length, tiles.Select(tile => tile.position).Distinct().Count());
        }

        [UnityTest]
        public IEnumerator Update_CameraMoved_ReusesTilesFromPool()
        {
            _floorManager.Initialize(_floorPrefab, _player, _camera);
            yield return null;

            var tileCount = _floorManager.transform.childCount;

            _camera.transform.position += new Vector3(1000, 0, 0);
            yield return null;

            Assert.AreEqual(tileCount, _floorManager.transform.childCount);
            Assert.IsTrue(ActiveTiles.All(tile => tile.position.x > 900));
        }

        [UnityTest]
        public IEnumerator Update_PooledTileDestroyed_CreatesNewTile()
        {
            _floorManager.Initialize(_floorPrefab, _player, _camera);
            yield return null;

            var tileCount = ActiveTiles.Length;

            // 카메라를 낮춰 보이는 영역을 줄이면 남는 타일은 Pool로 들어감
            _camera.transform.position = new Vector3(0, 2, -2);
            yield return null;

            var pooledTiles = _floorManager.transform.Cast<Transform>().Where(tile => !tile.gameObject.activeSelf).ToArray();
            Assert.IsNotEmpty(pooledTiles);

            // Pool에 들어간 타일이 외부에서 파괴된 경우
            foreach (var tile in pooledTiles)
                Object.Destroy(tile.gameObject);

            yield return null;

            _camera.transform.position = new Vector3(0, 10, -10);
            yield return null;

            Assert.AreEqual(tileCount, ActiveTiles.Length);
        }

        [UnityTest]
        public IEnumerator Update_ActiveTileDestroyed_IsSkippedOnRelease()
        {
            _floorManager.Initialize(_floorPrefab, _player, _camera);
            yield return null;

            var tileCount = ActiveTiles.Length;
            Object.Destroy(ActiveTiles[0].gameObject);
            yield return null;

            _camera.transform.position += new Vector3(1000, 0, 0);
            yield return null;

            Assert.AreEqual(tileCount, ActiveTiles.Length);
        }

        [UnityTest]
        public IEnumerator Update_CameraLooksAtSky_SpawnsNothing()
        {
            _camera.transform.rotation = Quaternion.LookRotation(Vector3.up);
            _floorManager.Initialize(_floorPrefab, _player, _camera);
            yield return null;

            Assert.AreEqual(0, _floorManager.transform.childCount);
        }

        [UnityTest]
        public IEnumerator Update_FloorBeyondFarClipPlane_LimitsRange()
        {
            // 지평선이 보이도록 수평에 가깝게 바라보면 먼 모서리는 최대 거리로 제한
            _camera.transform.rotation = Quaternion.LookRotation(new Vector3(0, -0.2f, 1));
            _camera.farClipPlane = 50;
            _floorManager.Initialize(_floorPrefab, _player, _camera);
            yield return null;

            var tiles = ActiveTiles;
            Assert.IsNotEmpty(tiles);
            Assert.IsTrue(tiles.All(tile => tile.position.z <= 50 + TileSize));
        }

        [UnityTest]
        public IEnumerator Update_PlayerDestroyed_StopsUpdating()
        {
            _floorManager.Initialize(_floorPrefab, _player, _camera);
            Object.Destroy(_player);
            yield return null;

            Assert.AreEqual(0, _floorManager.transform.childCount);
        }

        [UnityTest]
        public IEnumerator Update_CameraDestroyed_StopsUpdating()
        {
            _floorManager.Initialize(_floorPrefab, _player, _camera);
            Object.Destroy(_camera.gameObject);
            yield return null;

            Assert.AreEqual(0, _floorManager.transform.childCount);
        }

        public enum InvalidSetup
        {
            NullPrefab,
            PrefabWithoutMeshFilter,
            PrefabWithoutMesh,
            ZeroSizePrefab,
            NullPlayer,
            NullCamera,
        }

        [UnityTest]
        public IEnumerator Initialize_InvalidSetup_LogsErrorAndSpawnsNothing(
            [Values] InvalidSetup setup)
        {
            var floorPrefab = _floorPrefab;
            var player = _player;
            var camera = _camera;

            switch (setup)
            {
                case InvalidSetup.NullPrefab:
                    floorPrefab = null;
                    LogAssert.Expect(LogType.Error, new Regex("인자가 null 입니다"));
                    break;

                case InvalidSetup.PrefabWithoutMeshFilter:
                    floorPrefab = _helper.CreateTemplate("Empty");
                    LogAssert.Expect(LogType.Error, new Regex("MeshFilter Component가 존재하지 않습니다"));
                    break;

                case InvalidSetup.PrefabWithoutMesh:
                    floorPrefab.GetComponent<MeshFilter>().sharedMesh = null;
                    LogAssert.Expect(LogType.Error, new Regex("인자가 null 입니다"));
                    break;

                case InvalidSetup.ZeroSizePrefab:
                    floorPrefab.transform.localScale = new Vector3(0, 1, 1);
                    LogAssert.Expect(LogType.Error, new Regex("크기가 올바르지 않습니다"));
                    break;

                case InvalidSetup.NullPlayer:
                    player = null;
                    LogAssert.Expect(LogType.Error, new Regex("인자가 null 입니다"));
                    break;

                case InvalidSetup.NullCamera:
                    camera = null;
                    LogAssert.Expect(LogType.Error, new Regex("인자가 null 입니다"));
                    break;
            }

            _floorManager.Initialize(floorPrefab, player, camera);
            yield return null;

            Assert.AreEqual(0, _floorManager.transform.childCount);
        }
    }
}
