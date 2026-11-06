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

        [Header("기본 능력치")]
        [SetInspectorName("최대 체력")]
        [SerializeField]
        public float maxHealth = 100;

        [SetInspectorName("투사체 데미지")]
        [SerializeField]
        public float damage = 2;

        [SetInspectorName("발사 간격(초)")]
        [SerializeField]
        public float fireInterval = 0.5f;

        [SetInspectorName("투사체 수")]
        [SerializeField]
        public int projectileCount = 1;

        [SetInspectorName("투사체 속도(m/s)")]
        [SerializeField]
        public float projectileSpeed = 4;

        [SetInspectorName("사거리(m)")]
        [SerializeField]
        public float range = 6;

        [SetInspectorName("관통 수")]
        [SerializeField]
        public int pierce = 1;

        [SetInspectorName("자석 범위(m)")]
        [SerializeField]
        public float magnetRange = 0.3f;

        private InputSystem_Actions _playerActions;
        private GameCamera _gameCamera;
        private PlayerStats _stats;
        
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

        public void Initialize(GameCamera gameCamera, PlayerStats stats = null)
        {
            if (!Utility.IsValid(gameCamera, this))
                return;

            _gameCamera = gameCamera;
            _stats = stats;
        }

        // Inspector의 기본 능력치로 PlayerStats 생성. 강화는 생성된 PlayerStats에 누적
        public PlayerStats CreateStats()
        {
            var stats = new PlayerStats();
            stats.SetBase(StatType.MaxHealth, maxHealth);
            stats.SetBase(StatType.MoveSpeed, moveSpeed);
            stats.SetBase(StatType.Damage, damage);
            stats.SetBase(StatType.FireInterval, fireInterval);
            stats.SetBase(StatType.ProjectileCount, projectileCount);
            stats.SetBase(StatType.ProjectileSpeed, projectileSpeed);
            stats.SetBase(StatType.Range, range);
            stats.SetBase(StatType.Pierce, pierce);
            stats.SetBase(StatType.Magnet, magnetRange);
            return stats;
        }

        private void UpdateInput(Vector2 input)
        {
            var direction = new Vector3(input.x, 0, input.y);
            var targetRotation = Quaternion.LookRotation(direction, Vector3.up);

            transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, rotateSpeed * 180 * Time.deltaTime);
            var speed = _stats?.Get(StatType.MoveSpeed) ?? moveSpeed;
            transform.position += direction * (speed * Time.deltaTime);
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