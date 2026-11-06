using System;
using System.Collections.Generic;
using aisilol.Common;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace aisilol.Game
{
    // 게임 진행 상태 관리
    // Playing : 레벨에 따라 Enemy 생성 수 증가, 경험치 Object / HP 포션 획득, Enemy 접촉 시 피해
    // LevelUp : 일시 정지 후 강화 선택지 표시. 선택하면 Playing으로 복귀
    // GameOver: 체력이 0이 되면 일시 정지 후 결과 표시
    [RequireComponent(typeof(EnemyManager))]
    public class GameManager : MonoBehaviour
    {
        public enum GameState
        {
            Playing,
            LevelUp,
            GameOver,
        }

        [Header("Prefab 구성")]
        [SetInspectorName("플레이어 Prefab")]
        [SerializeField]
        public GameObject playerPrefab;

        [SetInspectorName("Enemy Prefab")]
        [SerializeField]
        public GameObject enemyPrefab;

        [SetInspectorName("게임 카메라 Prefab")]
        [SerializeField]
        public GameObject gameCameraPrefab;

        [SetInspectorName("Floor Prefab")]
        [SerializeField]
        public GameObject floorPrefab;

        [SetInspectorName("투사체 Prefab")]
        [SerializeField]
        public GameObject projectilePrefab;

        [SetInspectorName("경험치 Object Prefab")]
        [SerializeField]
        public GameObject experiencePrefab;

        [SetInspectorName("HP 포션 Prefab")]
        [SerializeField]
        public GameObject potionPrefab;

        [SetInspectorName("HP 포션 회복량")]
        [SerializeField]
        public float potionHeal = 30;

        [SetInspectorName("강화 목록")]
        [SerializeField]
        public UpgradeDefinition[] upgrades;

        // 플레이어가 강해지는 만큼 Enemy 수도 늘어나도록 레벨 기준 지수 증가. 생성 수 = 기본값 * 증가율^(레벨 - 1)
        [Header("난이도")]
        [SetInspectorName("초당 Enemy 생성 수 (1레벨)")]
        [SerializeField]
        public float spawnPerSecondBase = 15;

        [SetInspectorName("레벨당 생성 수 증가율 (배)")]
        [SerializeField]
        public float spawnPerSecondGrowth = 1.3f;

        // 첫 1분은 기본 공격 한 발에 처치되도록 배율 1 유지
        [SetInspectorName("Enemy 체력 배율 (가로축: 경과 시간(초))")]
        [SerializeField]
        public AnimationCurve enemyHealthMultiplier = new(new Keyframe(0, 1), new Keyframe(60, 1), new Keyframe(300, 4));

        [SetInspectorName("시야 밖 렌더링 제외(컬링)")]
        [SerializeField]
        public bool useCulling = true;

        [SetInspectorName("프레임당 최대 생성 수")]
        [SerializeField]
        public int maxSpawnPerFrame = 100;

        [SetInspectorName("생성 제한 FPS (평균 FPS가 이보다 낮으면 생성 중지)")]
        [SerializeField]
        public float minSpawnFps = 60;

        // HP가 낮을수록 포션이 잘 나오도록 현재 HP 비율 기준
        [SetInspectorName("HP 포션 드롭 확률 (가로축: HP 비율 0~1)")]
        [SerializeField]
        public AnimationCurve potionDropChance = new(new Keyframe(0, 0.05f), new Keyframe(0.5f, 0.015f), new Keyframe(1, 0.002f));

        [SerializeField]
        public TextMeshProUGUI uiText;

        private const int ChoiceCount = 3;
        private const float InvincibleTime = 0.5f;      // 피격 후 무적 시간(초)
        private const double FPSSampleInterval = 0.1;   // FPS 갱신 주기 (초)

        private EnemyManager _enemyManager;
        private PickupSystem _pickups;
        private PlayerStats _stats;
        private GameHud _hud;

        private readonly Experience _experience = new();
        private readonly List<UpgradeDefinition> _choices = new();
        private readonly System.Random _random = new();

        private float _invincibleTimer;
        private int _pendingLevelUps;

        private double _fpsSampleStartTime;
        private int _fpsFrameCount;
        private float _fps;

        public GameState State { get; private set; }
        public float Health { get; private set; }
        public float ElapsedTime { get; private set; }
        public PlayerStats Stats => _stats;
        public Experience Experience => _experience;
        public IReadOnlyList<UpgradeDefinition> Choices => _choices;
        internal GameHud Hud => _hud;

        public void OnClick_Restart()
        {
            Time.timeScale = 1;

            var activeScene = SceneManager.GetActiveScene();
            SceneManager.LoadScene(activeScene.name);
        }

        // 레벨업 선택지 중 index번째 강화 적용
        public void SelectUpgrade(int index)
        {
            if (State != GameState.LevelUp || index < 0 || index >= _choices.Count)
                return;

            ApplyUpgrade(_choices[index]);
            --_pendingLevelUps;

            _hud?.HideLevelUp();
            SetState(GameState.Playing);

            // 한 번에 여러 레벨이 오른 경우 이어서 선택
            if (_pendingLevelUps > 0)
                OpenLevelUp();
        }

        private bool CheckCondition()
        {
            if (!Utility.IsValid(playerPrefab, this))
                return false;

            if (!Utility.IsValid(gameCameraPrefab, this))
                return false;

            return true;
        }

        private void Start()
        {
            if (!CheckCondition())
                return;

            // 플레이어 생성 및 초기화
            if (!Utility.TryInstantiate(playerPrefab, out var player, this))
                return;

            if (!player.TryGetComponent<MeshFilter>(out var playerMeshFilter))
                return;

            var playerHalfHeight = playerMeshFilter.sharedMesh.bounds.size.y * player.transform.localScale.y * 0.5f;
            if (playerHalfHeight <= 0)
            {
                Log.Error($"Player의 높이가 0 입니다. [Size : ${playerMeshFilter.sharedMesh.bounds} / Scale : ${playerMeshFilter.transform.localScale}]");
                return;
            }

            player.transform.SetPositionAndRotation(new Vector3(0, playerHalfHeight, 0), Quaternion.identity);

            // 카메라 생성 및 초기화
            if (!Utility.TryInstantiate(gameCameraPrefab, out var camera, this))
                return;

            if (!Utility.TryGetComponent<GameCamera>(camera, out var cameraComponent, gameCameraPrefab))
                return;

            cameraComponent.Initialize(player);

            if (!Utility.TryGetComponent<PlayerController>(player, out var playerController, playerPrefab))
                return;

            var stats = playerController.CreateStats();
            playerController.Initialize(cameraComponent, stats);

            if (!Utility.TryGetComponent<Camera>(camera, out var unityCamera, gameCameraPrefab))
                return;

            // Floor 초기화
            var floorManager = gameObject.AddComponent<FloorManager>();
            floorManager.Initialize(floorPrefab, player, unityCamera);

            // Enemy 초기화
            if (!Utility.TryGetComponent(gameObject, out _enemyManager, this))
                return;

            _enemyManager.Initialize(player, cameraComponent, enemyPrefab);

            // 투사체 초기화
            var projectileSystem = gameObject.AddComponent<ProjectileSystem>();
            projectileSystem.Initialize(player, _enemyManager, projectilePrefab, stats);

            // 드롭 초기화
            _pickups = gameObject.AddComponent<PickupSystem>();
            _pickups.Initialize(player, _enemyManager, experiencePrefab, potionPrefab, potionHeal, stats);

            _stats = stats;
            Health = stats.Get(StatType.MaxHealth);

            if (uiText && uiText.canvas)
                _hud = new GameHud(uiText.canvas, ChoiceCount, SelectUpgrade, OnClick_Restart);

            SetState(GameState.Playing);
        }

        private void OnDestroy()
        {
            // 일시 정지 중 씬이 바뀌거나 종료되어도 다음 씬에 영향이 없도록 복구
            Time.timeScale = 1;
        }

        private void Update()
        {
            // Start에서 초기화에 실패한 경우
            if (!_enemyManager || _stats == null)
                return;

            switch (State)
            {
                case GameState.Playing:
                    UpdatePlaying(Time.deltaTime);
                    break;

                case GameState.LevelUp:
                    UpdateLevelUpInput();
                    break;
            }

            UpdateUI();
        }

        private void UpdatePlaying(float deltaTime)
        {
            ElapsedTime += deltaTime;
            _enemyManager.SpawnPerSecond = CalcSpawnPerSecond(_experience.Level);
            _enemyManager.MinSpawnFps = minSpawnFps;
            _enemyManager.MaxSpawnPerFrame = maxSpawnPerFrame;
            _enemyManager.UseCulling = useCulling;
            _enemyManager.HealthMultiplier = enemyHealthMultiplier.Evaluate(ElapsedTime);

            // 경험치는 Enemy 처치가 아니라 떨어진 경험치 Object를 획득해야 증가
            _pendingLevelUps += _experience.Add(_pickups.TakeExperience());

            var maxHealth = _stats.Get(StatType.MaxHealth);
            Health = Mathf.Min(maxHealth, Health + _pickups.TakeHealing());

            // 접촉 중인 Enemy 수와 관계없이 무적 시간마다 1회 피해
            _invincibleTimer -= deltaTime;
            if (_enemyManager.ContactCount > 0 && _invincibleTimer <= 0)
            {
                Health = Mathf.Max(0, Health - _enemyManager.ContactDamage);
                _invincibleTimer = InvincibleTime;
            }

            _pickups.PotionDropChance = Mathf.Clamp01(potionDropChance.Evaluate(Health / Mathf.Max(1, _stats.Get(StatType.MaxHealth))));

            if (Health <= 0)
            {
                SetState(GameState.GameOver);
                _hud?.ShowGameOver(FormatResult());
                return;
            }

            if (_pendingLevelUps > 0)
                OpenLevelUp();
        }

        private void UpdateLevelUpInput()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null)
                return;

            if (keyboard.digit1Key.wasPressedThisFrame)
                SelectUpgrade(0);
            else if (keyboard.digit2Key.wasPressedThisFrame)
                SelectUpgrade(1);
            else if (keyboard.digit3Key.wasPressedThisFrame)
                SelectUpgrade(2);
        }

        private void OpenLevelUp()
        {
            UpgradeSelector.Pick(upgrades, _stats, ChoiceCount, _random, _choices);

            // 모든 강화가 최대 레벨이면 선택 없이 진행
            if (_choices.Count == 0)
            {
                _pendingLevelUps = 0;
                return;
            }

            SetState(GameState.LevelUp);
            _hud?.ShowLevelUp(_choices, _stats);
        }

        // 최대 체력이 늘어난 만큼 현재 체력도 회복
        private void ApplyUpgrade(UpgradeDefinition upgrade)
        {
            var maxHealth = _stats.Get(StatType.MaxHealth);
            if (!_stats.Apply(upgrade))
                return;

            Health += Mathf.Max(0, _stats.Get(StatType.MaxHealth) - maxHealth);
        }

        public float CalcSpawnPerSecond(int level)
        {
            return spawnPerSecondBase * Mathf.Pow(spawnPerSecondGrowth, Mathf.Max(0, level - 1));
        }

        private void SetState(GameState state)
        {
            State = state;
            Time.timeScale = state == GameState.Playing ? 1 : 0;
        }

        private string FormatResult()
        {
            return $"Time {FormatTime(ElapsedTime)}\nKills {_enemyManager.KillCount:N0}\nLevel {_experience.Level}";
        }

        private static string FormatTime(float seconds)
        {
            return TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss");
        }

        private void UpdateUI()
        {
            _hud?.SetHealth(Health / Mathf.Max(1, _stats.Get(StatType.MaxHealth)));
            _hud?.SetExperience(_experience.Level, _experience.Progress);

            if (!uiText)
                return;

            var throttled = _enemyManager.IsSpawnThrottled ? " (spawn paused)" : "";
            var text = $"Enemy Count: {_enemyManager.EnemyCount:N0} (visible {_enemyManager.VisibleCount:N0}){throttled}\nFPS : {CalcFPS():F1}\n" +
                       $"Time : {FormatTime(ElapsedTime)}\nKills : {_enemyManager.KillCount:N0}";
            uiText.SetText(text);
        }

        // 매 프레임 1회 호출. 일정 구간 동안의 프레임 수를 실제 경과 시간으로 나눈 평균 FPS
        // timeScale 영향을 받지 않고, float 누적 오차가 없도록 double 실시간을 사용
        private float CalcFPS()
        {
            var now = Time.realtimeSinceStartupAsDouble;
            if (_fpsFrameCount == 0 && _fpsSampleStartTime == 0)
            {
                _fpsSampleStartTime = now;
                return _fps;
            }

            ++_fpsFrameCount;

            var elapsed = now - _fpsSampleStartTime;
            if (elapsed < FPSSampleInterval)
                return _fps;

            _fps = (float)(_fpsFrameCount / elapsed);
            _fpsFrameCount = 0;
            _fpsSampleStartTime = now;
            return _fps;
        }
    }
}
