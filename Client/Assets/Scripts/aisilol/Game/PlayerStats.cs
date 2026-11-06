using System;
using System.Collections.Generic;

namespace aisilol.Game
{
    public enum StatType
    {
        MaxHealth,
        MoveSpeed,
        Damage,
        FireInterval,
        ProjectileCount,
        ProjectileSpeed,
        Range,
        Pierce,     // 투사체 1발이 맞힐 수 있는 Enemy 수. 에셋에 enum 값이 저장되므로 항상 끝에 추가
        Magnet,     // 경험치 Object / HP 포션을 끌어오는 반경(m)
    }

    public enum ModifierType
    {
        Add,        // 기본값에 더함
        Percent,    // 0.1 = 10%. 같은 능력치의 Percent는 합산 후 곱함
    }

    // 능력치 = (기본값 + Add 합) * (1 + Percent 합)
    public sealed class PlayerStats
    {
        private const float MinMultiplier = 0.1f;   // Percent 감소가 누적되어도 기본값의 10% 아래로 내려가지 않음

        private static readonly int StatCount = Enum.GetValues(typeof(StatType)).Length;

        private readonly float[] _base = new float[StatCount];
        private readonly float[] _add = new float[StatCount];
        private readonly float[] _percent = new float[StatCount];
        private readonly Dictionary<UpgradeDefinition, int> _levels = new();

        public float Get(StatType stat)
        {
            var index = (int)stat;
            return (_base[index] + _add[index]) * Math.Max(MinMultiplier, 1 + _percent[index]);
        }

        public void SetBase(StatType stat, float value)
        {
            _base[(int)stat] = value;
        }

        public int GetLevel(UpgradeDefinition upgrade)
        {
            return upgrade && _levels.TryGetValue(upgrade, out var level) ? level : 0;
        }

        public bool CanApply(UpgradeDefinition upgrade)
        {
            return upgrade && GetLevel(upgrade) < upgrade.maxLevel;
        }

        public bool Apply(UpgradeDefinition upgrade)
        {
            if (!CanApply(upgrade))
                return false;

            var index = (int)upgrade.stat;
            switch (upgrade.modifier)
            {
                case ModifierType.Add: _add[index] += upgrade.value; break;
                case ModifierType.Percent: _percent[index] += upgrade.value; break;
                default: return false;
            }

            _levels[upgrade] = GetLevel(upgrade) + 1;
            return true;
        }
    }
}
