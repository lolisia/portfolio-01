using System.Collections;
using System.Collections.Generic;
using System.Linq;
using aisilol.Game;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace aisilol.Tests.Game
{
    public class PlayerStatsTests
    {
        private readonly TestHelper _helper = new();

        [UnityTearDown]
        public IEnumerator TearDown() => _helper.UnloadScene();

        [Test]
        public void Get_WithoutUpgrade_ReturnsBaseValue()
        {
            var stats = new PlayerStats();
            stats.SetBase(StatType.Damage, 3);

            Assert.AreEqual(3, stats.Get(StatType.Damage));
            Assert.AreEqual(0, stats.Get(StatType.Range));
        }

        [Test]
        public void Apply_AddThenPercent_Combines()
        {
            var stats = new PlayerStats();
            stats.SetBase(StatType.Damage, 10);

            Assert.IsTrue(stats.Apply(_helper.CreateUpgrade(StatType.Damage, ModifierType.Percent, 0.5f)));
            Assert.IsTrue(stats.Apply(_helper.CreateUpgrade(StatType.Damage, ModifierType.Add, 5)));

            // (10 + 5) * (1 + 0.5)
            Assert.AreEqual(22.5f, stats.Get(StatType.Damage), 1e-5f);
        }

        [Test]
        public void Apply_PercentDecrease_ClampsAtMinimumMultiplier()
        {
            var stats = new PlayerStats();
            stats.SetBase(StatType.FireInterval, 1);

            var upgrade = _helper.CreateUpgrade(StatType.FireInterval, ModifierType.Percent, -0.6f);
            stats.Apply(upgrade);
            stats.Apply(upgrade);

            Assert.AreEqual(0.1f, stats.Get(StatType.FireInterval), 1e-5f);
        }

        [Test]
        public void Apply_StopsAtMaxLevel()
        {
            var stats = new PlayerStats();
            var upgrade = _helper.CreateUpgrade(StatType.ProjectileCount, ModifierType.Add, 1, maxLevel: 2);

            Assert.IsTrue(stats.Apply(upgrade));
            Assert.IsTrue(stats.Apply(upgrade));
            Assert.IsFalse(stats.CanApply(upgrade));
            Assert.IsFalse(stats.Apply(upgrade));

            Assert.AreEqual(2, stats.GetLevel(upgrade));
            Assert.AreEqual(2, stats.Get(StatType.ProjectileCount));
        }

        [Test]
        public void Apply_Null_ReturnsFalse()
        {
            var stats = new PlayerStats();

            Assert.IsFalse(stats.Apply(null));
            Assert.AreEqual(0, stats.GetLevel(null));
        }

        [Test]
        public void Description_FormatsByModifier()
        {
            Assert.AreEqual("Damage +25%", _helper.CreateUpgrade(StatType.Damage, ModifierType.Percent, 0.25f).Description);
            Assert.AreEqual("FireInterval -12%", _helper.CreateUpgrade(StatType.FireInterval, ModifierType.Percent, -0.12f).Description);
            Assert.AreEqual("ProjectileCount +1", _helper.CreateUpgrade(StatType.ProjectileCount, ModifierType.Add, 1).Description);
        }

        [Test]
        public void Experience_RequiredGrowsWithLevel()
        {
            Assert.AreEqual(10, Experience.RequiredFor(1));
            Assert.AreEqual(28, Experience.RequiredFor(2));
            Assert.AreEqual(52, Experience.RequiredFor(3));
        }

        [Test]
        public void Experience_Add_LevelsUpAndCarriesOver()
        {
            var experience = new Experience();

            Assert.AreEqual(0, experience.Add(9));
            Assert.AreEqual(0.9f, experience.Progress, 1e-5f);

            Assert.AreEqual(1, experience.Add(1));
            Assert.AreEqual(2, experience.Level);
            Assert.AreEqual(0, experience.Current);

            // 28 + 52로 두 레벨, 남은 5는 이월
            Assert.AreEqual(2, experience.Add(28 + 52 + 5));
            Assert.AreEqual(4, experience.Level);
            Assert.AreEqual(5, experience.Current);
        }

        [Test]
        public void Experience_AddNonPositive_IsIgnored()
        {
            var experience = new Experience();

            Assert.AreEqual(0, experience.Add(0));
            Assert.AreEqual(0, experience.Add(-5));
            Assert.AreEqual(0, experience.Current);
        }

        private List<UpgradeDefinition> CreatePool(int count)
        {
            return Enumerable.Range(0, count)
                .Select(index => _helper.CreateUpgrade(StatType.Damage, ModifierType.Add, index + 1))
                .ToList();
        }

        [Test]
        public void UpgradeSelector_PicksDistinctUpgradesFromPool()
        {
            var pool = CreatePool(5);
            var result = new List<UpgradeDefinition>();
            var random = new Unity.Mathematics.Random(1);

            UpgradeSelector.Pick(pool, new PlayerStats(), 3, ref random, result);

            Assert.AreEqual(3, result.Count);
            Assert.AreEqual(3, result.Distinct().Count());
            CollectionAssert.IsSubsetOf(result, pool);
        }

        [Test]
        public void UpgradeSelector_SkipsMaxLevelAndDuplicates()
        {
            var pool = CreatePool(2);
            var maxed = _helper.CreateUpgrade(StatType.Range, ModifierType.Add, 1, maxLevel: 1);
            pool.Add(maxed);
            pool.Add(pool[0]);

            var stats = new PlayerStats();
            stats.Apply(maxed);

            var result = new List<UpgradeDefinition>();
            var random = new Unity.Mathematics.Random(1);
            UpgradeSelector.Pick(pool, stats, 3, ref random, result);

            Assert.AreEqual(2, result.Count);
            CollectionAssert.DoesNotContain(result, maxed);
        }

        [Test]
        public void UpgradeSelector_SameSeed_SameResult()
        {
            var pool = CreatePool(6);
            var first = new List<UpgradeDefinition>();
            var second = new List<UpgradeDefinition>();

            var firstRandom = new Unity.Mathematics.Random(7);
            var secondRandom = new Unity.Mathematics.Random(7);

            UpgradeSelector.Pick(pool, new PlayerStats(), 3, ref firstRandom, first);
            UpgradeSelector.Pick(pool, new PlayerStats(), 3, ref secondRandom, second);

            CollectionAssert.AreEqual(first, second);
        }

        [Test]
        public void UpgradeSelector_NullPool_ReturnsEmpty()
        {
            var result = new List<UpgradeDefinition> { null };
            var random = new Unity.Mathematics.Random(1);

            UpgradeSelector.Pick(null, new PlayerStats(), 3, ref random, result);

            Assert.IsEmpty(result);
        }
    }
}
