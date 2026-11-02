using System.Collections;
using System.Linq;
using aisilol.Game;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.TestTools.Utils;

namespace aisilol.Tests.Game
{
    public class GameManagerTests
    {
        private readonly TestHelper _helper = new();
        private GameManager _gameManager;

        [SetUp]
        public void SetUp()
        {
            _helper.CreateScene();

            var playerPrefab = _helper.CreateTemplate(PrimitiveType.Capsule, "Player");
            playerPrefab.AddComponent<PlayerController>();

            var gameCameraPrefab = _helper.CreateTemplate("GameCamera");
            gameCameraPrefab.AddComponent<Camera>();
            gameCameraPrefab.AddComponent<GameCamera>();

            var enemyPrefab = _helper.CreateTemplate(PrimitiveType.Cube, "Enemy");
            enemyPrefab.AddComponent<Enemy>();
            enemyPrefab.GetComponent<MeshRenderer>().sharedMaterial = _helper.CreateMaterial(enableInstancing: true);

            // Start 전에 설정을 마치도록 비활성 상태로 구성
            var gameObject = new GameObject("GameManager");
            gameObject.SetActive(false);

            _gameManager = gameObject.AddComponent<GameManager>();
            _gameManager.playerPrefab = playerPrefab;
            _gameManager.gameCameraPrefab = gameCameraPrefab;
            _gameManager.enemyPrefab = enemyPrefab;
            _gameManager.floorPrefab = _helper.CreateTemplate(PrimitiveType.Plane, "Floor");
            _gameManager.uiText = new GameObject("UIText").AddComponent<TextMeshProUGUI>();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => _helper.UnloadScene();

        private static T FindActive<T>() where T : Object => Object.FindObjectsByType<T>(FindObjectsSortMode.None).SingleOrDefault();

        [UnityTest]
        public IEnumerator Start_CreatesGameAndShowsStatus()
        {
            _gameManager.gameObject.SetActive(true);
            yield return TestHelper.WaitFrames(5);

            // 플레이어는 바닥 위에 서 있고, 카메라가 플레이어를 따라감
            var player = FindActive<PlayerController>();
            Assert.IsNotNull(player);
            Assert.That(player.transform.position, Is.EqualTo(new Vector3(0, 1, 0)).Using(Vector3EqualityComparer.Instance));

            var gameCamera = FindActive<GameCamera>();
            Assert.IsNotNull(gameCamera);
            Assert.That(gameCamera.transform.position, Is.EqualTo(new Vector3(0, 11, -10)).Using(Vector3EqualityComparer.Instance));

            Assert.IsNotNull(_gameManager.GetComponent<FloorManager>());
            Assert.Greater(_gameManager.transform.childCount, 0);
            Assert.Greater(_gameManager.GetComponent<EnemySpawner>().EnemyCount, 0);

            StringAssert.StartsWith("Enemy Count: ", _gameManager.uiText.text);
        }

        [UnityTest]
        public IEnumerator Update_ShowsMeasuredFps()
        {
            _gameManager.gameObject.SetActive(true);
            yield return null;

            // FPS 측정 구간(0.1초)보다 길게 진행
            var endTime = Time.realtimeSinceStartupAsDouble + 0.3;
            while (Time.realtimeSinceStartupAsDouble < endTime)
                yield return null;

            var match = TestHelper.Pattern(@"FPS : ([\d.]+)").Match(_gameManager.uiText.text);
            Assert.IsTrue(match.Success, _gameManager.uiText.text);
            Assert.Greater(float.Parse(match.Groups[1].Value), 0);
        }

        [UnityTest]
        public IEnumerator Update_WithoutUIText_KeepsRunning()
        {
            _gameManager.uiText = null;
            _gameManager.gameObject.SetActive(true);
            yield return TestHelper.WaitFrames(5);

            Assert.Greater(_gameManager.GetComponent<EnemySpawner>().EnemyCount, 0);
        }

        public enum InvalidSetup
        {
            NullPlayerPrefab,
            NullGameCameraPrefab,
            PlayerWithoutMeshFilter,
            ZeroHeightPlayer,
            CameraWithoutGameCamera,
            PlayerWithoutPlayerController,
            CameraWithoutCamera,
        }

        [UnityTest]
        public IEnumerator Start_InvalidSetup_StopsWithoutStartingGame([Values] InvalidSetup setup)
        {
            switch (setup)
            {
                case InvalidSetup.NullPlayerPrefab:
                    _gameManager.playerPrefab = null;
                    LogAssert.Expect(LogType.Error, TestHelper.Pattern("인자가 null 입니다"));
                    break;

                case InvalidSetup.NullGameCameraPrefab:
                    _gameManager.gameCameraPrefab = null;
                    LogAssert.Expect(LogType.Error, TestHelper.Pattern("인자가 null 입니다"));
                    break;

                // MeshFilter가 없으면 로그 없이 중단
                case InvalidSetup.PlayerWithoutMeshFilter:
                    Object.DestroyImmediate(_gameManager.playerPrefab.GetComponent<MeshFilter>());
                    break;

                case InvalidSetup.ZeroHeightPlayer:
                    _gameManager.playerPrefab.transform.localScale = new Vector3(1, 0, 1);
                    LogAssert.Expect(LogType.Error, TestHelper.Pattern("Player의 높이가 0 입니다"));
                    break;

                case InvalidSetup.CameraWithoutGameCamera:
                    Object.DestroyImmediate(_gameManager.gameCameraPrefab.GetComponent<GameCamera>());
                    LogAssert.Expect(LogType.Error, TestHelper.Pattern("GameCamera Component가 존재하지 않습니다"));
                    break;

                case InvalidSetup.PlayerWithoutPlayerController:
                    Object.DestroyImmediate(_gameManager.playerPrefab.GetComponent<PlayerController>());
                    LogAssert.Expect(LogType.Error, TestHelper.Pattern("PlayerController Component가 존재하지 않습니다"));
                    break;

                case InvalidSetup.CameraWithoutCamera:
                    Object.DestroyImmediate(_gameManager.gameCameraPrefab.GetComponent<GameCamera>());
                    Object.DestroyImmediate(_gameManager.gameCameraPrefab.GetComponent<Camera>());
                    _gameManager.gameCameraPrefab.AddComponent<GameCamera>();
                    LogAssert.Expect(LogType.Error, TestHelper.Pattern("Camera Component가 존재하지 않습니다"));
                    break;
            }

            _gameManager.gameObject.SetActive(true);
            yield return TestHelper.WaitFrames(3);

            Assert.IsNull(_gameManager.GetComponent<FloorManager>());
            Assert.AreEqual(0, _gameManager.GetComponent<EnemySpawner>().EnemyCount);
            StringAssert.DoesNotContain("Enemy Count", _gameManager.uiText.text ?? "");
        }
    }
}
