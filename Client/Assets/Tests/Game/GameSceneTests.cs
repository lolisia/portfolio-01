using System.Collections;
using aisilol.Game;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace aisilol.Tests.Game
{
    // 실제 GameScene과 Prefab / Material 설정으로 게임이 시작되는지 확인
    public class GameSceneTests
    {
        private const string SceneName = "GameScene";

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // 마지막 씬은 언로드할 수 없으므로 빈 씬을 먼저 만든 뒤 정리
            SceneManager.SetActiveScene(SceneManager.CreateScene("Empty"));

            var scene = SceneManager.GetSceneByName(SceneName);
            if (scene.isLoaded)
                yield return SceneManager.UnloadSceneAsync(scene);
        }

        [UnityTest]
        public IEnumerator GameScene_StartsGame()
        {
            yield return SceneManager.LoadSceneAsync(SceneName);
            yield return TestHelper.WaitFrames(5);

            var gameManager = Object.FindAnyObjectByType<GameManager>();
            Assert.IsNotNull(gameManager);
            Assert.IsNotNull(Object.FindAnyObjectByType<PlayerController>());

            // 초당 생성 수 기준이라 첫 Enemy가 나올 때까지 대기
            yield return TestHelper.WaitUntil(() => gameManager.GetComponent<EnemyManager>().EnemyCount > 0);
            Assert.Greater(gameManager.GetComponent<EnemyManager>().EnemyCount, 0);
            StringAssert.StartsWith("Enemy Count: ", gameManager.uiText.text);

            // 투사체 Prefab / 강화 에셋이 연결되어 있고 HUD가 만들어짐
            Assert.IsNotNull(gameManager.GetComponent<ProjectileSystem>());
            Assert.IsNotNull(gameManager.experiencePrefab);
            Assert.IsNotNull(gameManager.potionPrefab);
            Assert.IsNotEmpty(gameManager.upgrades);
            CollectionAssert.AllItemsAreNotNull(gameManager.upgrades);
            Assert.IsNotNull(gameManager.Hud);
        }

        [UnityTest]
        public IEnumerator GameScene_SpawnsJustOutsideScreen()
        {
            yield return SceneManager.LoadSceneAsync(SceneName);

            // 첫 프레임에는 카메라가 아직 플레이어를 보지 않아 최대 거리(200m)가 계산됨. 이후 프레임에 화면 모서리 거리로 갱신되어야 함
            var enemies = Object.FindAnyObjectByType<GameManager>().GetComponent<EnemyManager>();
            yield return TestHelper.WaitUntil(() => enemies.EnemyCount > 0);
            yield return TestHelper.WaitFrames(3);

            Assert.Greater(enemies.SpawnDistance, 0);
            Assert.Less(enemies.SpawnDistance, 50);
        }

        [UnityTest]
        public IEnumerator OnClick_Restart_ReloadsScene()
        {
            yield return SceneManager.LoadSceneAsync(SceneName);
            yield return TestHelper.WaitFrames(3);

            var gameManager = Object.FindAnyObjectByType<GameManager>();
            gameManager.OnClick_Restart();
            yield return TestHelper.WaitFrames(3);

            var reloaded = Object.FindAnyObjectByType<GameManager>();
            Assert.IsNotNull(reloaded);
            Assert.AreNotSame(gameManager, reloaded);
            Assert.AreEqual(SceneName, SceneManager.GetActiveScene().name);
        }
    }
}
