using UnityEngine;

namespace aisilol.Game
{
    // 이동은 EnemySpawner가 Job으로 일괄 처리. Enemy는 개별 설정값만 보관
    public class Enemy : MonoBehaviour
    {
        [Header("설정")]
        [SetInspectorName("이동 속도(m/s)")]
        [SerializeField]
        public Vector2 moveSpeed = new(0.1f, 0.2f);

        public float CurrentSpeed { get; private set; }
        
        public void Awake()
        {
            CurrentSpeed = Random.Range(moveSpeed.x, moveSpeed.y);
        }
    }
}
