using aisilol.Common;
using UnityEngine;

namespace aisilol.Game
{
    public class GameCamera : MonoBehaviour
    {
        [Header("카메라 설정")]
        [SetInspectorName("따라가기 속도")]
        [SerializeField]
        public float followSpeed = 3;

        [SetInspectorName("높이")]
        [SerializeField] 
        public float cameraHeight = 10;

        [SetInspectorName("거리")]
        [SerializeField] 
        public float cameraDistance = 10;

        [SetInspectorName("줌 비율 (휠 한 칸)")]
        [SerializeField]
        public float zoomStep = 0.1f;

        private const float MinDistance = 2;    // 줌 인 시 카메라 거리의 최솟값

        private GameObject _player;

        public delegate void OnZoomChangedDelegate();
        public OnZoomChangedDelegate onZoomChanged;
        
        public void Initialize(GameObject player)
        {
            if (!Utility.IsValid(player))
                return;
            
            _player = player;
            SetPosition(_player.transform.position);
        }

        // direction > 0 이면 줌 인, < 0 이면 줌 아웃. 높이와 거리는 같은 비율로 변경
        public void Zoom(float direction)
        {
            if (direction == 0 || cameraDistance <= 0)
                return;

            var step = Mathf.Clamp(zoomStep, 0.01f, 0.9f);
            var factor = direction > 0 ? 1 - step : 1 / (1 - step);
            var distance = Mathf.Max(cameraDistance * factor, MinDistance);

            cameraHeight *= distance / cameraDistance;
            cameraDistance = distance;
            
            onZoomChanged?.Invoke();
        }

        private void SetPosition(Vector3 lookPosition)
        {
            transform.position = new Vector3(lookPosition.x, lookPosition.y + cameraHeight, lookPosition.z - cameraDistance);
        }
        
        private void Update()
        {
            if (!_player)
                return;

            SetPosition(_player.transform.position);  
            transform.LookAt(_player.transform.position);
        }
    }
}