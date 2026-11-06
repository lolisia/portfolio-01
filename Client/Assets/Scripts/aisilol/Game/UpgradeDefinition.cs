using System.Globalization;
using UnityEngine;

namespace aisilol.Game
{
    // 레벨업 시 선택하는 능력치 강화. 에셋 하나가 선택지 하나
    [CreateAssetMenu(menuName = "aisilol/Upgrade", fileName = "Upgrade")]
    public class UpgradeDefinition : ScriptableObject
    {
        [SetInspectorName("표시 이름")]
        [SerializeField]
        public string displayName;

        [SetInspectorName("능력치")]
        [SerializeField]
        public StatType stat;

        [SetInspectorName("적용 방식")]
        [SerializeField]
        public ModifierType modifier;

        [SetInspectorName("값 (Percent는 0.1 = 10%)")]
        [SerializeField]
        public float value;

        [SetInspectorName("최대 레벨")]
        [SerializeField]
        public int maxLevel = 5;

        // 예) "Damage +25%", "ProjectileCount +1"
        public string Description => modifier == ModifierType.Percent
            ? $"{stat} {(value * 100).ToString("+0;-0", CultureInfo.InvariantCulture)}%"
            : $"{stat} {value.ToString("+0.##;-0.##", CultureInfo.InvariantCulture)}";
    }
}
