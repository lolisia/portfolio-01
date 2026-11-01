using aisilol.Common;
using UnityEngine;

namespace aisilol.Game
{
    public class PlayerController : MonoBehaviour
    {
        [Header("이동 설정")] 
        [SetInspectorName("이동 속도(m/s)")]
        [SerializeField] 
        public float moveSpeed = 5;

        [SetInspectorName("회전 속도")]
        [SerializeField] 
        public float rotateSpeed = 5;

        private InputSystem_Actions _playerActions;
        private GameCamera _gameCamera;
        
        private void Awake()
        {
            _playerActions = new InputSystem_Actions();
        }

        private void OnEnable()
        {
            if (Utility.IsValid(_playerActions, this))
                _playerActions.Enable();
        }

        private void OnDestroy()
        {
            if (Utility.IsValid(_playerActions, this))
                _playerActions.Disable();
        }

        public void Initialize(GameCamera gameCamera)
        {
            if (!Utility.IsValid(gameCamera, this))
                return;

            _gameCamera = gameCamera;
        }

        private void UpdateInput(Vector2 input)
        {
            var direction = new Vector3(input.x, 0, input.y);
            var targetRotation = Quaternion.LookRotation(direction, Vector3.up);

            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, rotateSpeed * 180 * Time.deltaTime);
            transform.position += direction * (moveSpeed * Time.deltaTime);
        }

        private void UpdateZoom(float delta)
        {
            if (!_gameCamera)
                return;

            _gameCamera.Zoom(delta);
        }
        
        private void Update()
        {
            if (_playerActions == null)
                return;

            var playerMove = _playerActions.Player.Move.ReadValue<Vector2>();
            if (playerMove != Vector2.zero)
                UpdateInput(playerMove);
            
            var playerCamera = _playerActions.Player.Camera.ReadValue<float>();
            if (playerCamera != 0)
                UpdateZoom(playerCamera);
        }
    }
}