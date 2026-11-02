using aisilol.Common;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace aisilol.Game
{
    [RequireComponent(typeof(EnemySpawner))]
    public class GameManager : MonoBehaviour
    {
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

        [SerializeField]
        public TextMeshProUGUI uiText;

        private EnemySpawner _enemySpawner;

        private const double FPSSampleInterval = 0.1;   // FPS 갱신 주기 (초)

        private double _fpsSampleStartTime;
        private int _fpsFrameCount;
        private float _fps;

        public void OnClick_Restart()
        {
            var activeScene = SceneManager.GetActiveScene();
            SceneManager.LoadScene(activeScene.name);
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

            playerController.Initialize(cameraComponent);

            if (!Utility.TryGetComponent<Camera>(camera, out var unityCamera, gameCameraPrefab))
                return;

            // Floor 초기화
            var floorManager = gameObject.AddComponent<FloorManager>();
            floorManager.Initialize(floorPrefab, player, unityCamera);
            
            // Enemy 초기화
            if (!Utility.TryGetComponent(gameObject, out _enemySpawner, this))
                return;
            
            _enemySpawner.Initialize(player, cameraComponent, enemyPrefab);
        }

        private void Update()
        {
            // Start에서 초기화에 실패했거나 UI가 연결되지 않은 경우
            if (!_enemySpawner || !uiText)
                return;

            var text = $"Enemy Count: {_enemySpawner.EnemyCount:N0}\nFPS : {CalcFPS():F1}";
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