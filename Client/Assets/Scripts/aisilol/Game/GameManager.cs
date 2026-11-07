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

        [SetInspectorName("강화 자동 선택 (레벨업 시 멈추지 않고 선택지 중 하나를 바로 적용)")]
        [SerializeField]
        public bool autoSelectUpgrade;

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

        // 시작 레벨까지는 1배, 이후 레벨에 비례해 증가. 체력 배율 = 1 + 증가량 * (레벨 - 시작 레벨)
        // 기본값이면 100레벨에서 약 25배(체력 2 → 약 50)
        [SetInspectorName("Enemy 체력 증가 시작 레벨")]
        [SerializeField]
        public int enemyHealthStartLevel = 30;

        [SetInspectorName("레벨당 Enemy 체력 배율 증가량")]
        [SerializeField]
        public float enemyHealthGrowthPerLevel = 0.34f;

        [SetInspectorName("시야 밖 렌더링 제외(컬링)")]
        [SerializeField]
        public bool useCulling = true;

        [SetInspectorName("GPU 렌더링 (Compute Shader 컬링 + RenderMeshIndirect)")]
        [SerializeField]
        public bool useGpuRendering = true;

        [SetInspectorName("경험치 Object 최대 수 (넘으면 넓은 범위로 병합)")]
        [SerializeField]
        public int maxExperienceObjects = 5000;

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
        private WeaponSystem _weapons;
        private PlayerStats _stats;
        private GameHud _hud;

        private readonly Experience _experience = new();
        private readonly List<UpgradeDefinition> _choices = new();
        private Unity.Mathematics.Random _random = new((uint)System.Environment.TickCount | 1);

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
        internal WeaponSystem Weapons => _weapons;

        public void OnClick_Restart()
        {
            Time.timeScale = 1;

            var activeScene = SceneManager.GetActiveScene();
            SceneManager.LoadScene(activeScene.name);
        }

        // 켜면 이후 레벨업은 멈추지 않고 자동 선택. 선택지가 열려 있으면 바로 선택
        public void SetAutoSelectUpgrade(bool value)
        {
            autoSelectUpgrade = value;

            if (value && State == GameState.LevelUp)
                SelectUpgrade(0);
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

            // 투사체 외 무기 초기화. 칼날은 투사체 Mesh 사용
            _weapons = gameObject.AddComponent<WeaponSystem>();
            _weapons.Initialize(player, _enemyManager, projectilePrefab, stats);

            // 드롭 초기화
            _pickups = gameObject.AddComponent<PickupSystem>();
            _pickups.Initialize(player, _enemyManager, experiencePrefab, potionPrefab, potionHeal, stats);

            _stats = stats;
            Health = stats.Get(StatType.MaxHealth);

            if (uiText && uiText.canvas)
                _hud = new GameHud(uiText.canvas, ChoiceCount, SelectUpgrade, OnClick_Restart, autoSelectUpgrade, SetAutoSelectUpgrade);

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
            _enemyManager.UseGpuRendering = useGpuRendering;
            _pickups.MaxExperienceObjects = maxExperienceObjects;
            _enemyManager.HealthMultiplier = CalcEnemyHealthMultiplier(_experience.Level);

            // 경험치는 Enemy 처치가 아니라 떨어진 경험치 Object를 획득해야 증가
            GainExperience(_pickups.TakeExperience());

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

        // 레벨이 오르면 맵에 남은 경험치 Object를 모두 끌어옴
        internal void GainExperience(int amount)
        {
            var levelUps = _experience.Add(amount);
            if (levelUps <= 0)
                return;

            _pendingLevelUps += levelUps;
            _pickups.AttractAllExperience();
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

        // 자동 선택이면 멈추지 않고 무작위 선택지의 첫 번째를 남은 레벨업 수만큼 적용
        private void OpenLevelUp()
        {
            while (_pendingLevelUps > 0)
            {
                UpgradeSelector.Pick(upgrades, _stats, ChoiceCount, ref _random, _choices);

                // 모든 강화가 최대 레벨이면 선택 없이 진행
                if (_choices.Count == 0)
                {
                    _pendingLevelUps = 0;
                    return;
                }

                if (!autoSelectUpgrade)
                {
                    SetState(GameState.LevelUp);
                    _hud?.ShowLevelUp(_choices, _stats);
                    return;
                }

                ApplyUpgrade(_choices[0]);
                --_pendingLevelUps;
            }
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

        // 시작 레벨 이하는 1배. 증가량이 음수여도 1배 아래로 내려가지 않음
        public float CalcEnemyHealthMultiplier(int level)
        {
            return Mathf.Max(1, 1 + enemyHealthGrowthPerLevel * Mathf.Max(0, level - enemyHealthStartLevel));
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
            var renderer = _enemyManager.IsGpuRendering ? "GPU indirect" : "CPU instanced";
            var text = $"Enemy Count: {_enemyManager.EnemyCount:N0} (visible {_enemyManager.VisibleCount:N0}){throttled}\nFPS : {CalcFPS():F1}\nRenderer : {renderer}\n" +
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
