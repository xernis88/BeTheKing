// LevelSystem — Unit Tests
// Story: production/epics/epic-progression-economy/story-002-level-system.md
// ADR: docs/architecture/ADR-011-level-system-server-dictionary.md
// TR: TR-PROG-002, TR-PROG-003
//
// 테스트 전략:
//   NGO NetworkBehaviour는 EditMode에서 IsServer/ClientRpc를 사용할 수 없음.
//   TestableLevelSystem으로 날짜 추적 및 레벨업 로직을 순수 C#으로 추출.
//   CurrencySystem.TrySpend 의존성은 Func<ulong, int, bool> 델리게이트로 주입.
//   SessionTimeManager.CurrentDay는 int 파라미터로 직접 주입.

using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace BeTheKing.Tests.Unit.Progression
{
    // ── Testable System ────────────────────────────────────────────────────────

    /// <summary>
    /// NGO 없이 LevelSystem 로직을 테스트하는 래퍼.
    /// IsServer, SessionTimeManager.CurrentDay, CurrencySystem.TrySpend를 외부 주입으로 대체.
    /// </summary>
    internal class TestableLevelSystem
    {
        public const int MaxLevel = 3;

        public bool SimulatedIsServer { get; set; } = true;

        private readonly float _hpPerLevel;
        private readonly float _attackPerLevel;
        private readonly float _staminaPerLevel;
        private readonly int _levelUpCost;
        private readonly Func<ulong, int, bool> _trySpend;

        private readonly Dictionary<ulong, int> _playerLevel = new();
        private readonly Dictionary<ulong, int> _lastLevelUpDay = new();

        // ClientRpc 호출 추적
        public List<(ulong clientId, float hp, float atk, float sta)> AppliedStats { get; } = new();

        public TestableLevelSystem(
            float hpPerLevel = 20f,
            float attackPerLevel = 5f,
            float staminaPerLevel = 10f,
            int levelUpCost = 50,
            Func<ulong, int, bool> trySpend = null)
        {
            _hpPerLevel = hpPerLevel;
            _attackPerLevel = attackPerLevel;
            _staminaPerLevel = staminaPerLevel;
            _levelUpCost = levelUpCost;
            _trySpend = trySpend ?? ((_, __) => true); // 기본: 항상 성공
        }

        public bool TryLevelUp(ulong clientId, int today)
        {
            if (!SimulatedIsServer) return false;

            _lastLevelUpDay.TryGetValue(clientId, out int lastDay);
            if (lastDay >= today) return false;

            int currentLevel = _playerLevel.GetValueOrDefault(clientId);
            if (currentLevel >= MaxLevel) return false;

            if (!_trySpend(clientId, _levelUpCost)) return false;

            _lastLevelUpDay[clientId] = today;
            _playerLevel[clientId] = currentLevel + 1;

            // ClientRpc 시뮬레이션
            AppliedStats.Add((clientId, _hpPerLevel, _attackPerLevel, _staminaPerLevel));
            return true;
        }

        public int GetLevel(ulong clientId) => _playerLevel.GetValueOrDefault(clientId);
        public int GetLastLevelUpDay(ulong clientId) => _lastLevelUpDay.GetValueOrDefault(clientId);
        public void SetLastLevelUpDay(ulong clientId, int day) => _lastLevelUpDay[clientId] = day;
    }

    // ── Test Fixtures ──────────────────────────────────────────────────────────

    [TestFixture]
    public class LevelSystemLevelUpSuccessTests
    {
        private TestableLevelSystem _system;
        private List<(ulong, int)> _spendCalls;

        [SetUp]
        public void SetUp()
        {
            _spendCalls = new List<(ulong, int)>();
            _system = new TestableLevelSystem(
                hpPerLevel: 20f, attackPerLevel: 5f, staminaPerLevel: 10f, levelUpCost: 50,
                trySpend: (id, amount) => { _spendCalls.Add((id, amount)); return true; });
        }

        /// <summary>AC-1: 레벨업 성공 시 true 반환 + 레벨 상승.</summary>
        [Test]
        public void test_levelSystem_tryLevelUp_returnsTrue_andIncrementsLevel()
        {
            bool result = _system.TryLevelUp(1UL, today: 1);

            Assert.IsTrue(result, "충분한 금화 + 오늘 레벨업 안 함 → true 반환");
            Assert.AreEqual(1, _system.GetLevel(1UL), "레벨이 1이 되어야 한다");
        }

        /// <summary>AC-1: 레벨업 성공 시 CurrencySystem.TrySpend() 호출됨.</summary>
        [Test]
        public void test_levelSystem_tryLevelUp_callsTrySpendWithCorrectArgs()
        {
            _system.TryLevelUp(42UL, today: 1);

            Assert.AreEqual(1, _spendCalls.Count, "TrySpend가 1회 호출되어야 한다");
            Assert.AreEqual(42UL, _spendCalls[0].Item1, "올바른 clientId로 차감");
            Assert.AreEqual(50, _spendCalls[0].Item2, "올바른 비용으로 차감");
        }

        /// <summary>AC-1: 레벨업 성공 시 ApplyStats(ClientRpc) 발행됨.</summary>
        [Test]
        public void test_levelSystem_tryLevelUp_appliesStatDeltas()
        {
            _system.TryLevelUp(1UL, today: 1);

            Assert.AreEqual(1, _system.AppliedStats.Count, "스탯 적용이 1회 발생해야 한다");
            var (cid, hp, atk, sta) = _system.AppliedStats[0];
            Assert.AreEqual(1UL, cid);
            Assert.AreEqual(20f, hp, 0.001f);
            Assert.AreEqual(5f, atk, 0.001f);
            Assert.AreEqual(10f, sta, 0.001f);
        }
    }

    [TestFixture]
    public class LevelSystemDailyLimitTests
    {
        private TestableLevelSystem _system;

        [SetUp]
        public void SetUp()
        {
            _system = new TestableLevelSystem();
        }

        /// <summary>AC-2: 오늘 이미 레벨업 → false 반환, 스탯 변화 없음.</summary>
        [Test]
        public void test_levelSystem_sameDay_secondAttemptRejected()
        {
            _system.TryLevelUp(1UL, today: 1); // 첫 레벨업
            bool second = _system.TryLevelUp(1UL, today: 1); // 같은 날 재시도

            Assert.IsFalse(second, "같은 날 2번째 레벨업은 거부되어야 한다");
            Assert.AreEqual(1, _system.GetLevel(1UL), "레벨이 1에서 더 오르지 않아야 한다");
            Assert.AreEqual(1, _system.AppliedStats.Count, "스탯 적용이 1회만 발생해야 한다");
        }

        /// <summary>AC-4: 날짜 변경 시 레벨업 재허용.</summary>
        [Test]
        public void test_levelSystem_nextDay_levelUpAllowedAgain()
        {
            _system.TryLevelUp(1UL, today: 1);
            bool day2 = _system.TryLevelUp(1UL, today: 2);

            Assert.IsTrue(day2, "다음 날에는 레벨업이 허용되어야 한다");
            Assert.AreEqual(2, _system.GetLevel(1UL));
        }

        /// <summary>Edge-1: _lastLevelUpDay 미등록 상태 첫 레벨업 허용.</summary>
        [Test]
        public void test_levelSystem_firstLevelUp_noEntryInDict_allowed()
        {
            bool result = _system.TryLevelUp(99UL, today: 1);

            Assert.IsTrue(result, "딕셔너리 미등록 상태(기본값 0 < today 1)에서 레벨업 허용");
        }

        /// <summary>Edge-2: lastDay == today → 거부 (>= 조건).</summary>
        [Test]
        public void test_levelSystem_lastDayEqualsToday_rejected()
        {
            _system.SetLastLevelUpDay(1UL, day: 2);
            bool result = _system.TryLevelUp(1UL, today: 2);

            Assert.IsFalse(result, "lastDay == today 이면 거부되어야 한다 (>= 판정)");
        }
    }

    [TestFixture]
    public class LevelSystemMaxLevelTests
    {
        private TestableLevelSystem _system;

        [SetUp]
        public void SetUp()
        {
            _system = new TestableLevelSystem();
        }

        /// <summary>AC-3 (GDD): 최대 레벨(3) 도달 후 추가 레벨업 거부.</summary>
        [Test]
        public void test_levelSystem_maxLevel_furtherLevelUpRejected()
        {
            _system.TryLevelUp(1UL, today: 1);
            _system.TryLevelUp(1UL, today: 2);
            _system.TryLevelUp(1UL, today: 3);

            bool fourth = _system.TryLevelUp(1UL, today: 4);

            Assert.IsFalse(fourth, "레벨 3에서 추가 레벨업은 거부되어야 한다");
            Assert.AreEqual(3, _system.GetLevel(1UL));
        }

        /// <summary>최대 레벨 도달 시 TrySpend 미호출 (비용 차감 없음).</summary>
        [Test]
        public void test_levelSystem_maxLevel_trySpendNotCalled()
        {
            int spendCount = 0;
            var system = new TestableLevelSystem(trySpend: (_, __) => { spendCount++; return true; });

            system.TryLevelUp(1UL, today: 1);
            system.TryLevelUp(1UL, today: 2);
            system.TryLevelUp(1UL, today: 3); // MaxLevel 도달
            int countBeforeFourth = spendCount;

            system.TryLevelUp(1UL, today: 4); // MaxLevel 초과 시도

            Assert.AreEqual(countBeforeFourth, spendCount, "최대 레벨 도달 후 TrySpend가 호출되지 않아야 한다");
        }
    }

    [TestFixture]
    public class LevelSystemCurrencyFailTests
    {
        /// <summary>AC-3: 금화 부족 시 TryLevelUp false 반환.</summary>
        [Test]
        public void test_levelSystem_insufficientCurrency_levelUpRejected()
        {
            var system = new TestableLevelSystem(trySpend: (_, __) => false);

            bool result = system.TryLevelUp(1UL, today: 1);

            Assert.IsFalse(result, "금화 부족 시 false 반환");
            Assert.AreEqual(0, system.GetLevel(1UL), "레벨이 오르지 않아야 한다");
            Assert.AreEqual(0, system.AppliedStats.Count, "스탯 적용 없음");
        }

        /// <summary>금화 부족 시 날짜 기록도 갱신되지 않는다.</summary>
        [Test]
        public void test_levelSystem_insufficientCurrency_lastLevelUpDayNotUpdated()
        {
            var system = new TestableLevelSystem(trySpend: (_, __) => false);

            system.TryLevelUp(1UL, today: 1);

            Assert.AreEqual(0, system.GetLastLevelUpDay(1UL), "실패 시 날짜 기록이 갱신되지 않아야 한다");
        }
    }

    [TestFixture]
    public class LevelSystemMultiPlayerTests
    {
        /// <summary>서로 다른 플레이어의 레벨업이 독립적으로 처리된다.</summary>
        [Test]
        public void test_levelSystem_multipleClients_independentLevelTracking()
        {
            var system = new TestableLevelSystem();

            system.TryLevelUp(1UL, today: 1);
            system.TryLevelUp(2UL, today: 1);
            system.TryLevelUp(1UL, today: 2);

            Assert.AreEqual(2, system.GetLevel(1UL), "플레이어 1은 레벨 2");
            Assert.AreEqual(1, system.GetLevel(2UL), "플레이어 2는 레벨 1 (Day 2 레벨업 안 함)");
        }
    }
}
