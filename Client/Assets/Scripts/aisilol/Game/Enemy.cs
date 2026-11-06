using UnityEngine;

namespace aisilol.Game
{
    // Enemy Prefab의 설정값 보관용. 실제 Enemy는 EnemyManager가 GameObject 없이 데이터로 생성/이동/렌더링
    public class Enemy : MonoBehaviour
    {
        [Header("설정")]
        [SetInspectorName("이동 속도(m/s)")]
        [SerializeField]
        public Vector2 moveSpeed = new(0.1f, 0.2f);

        [SetInspectorName("체력")]
        [SerializeField]
        public float maxHealth = 2;

        [SetInspectorName("접촉 데미지")]
        [SerializeField]
        public float contactDamage = 10;

        [SetInspectorName("처치 경험치")]
        [SerializeField]
        public int experience = 1;
    }
}
