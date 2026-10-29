using aisilol.Common;
using UnityEngine;

namespace aisilol.Game
{
    public class GameManager : MonoBehaviour
    {
        [Header("Prefab 구성")]
        [SetInspectorName("플레이어 Prefab")]
        [SerializeField]
        public GameObject playerPrefab;

        [SetInspectorName("게임 카메라 Prefab")]
        [SerializeField]
        public GameObject gameCameraPrefab;

        [SetInspectorName("Floor Prefab")]
        [SerializeField]
        public GameObject floorPrefab;

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
        }
    }
}