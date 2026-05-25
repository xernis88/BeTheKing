// LevelSystem — Unit Tests (XP-based)
// Story: production/epics/epic-progression-economy/story-002-level-system.md
// ADR: docs/architecture/ADR-011-level-system-server-dictionary.md
// TR: TR-PROG-002, TR-PROG-003
//
// 테스트 전략:
//   TestableLevelSystem으로 XP 축적 및 레벨업 로직을 순수 C#으로 추출.
//   NGO 없는 EditMode 환경에서 실행.
//   GainXP() 호출 → XP 누적 → 역치 도달 시 자동 레벨업 검증.

using System.Collections.Generic;
using NUnit.Framework;

namespace BeTheKing.Tests.Unit.Progression
{
    // ── Testable System ────────────────────────────────────────────────────────

    /// <summary>
    /// NGO 없이 LevelSystem XP 로직을 테스트하는 래퍼.
    /// ApplyStatsClientRpc 호출을 추적하여 레벨업 발생 여부를 검증한다.
    /// </summary>
    internal class TestableLevelSystem
    {
        private readonly int _baseXpPerLevel;
        private readonly float _hpPerLevel;
        private readonly float _attackPerLevel;
        private readonly float _staminaPerLevel;

        private readonly Dictionary<ulong, int> _playerLevel = new();
        private readonly Dictionary<ulong, int> _playerXP = new();

        public List<(ulong clientId, float hp, float atk, float sta)> LevelUpEvents { get; } = new();

        public TestableLevelSystem(int baseXpPerLevel = 100, float hpPerLevel = 20f,
            float attackPerLevel = 5f, float staminaPerLevel = 10f)
        {
            _baseXpPerLevel = baseXpPerLevel;
            _hpPerLevel = hpPerLevel;
            _attackPerLevel = attackPerLevel;
            _staminaPerLevel = staminaPerLevel;
        }

        public int GetXpToNextLevel(int level) => _baseXpPerLevel * level;

        public void GainXP(ulong clientId, int amount)
        {
            int currentXP = _playerXP.GetValueOrDefault(clientId) + amount;
            int currentLevel = _playerLevel.GetValueOrDefault(clientId);
            int required = GetXpToNextLevel(currentLevel + 1);

            if (currentXP >= required)
            {
                currentXP -= required;
                _playerLevel[clientId] = currentLevel + 1;
                _playerXP[clientId] = currentXP;
                LevelUpEvents.Add((clientId, _hpPerLevel, _attackPerLevel, _staminaPerLevel));
            }
            else
            {
                _playerXP[clientId] = currentXP;
            }
        }

        public int GetLevel(ulong clientId) => _playerLevel.GetValueOrDefault(clientId);
        public int GetXP(ulong clientId) => _playerXP.GetValueOrDefault(clientId);
    }

    // ── Test Fixtures ──────────────────────────────────────────────────────────

    [TestFixture]
    public class LevelSystemXPAccumulationTests
    {
        private TestableLevelSystem _system;

        [SetUp]
        public void SetUp()
        {
            _system = new TestableLevelSystem(baseXpPerLevel: 100);
        }

        /// <summary>XP가 역치 미만이면 레벨업 없이 XP만 누적된다.</summary>
        [Test]
        public void test_levelSystem_gainXP_belowThreshold_noLevelUp()
        {
            _system.GainXP(1UL, 50);

            Assert.AreEqual(0, _system.GetLevel(1UL), "역치 미달 시 레벨 0 유지");
            Assert.AreEqual(50, _system.GetXP(1UL), "XP가 50 누적되어야 한다");
            Assert.AreEqual(0, _system.LevelUpEvents.Count);
        }

        /// <summary>AC-1: XP 역치(100) 도달 시 자동 레벨업.</summary>
        [Test]
        public void test_levelSystem_gainXP_exactThreshold_levelsUp()
        {
            _system.GainXP(1UL, 100);

            Assert.AreEqual(1, _system.GetLevel(1UL), "레벨 1이 되어야 한다");
            Assert.AreEqual(0, _system.GetXP(1UL), "역치 도달 후 잉여 XP = 0");
            Assert.AreEqual(1, _system.LevelUpEvents.Count);
        }

        /// <summary>AC-1: XP 역치 초과 시 레벨업 + 잉여 XP 이월.</summary>
        [Test]
        public void test_levelSystem_gainXP_exceedsThreshold_carryOverXP()
        {
            _system.GainXP(1UL, 130); // 역치 100, 잉여 30

            Assert.AreEqual(1, _system.GetLevel(1UL));
            Assert.AreEqual(30, _system.GetXP(1UL), "잉여 XP 30이 이월되어야 한다");
        }

        /// <summary>여러 번 GainXP 호출로 XP가 누적 합산된다.</summary>
        [Test]
        public void test_levelSystem_multipleGainXP_accumulates()
        {
            _system.GainXP(1UL, 40);
            _system.GainXP(1UL, 40);

            Assert.AreEqual(0, _system.GetLevel(1UL), "80 XP는 역치 100 미달");
            Assert.AreEqual(80, _system.GetXP(1UL));

            _system.GainXP(1UL, 30); // 총 110 XP → 레벨업 + 잉여 10

            Assert.AreEqual(1, _system.GetLevel(1UL));
            Assert.AreEqual(10, _system.GetXP(1UL));
        }
    }

    [TestFixture]
    public class LevelSystemScalingXPTests
    {
        private TestableLevelSystem _system;

        [SetUp]
        public void SetUp()
        {
            _system = new TestableLevelSystem(baseXpPerLevel: 100);
        }

        /// <summary>레벨 1→2 요구 XP = baseXp × 1 = 100.</summary>
        [Test]
        public void test_levelSystem_xpToNextLevel_scalingFormula()
        {
            Assert.AreEqual(100, _system.GetXpToNextLevel(1));
            Assert.AreEqual(200, _system.GetXpToNextLevel(2));
            Assert.AreEqual(300, _system.GetXpToNextLevel(3));
        }

        /// <summary>레벨 2→3 요구 XP는 1→2보다 크다.</summary>
        [Test]
        public void test_levelSystem_higherLevel_requiresMoreXP()
        {
            // 레벨 0→1
            _system.GainXP(1UL, 100);
            Assert.AreEqual(1, _system.GetLevel(1UL));

            // 레벨 1→2: 요구 XP = 200
            _system.GainXP(1UL, 100); // 아직 200 미달
            Assert.AreEqual(1, _system.GetLevel(1UL), "100 XP 추가로는 레벨 2 미달");

            _system.GainXP(1UL, 100); // 총 200 → 레벨 2
            Assert.AreEqual(2, _system.GetLevel(1UL));
        }
    }

    [TestFixture]
    public class LevelSystemLevelUpStatTests
    {
        /// <summary>AC-1: 레벨업 시 올바른 스탯 증분이 ClientRpc로 발행된다.</summary>
        [Test]
        public void test_levelSystem_levelUp_appliesCorrectStatDeltas()
        {
            var system = new TestableLevelSystem(baseXpPerLevel: 100, hpPerLevel: 20f,
                attackPerLevel: 5f, staminaPerLevel: 10f);

            system.GainXP(1UL, 100);

            Assert.AreEqual(1, system.LevelUpEvents.Count);
            var (cid, hp, atk, sta) = system.LevelUpEvents[0];
            Assert.AreEqual(1UL, cid);
            Assert.AreEqual(20f, hp, 0.001f);
            Assert.AreEqual(5f, atk, 0.001f);
            Assert.AreEqual(10f, sta, 0.001f);
        }

        /// <summary>1회 GainXP에서 레벨업은 최대 1회만 발생한다.</summary>
        [Test]
        public void test_levelSystem_singleGainXP_maxOneLevelUp()
        {
            var system = new TestableLevelSystem(baseXpPerLevel: 100);

            system.GainXP(1UL, 9999); // 역치를 크게 초과해도 1회만

            Assert.AreEqual(1, system.LevelUpEvents.Count, "1회 GainXP 호출 시 최대 1레벨업");
        }
    }

    [TestFixture]
    public class LevelSystemMultiPlayerTests
    {
        /// <summary>서로 다른 플레이어의 XP/레벨이 독립적으로 관리된다.</summary>
        [Test]
        public void test_levelSystem_multipleClients_independentTracking()
        {
            var system = new TestableLevelSystem(baseXpPerLevel: 100);

            system.GainXP(1UL, 100); // 플레이어1 레벨업
            system.GainXP(2UL, 50);  // 플레이어2 미레벨업

            Assert.AreEqual(1, system.GetLevel(1UL));
            Assert.AreEqual(0, system.GetLevel(2UL));
            Assert.AreEqual(50, system.GetXP(2UL));
        }
    }

    [TestFixture]
    public class LevelSystemNoDailyLimitTests
    {
        /// <summary>AC-2: 1일 제한 없음 — 동일 조건에서 연속 레벨업 가능.</summary>
        [Test]
        public void test_levelSystem_noDailyLimit_multipleLeveUpsAllowed()
        {
            var system = new TestableLevelSystem(baseXpPerLevel: 100);

            // 레벨 1
            system.GainXP(1UL, 100);
            Assert.AreEqual(1, system.GetLevel(1UL));

            // 레벨 2 (같은 날, 제한 없음)
            system.GainXP(1UL, 200);
            Assert.AreEqual(2, system.GetLevel(1UL));

            Assert.AreEqual(2, system.LevelUpEvents.Count, "1일 제한 없이 2회 레벨업 가능");
        }
    }
}
