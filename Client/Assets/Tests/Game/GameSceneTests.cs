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
            Assert.Greater(gameManager.GetComponent<EnemySpawner>().EnemyCount, 0);
            StringAssert.StartsWith("Enemy Count: ", gameManager.uiText.text);
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
