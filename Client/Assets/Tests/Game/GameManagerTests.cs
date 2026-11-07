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

            // 경험치 / 레벨업을 직접 처치한 수로만 확인하도록 기본은 발사하지 않고, 떨어진 경험치 Object는 모두 끌어옴
            var playerPrefab = _helper.CreateTemplate(PrimitiveType.Capsule, "Player");
            var playerController = playerPrefab.AddComponent<PlayerController>();
            playerController.range = 0;
            playerController.magnetRange = 10000;

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
            _gameManager.projectilePrefab = _helper.CreateTemplate(PrimitiveType.Sphere, "Projectile");
            _gameManager.projectilePrefab.GetComponent<MeshRenderer>().sharedMaterial = _helper.CreateMaterial(enableInstancing: true);
            _gameManager.experiencePrefab = _helper.CreateTemplate(PrimitiveType.Sphere, "ExperienceOrb");
            _gameManager.experiencePrefab.GetComponent<MeshRenderer>().sharedMaterial = _helper.CreateMaterial(enableInstancing: true);
            _gameManager.potionPrefab = _helper.CreateTemplate(PrimitiveType.Capsule, "Potion");
            _gameManager.potionPrefab.GetComponent<MeshRenderer>().sharedMaterial = _helper.CreateMaterial(enableInstancing: true);
            _gameManager.upgrades = new[]
            {
                _helper.CreateUpgrade(StatType.MaxHealth, ModifierType.Add, 20),
                _helper.CreateUpgrade(StatType.Damage, ModifierType.Percent, 0.5f),
            };
            _gameManager.spawnPerSecondBase = 100000;
            _gameManager.spawnPerSecondGrowth = 1;
            _gameManager.minSpawnFps = 0;
            _gameManager.potionDropChance = AnimationCurve.Constant(0, 1, 0);
            _gameManager.uiText = new GameObject("UIText").AddComponent<TextMeshProUGUI>();
        }

        [UnityTearDown]
        public IEnumerator TearDown() => _helper.UnloadScene();

        private static T FindActive<T>() where T : Object => Object.FindObjectsByType<T>().SingleOrDefault();

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
            Assert.IsNotNull(_gameManager.GetComponent<ProjectileSystem>());
            Assert.IsNotNull(_gameManager.GetComponent<PickupSystem>());
            Assert.IsNotNull(_gameManager.GetComponent<WeaponSystem>());
            Assert.AreSame(_gameManager.GetComponent<WeaponSystem>(), _gameManager.Weapons);
            Assert.AreEqual(GameManager.GameState.Playing, _gameManager.State);
            Assert.AreEqual(100, _gameManager.Health);
            Assert.Greater(_gameManager.transform.childCount, 0);
            Assert.Greater(_gameManager.GetComponent<EnemyManager>().EnemyCount, 0);

            StringAssert.StartsWith("Enemy Count: ", _gameManager.uiText.text);
            var renderer = GpuInstanceRenderer.IsSupported ? "Renderer : GPU indirect" : "Renderer : CPU instanced";
            StringAssert.Contains(renderer, _gameManager.uiText.text);
        }

        [UnityTest]
        public IEnumerator UseGpuRenderingOff_ShowsCpuRenderer()
        {
            _gameManager.useGpuRendering = false;
            _gameManager.gameObject.SetActive(true);
            yield return TestHelper.WaitFrames(3);

            Assert.IsFalse(_gameManager.GetComponent<EnemyManager>().IsGpuRendering);
            StringAssert.Contains("Renderer : CPU instanced", _gameManager.uiText.text);
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

            Assert.Greater(_gameManager.GetComponent<EnemyManager>().EnemyCount, 0);
        }

        private EnemyManager Enemies => _gameManager.GetComponent<EnemyManager>();

        private PickupSystem Pickups => _gameManager.GetComponent<PickupSystem>();

        // Enemy를 count개 처치. 경험치는 떨어진 경험치 Object가 플레이어에게 끌려와 획득된 뒤 반영
        private IEnumerator KillEnemies(int count)
        {
            while (Enemies.EnemyCount < count)
                yield return null;

            var kills = Enemies.KillCount;
            for (var index = 0; index < count; index++)
                Enemies.QueueDamage(index, 1000);

            yield return TestHelper.WaitUntil(() => Enemies.KillCount >= kills + count);
        }

        private void AttachCanvas()
        {
            var canvas = new GameObject("Canvas").AddComponent<Canvas>();
            _gameManager.uiText.transform.SetParent(canvas.transform, false);
        }

        [UnityTest]
        public IEnumerator Kills_GainExperienceAndOpenLevelUp()
        {
            _gameManager.gameObject.SetActive(true);
            yield return KillEnemies(10);

            // 처치만으로는 경험치가 오르지 않고, 경험치 Object를 획득해야 오름
            Assert.AreEqual(1, _gameManager.Experience.Level);
            yield return TestHelper.WaitUntil(() => _gameManager.State == GameManager.GameState.LevelUp);

            Assert.AreEqual(GameManager.GameState.LevelUp, _gameManager.State);
            Assert.AreEqual(0, Time.timeScale);
            Assert.AreEqual(2, _gameManager.Experience.Level);
            Assert.AreEqual(2, _gameManager.Choices.Count);

            // 최대 체력 강화는 늘어난 만큼 회복
            var maxHealth = _gameManager.upgrades[0];
            _gameManager.SelectUpgrade(_gameManager.Choices.ToList().IndexOf(maxHealth));

            Assert.AreEqual(GameManager.GameState.Playing, _gameManager.State);
            Assert.AreEqual(1, Time.timeScale);
            Assert.AreEqual(1, _gameManager.Stats.GetLevel(maxHealth));
            Assert.AreEqual(120, _gameManager.Stats.Get(StatType.MaxHealth));
            Assert.AreEqual(120, _gameManager.Health);
        }

        [UnityTest]
        public IEnumerator Kills_MultipleLevels_SelectsOneByOne()
        {
            _gameManager.gameObject.SetActive(true);

            // 10 + 28 = 38 경험치로 2레벨 상승. 레벨업 선택 중에는 일시 정지되어 남은 Object는 선택 후 획득
            yield return KillEnemies(40);

            var selections = 0;
            yield return TestHelper.WaitUntil(() =>
            {
                if (_gameManager.State == GameManager.GameState.LevelUp)
                {
                    _gameManager.SelectUpgrade(0);
                    ++selections;
                }

                return selections == 2;
            });

            Assert.AreEqual(3, _gameManager.Experience.Level);
            Assert.AreEqual(GameManager.GameState.Playing, _gameManager.State);
        }

        [UnityTest]
        public IEnumerator Kills_NoUpgradeAvailable_KeepsPlaying()
        {
            _gameManager.upgrades = new UpgradeDefinition[0];
            _gameManager.gameObject.SetActive(true);
            yield return KillEnemies(10);
            yield return TestHelper.WaitUntil(() => _gameManager.Experience.Level == 2);

            Assert.AreEqual(2, _gameManager.Experience.Level);
            Assert.AreEqual(GameManager.GameState.Playing, _gameManager.State);
        }

        [UnityTest]
        public IEnumerator LevelUp_AttractsAllExperienceObjects()
        {
            // 자석 범위가 작아 처치한 자리의 경험치 Object가 남아 있다가, 레벨업하면 모두 끌려와 획득
            _gameManager.playerPrefab.GetComponent<PlayerController>().magnetRange = 0.1f;
            _gameManager.gameObject.SetActive(true);
            yield return KillEnemies(5);
            yield return TestHelper.WaitFrames(3);

            Assert.Greater(Pickups.ExperienceObjectCount, 0);
            Assert.AreEqual(0, _gameManager.Experience.Current);

            // 레벨업 선택은 다음 Update에서 열림
            _gameManager.GainExperience(10);
            yield return TestHelper.WaitUntil(() => _gameManager.State == GameManager.GameState.LevelUp);
            Assert.AreEqual(GameManager.GameState.LevelUp, _gameManager.State);
            _gameManager.SelectUpgrade(0);

            yield return TestHelper.WaitUntil(() => Pickups.ExperienceObjectCount == 0);

            Assert.AreEqual(0, Pickups.ExperienceObjectCount);
            Assert.AreEqual(2, _gameManager.Experience.Level);
            Assert.GreaterOrEqual(_gameManager.Experience.Current, 5);
        }

        [UnityTest]
        public IEnumerator Potion_HealsUpToMaxHealth()
        {
            _gameManager.potionDropChance = AnimationCurve.Constant(0, 1, 1);
            _gameManager.gameObject.SetActive(true);
            yield return KillEnemies(3);

            yield return TestHelper.WaitUntil(() => Pickups.PotionCount == 0);

            // 체력이 가득 찬 상태에서 회복해도 최대 체력을 넘지 않음
            Assert.AreEqual(0, Pickups.PotionCount);
            Assert.AreEqual(100, _gameManager.Health);
        }

        [UnityTest]
        public IEnumerator PotionDropChance_RisesAsHealthDrops()
        {
            // HP 비율 1 → 확률 0, HP 비율 0 → 확률 1
            _gameManager.potionDropChance = AnimationCurve.Linear(0, 1, 1, 0);
            var enemy = _gameManager.enemyPrefab.GetComponent<Enemy>();
            enemy.moveSpeed = new Vector2(1000, 1000);
            enemy.contactDamage = 40;

            _gameManager.gameObject.SetActive(true);
            yield return null;
            Assert.AreEqual(0, Pickups.PotionDropChance, 1e-5f);

            // 접촉 피해 40 → HP 비율 0.6 → 확률 0.4
            yield return TestHelper.WaitUntil(() => _gameManager.Health < 100);
            yield return null;
            Assert.AreEqual(1 - _gameManager.Health / 100, Pickups.PotionDropChance, 1e-5f);
        }

        [Test]
        public void CalcSpawnPerSecond_GrowsExponentiallyByLevel()
        {
            _gameManager.spawnPerSecondBase = 10;
            _gameManager.spawnPerSecondGrowth = 2;

            Assert.AreEqual(10, _gameManager.CalcSpawnPerSecond(1));
            Assert.AreEqual(20, _gameManager.CalcSpawnPerSecond(2));
            Assert.AreEqual(80, _gameManager.CalcSpawnPerSecond(4));
            Assert.AreEqual(10, _gameManager.CalcSpawnPerSecond(0));
        }

        [UnityTest]
        public IEnumerator SelectUpgrade_NotLevelUp_IsIgnored()
        {
            _gameManager.gameObject.SetActive(true);
            yield return null;

            _gameManager.SelectUpgrade(0);

            Assert.AreEqual(GameManager.GameState.Playing, _gameManager.State);
            Assert.AreEqual(0, _gameManager.Stats.GetLevel(_gameManager.upgrades[0]));
        }

        [UnityTest]
        public IEnumerator LevelUp_Hud_ShowsChoicesAndSelectsByButton()
        {
            AttachCanvas();
            _gameManager.gameObject.SetActive(true);
            yield return KillEnemies(10);
            yield return TestHelper.WaitUntil(() => _gameManager.State == GameManager.GameState.LevelUp);

            var hud = _gameManager.Hud;
            Assert.IsNotNull(hud);
            Assert.IsTrue(hud.LevelUpPanel.activeSelf);
            Assert.IsTrue(hud.ChoiceButtons[1].gameObject.activeSelf);
            Assert.IsFalse(hud.ChoiceButtons[2].gameObject.activeSelf);

            hud.ChoiceButtons[0].onClick.Invoke();

            Assert.IsFalse(hud.LevelUpPanel.activeSelf);
            Assert.AreEqual(GameManager.GameState.Playing, _gameManager.State);
        }

        [UnityTest]
        public IEnumerator Contact_DamagesPlayerUntilGameOver()
        {
            var enemy = _gameManager.enemyPrefab.GetComponent<Enemy>();
            enemy.moveSpeed = new Vector2(1000, 1000);
            enemy.contactDamage = 100;

            AttachCanvas();
            _gameManager.gameObject.SetActive(true);

            // 첫 접촉에서 체력 100이 모두 사라짐
            var endTime = Time.realtimeSinceStartupAsDouble + 5;
            while (_gameManager.State != GameManager.GameState.GameOver && Time.realtimeSinceStartupAsDouble < endTime)
                yield return null;

            Assert.AreEqual(GameManager.GameState.GameOver, _gameManager.State);
            Assert.AreEqual(0, _gameManager.Health);
            Assert.AreEqual(0, Time.timeScale);
            Assert.IsTrue(_gameManager.Hud.GameOverPanel.activeSelf);
            StringAssert.StartsWith("Time ", _gameManager.Hud.ResultText.text);
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
            Assert.IsNull(_gameManager.GetComponent<ProjectileSystem>());
            Assert.IsNull(_gameManager.GetComponent<PickupSystem>());
            Assert.AreEqual(0, _gameManager.GetComponent<EnemyManager>().EnemyCount);
            StringAssert.DoesNotContain("Enemy Count", _gameManager.uiText.text ?? "");
        }
    }
}
