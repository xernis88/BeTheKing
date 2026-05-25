// ============================================================
// RoyalGaugeSystem — Unit Tests
// Story: production/epics/epic-victory-endgame/story-002-throne-gauge.md
// GDD: design/gdd/04-victory-endgame.md
//
// 자동화 범위: 게이지 상승 속도, 이탈 초기화, 누적 포인트, 승리 조건,
//              SyncClientRpc 파라미터, Edge Case(119이탈), 복수 플레이어 독립성,
//              영역 내 0명, 승리 후 재발화 방지
// 플레이테스트 범위: SyncGaugeClientRpc 실제 클라이언트 동기화 (NGO 의존)
// ============================================================

using System;
using System.Collections.Generic;
using NUnit.Framework;
using BeTheKing.GameplaySystems;

namespace BeTheKing.Tests.Unit.Gameplay
{
    // ──────────────────────────────────────────────────────────
    // TestableRoyalGaugeSystem — NetworkBehaviour 의존성 분리
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// NetworkBehaviour 없이 RoyalGaugeSystem 로직만 검증하기 위한 테스트 전용 래퍼.
    /// IsServer를 필드로 시뮬레이션하고, Time.deltaTime 대신 Tick(deltaTime) 주입을 사용한다.
    /// </summary>
    internal class TestableRoyalGaugeSystem
    {
        public bool IsServer { get; set; } = true;

        public float MaxGauge { get; }

        // 내부 상태 — 테스트에서 직접 접근 가능 (internal)
        internal readonly Dictionary<ulong, float> _gauges = new();
        internal readonly Dictionary<ulong, float> _cumulative = new();
        internal readonly SortedSet<ulong> _inZone = new();

        // SyncGaugeClientRpc 호출 추적
        public int SyncCallCount { get; private set; }

        // 마지막 SyncClientRpc 호출 파라미터
        public (ulong clientId, float gauge, float cumulative) LastSyncCall { get; private set; }

        // 승리 이벤트 콜백 (VictoryManager 대체)
        public event Action<ulong, VictoryReason> OnVictoryConditionMet;

        // 승리 후 재발화 방지 플래그
        private bool _victoryDeclared;

        public TestableRoyalGaugeSystem(float maxGauge = 120f)
        {
            MaxGauge = maxGauge;
        }

        public void OnPlayerEnter(ulong clientId)
        {
            if (!IsServer) return;
            _inZone.Add(clientId);
        }

        public void OnPlayerExit(ulong clientId)
        {
            if (!IsServer) return;

            _inZone.Remove(clientId);

            float currentGauge = _gauges.GetValueOrDefault(clientId, 0f);
            float previousCumulative = _cumulative.GetValueOrDefault(clientId, 0f);
            float newCumulative = previousCumulative + currentGauge;

            _cumulative[clientId] = newCumulative;
            _gauges[clientId] = 0f;

            SyncGaugeClientRpc(clientId, 0f, newCumulative);
        }

        /// <summary>
        /// RoyalGaugeSystem.Tick()에 대응하는 테스트용 진입점.
        /// Time.deltaTime 대신 임의의 deltaTime을 주입하여 결정적 테스트를 수행한다.
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (!IsServer) return;
            if (_inZone.Count == 0) return;
            if (_victoryDeclared) return;

            ulong? victoryWinner = null;

            foreach (ulong clientId in _inZone)
            {
                float newGauge = _gauges.GetValueOrDefault(clientId, 0f) + deltaTime;
                _gauges[clientId] = newGauge;

                SyncGaugeClientRpc(clientId, newGauge, _cumulative.GetValueOrDefault(clientId, 0f));

                if (newGauge >= MaxGauge)
                {
                    victoryWinner = clientId;
                    break;
                }
            }

            if (victoryWinner.HasValue)
            {
                _victoryDeclared = true;

                ulong winner = victoryWinner.Value;
                _cumulative[winner] = _cumulative.GetValueOrDefault(winner, 0f) + _gauges[winner];

                OnVictoryConditionMet?.Invoke(winner, VictoryReason.GaugeFull);
            }
        }

        private void SyncGaugeClientRpc(ulong clientId, float gauge, float cumulative)
        {
            SyncCallCount++;
            LastSyncCall = (clientId, gauge, cumulative);
        }

        /// <summary>SyncCallCount 카운터를 초기화한다.</summary>
        public void ResetSyncCount() => SyncCallCount = 0;

        /// <summary>
        /// RoyalGaugeSystem.GetHighestCumulativePlayer()에 대응하는 테스트용 진입점.
        /// cumulative + 현재 inZone gauge 합산 기준 최고점 플레이어를 반환한다.
        /// </summary>
        public ulong? GetHighestCumulativePlayer()
        {
            ulong? best = null;
            float bestScore = float.MinValue;

            var allTracked = new HashSet<ulong>(_cumulative.Keys);
            foreach (ulong id in _inZone) allTracked.Add(id);

            var sorted = new List<ulong>(allTracked);
            sorted.Sort();

            foreach (ulong id in sorted)
            {
                float score = _cumulative.GetValueOrDefault(id, 0f)
                            + _gauges.GetValueOrDefault(id, 0f);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = id;
                }
            }

            return best;
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-VE002-1: 게이지 상승 속도
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("RoyalGaugeSystem")]
    public class ThroneGaugeRiseRateTests
    {
        private TestableRoyalGaugeSystem _gauge;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp()
        {
            _gauge = new TestableRoyalGaugeSystem();
            _gauge.OnPlayerEnter(ClientA);
        }

        /// <summary>TC-VE002-1: 60초 경과 시 게이지가 약 60 상승한다.</summary>
        [Test]
        public void test_royalGauge_rise_after60Seconds_gaugeIsApprox60()
        {
            // Arrange — SetUp에서 ClientA 진입 완료

            // Act
            _gauge.Tick(60f);

            // Assert
            Assert.AreEqual(60f, _gauge._gauges[ClientA], 0.1f,
                "60초 경과 시 게이지는 약 60이어야 한다 (GDD: 1/초).");
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-VE002-2: 이탈 즉시 초기화 + 누적 적립
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("RoyalGaugeSystem")]
    public class ThroneGaugeExitResetTests
    {
        private TestableRoyalGaugeSystem _gauge;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp() => _gauge = new TestableRoyalGaugeSystem();

        /// <summary>TC-VE002-2: 이탈 시 게이지가 0으로 초기화되고 누적 포인트에 가산된다.</summary>
        [Test]
        public void test_royalGauge_exit_gaugeResetAndCumulativeAdded()
        {
            // Arrange
            _gauge._gauges[ClientA] = 80f;
            _gauge._inZone.Add(ClientA);

            // Act
            _gauge.OnPlayerExit(ClientA);

            // Assert
            Assert.AreEqual(0f, _gauge._gauges[ClientA], 0.001f,
                "이탈 시 게이지가 즉시 0으로 초기화되어야 한다.");
            Assert.AreEqual(80f, _gauge._cumulative[ClientA], 0.001f,
                "이탈 전 게이지 80이 누적 포인트에 가산되어야 한다.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-VE002-3: 누적 포인트 2회 이탈
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("RoyalGaugeSystem")]
    public class ThroneGaugeCumulativeAccumulationTests
    {
        private TestableRoyalGaugeSystem _gauge;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp() => _gauge = new TestableRoyalGaugeSystem();

        /// <summary>TC-VE002-3: 2회 이탈 시 누적 포인트가 합산된다 (50 + 30 = 80).</summary>
        [Test]
        public void test_royalGauge_exit_twiceExits_cumulativeIsSumOfBoth()
        {
            // Arrange — 1차 이탈: gauge=50
            _gauge._gauges[ClientA] = 50f;
            _gauge._inZone.Add(ClientA);
            _gauge.OnPlayerExit(ClientA);

            // 재진입 후 게이지 30 적립
            _gauge._inZone.Add(ClientA);
            _gauge._gauges[ClientA] = 30f;

            // Act — 2차 이탈
            _gauge.OnPlayerExit(ClientA);

            // Assert
            Assert.AreEqual(80f, _gauge._cumulative[ClientA], 0.001f,
                "2회 이탈 누적 포인트는 50 + 30 = 80이어야 한다.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-VE002-4: 게이지 120 도달 즉시 승리
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("RoyalGaugeSystem")]
    public class ThroneGaugeVictoryConditionTests
    {
        private TestableRoyalGaugeSystem _gauge;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp() => _gauge = new TestableRoyalGaugeSystem();

        /// <summary>TC-VE002-4: 게이지가 120에 도달하면 OnVictoryConditionMet(GaugeFull)이 호출된다.</summary>
        [Test]
        public void test_royalGauge_victory_gaugeReaches120_declareVictoryCalled()
        {
            // Arrange
            bool victoryCalled = false;
            ulong victoryClientId = 0UL;
            VictoryReason victoryReason = VictoryReason.TimeUp;

            _gauge.OnVictoryConditionMet += (id, reason) =>
            {
                victoryCalled = true;
                victoryClientId = id;
                victoryReason = reason;
            };

            _gauge._gauges[ClientA] = 119f;
            _gauge._inZone.Add(ClientA);

            // Act — 1초 경과로 120 도달
            _gauge.Tick(1f);

            // Assert
            Assert.IsTrue(victoryCalled,
                "게이지 120 도달 시 OnVictoryConditionMet이 호출되어야 한다.");
            Assert.AreEqual(ClientA, victoryClientId,
                "승리 이벤트의 clientId가 올바른 플레이어여야 한다.");
            Assert.AreEqual(VictoryReason.GaugeFull, victoryReason,
                "승리 이유가 GaugeFull이어야 한다.");
        }

        /// <summary>TC-VE002-4b: 승리 후 Tick을 추가 호출해도 OnVictoryConditionMet이 재발행되지 않는다.</summary>
        [Test]
        public void test_royalGauge_victory_secondTickAfterWin_doesNotRefire()
        {
            // Arrange
            int victoryCallCount = 0;
            _gauge.OnVictoryConditionMet += (_, __) => victoryCallCount++;

            _gauge._gauges[ClientA] = 119f;
            _gauge._inZone.Add(ClientA);
            _gauge.Tick(1f);  // 승리 발생

            // Act — 추가 Tick
            _gauge.Tick(1f);
            _gauge.Tick(1f);

            // Assert
            Assert.AreEqual(1, victoryCallCount,
                "승리 이벤트는 한 번만 발행되어야 한다 (재발화 방지).");
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-VE002-5: SyncGaugeClientRpc 이탈 시 파라미터
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("RoyalGaugeSystem")]
    public class ThroneGaugeSyncRpcParameterTests
    {
        private TestableRoyalGaugeSystem _gauge;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp() => _gauge = new TestableRoyalGaugeSystem();

        /// <summary>TC-VE002-5: OnPlayerExit 시 SyncRpc가 (clientId, 0f, 80f) 파라미터로 호출된다.</summary>
        [Test]
        public void test_royalGauge_syncRpc_onExit_correctParametersGaugeZeroAndUpdatedCumulative()
        {
            // Arrange
            _gauge._gauges[ClientA] = 50f;
            _gauge._cumulative[ClientA] = 30f;
            _gauge._inZone.Add(ClientA);
            _gauge.ResetSyncCount();

            // Act
            _gauge.OnPlayerExit(ClientA);

            // Assert
            Assert.AreEqual(1, _gauge.SyncCallCount,
                "이탈 시 SyncGaugeClientRpc가 정확히 1회 호출되어야 한다.");
            Assert.AreEqual(ClientA, _gauge.LastSyncCall.clientId,
                "SyncRpc clientId 파라미터가 올바른 플레이어여야 한다.");
            Assert.AreEqual(0f, _gauge.LastSyncCall.gauge, 0.001f,
                "SyncRpc gauge 파라미터가 0f이어야 한다 (이탈 후 초기화).");
            Assert.AreEqual(80f, _gauge.LastSyncCall.cumulative, 0.001f,
                "SyncRpc cumulative 파라미터가 30 + 50 = 80f이어야 한다.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-VE002-6: 게이지 119 이탈 — 승리 없음 (GDD Edge Case)
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("RoyalGaugeSystem")]
    public class ThroneGaugeEdgeCaseExitAt119Tests
    {
        private TestableRoyalGaugeSystem _gauge;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp() => _gauge = new TestableRoyalGaugeSystem();

        /// <summary>TC-VE002-6: 게이지 119에서 이탈 시 누적만 적립되고 승리는 선언되지 않는다.</summary>
        [Test]
        public void test_royalGauge_exitAt119_cumulativeAddedButNoVictory()
        {
            // Arrange
            bool victoryCalled = false;
            _gauge.OnVictoryConditionMet += (_, __) => victoryCalled = true;

            _gauge._gauges[ClientA] = 119f;
            _gauge._inZone.Add(ClientA);

            // Act
            _gauge.OnPlayerExit(ClientA);

            // Assert
            Assert.AreEqual(0f, _gauge._gauges[ClientA], 0.001f,
                "이탈 시 게이지가 0으로 초기화되어야 한다.");
            Assert.AreEqual(119f, _gauge._cumulative[ClientA], 0.001f,
                "119 게이지가 누적 포인트에 가산되어야 한다.");
            Assert.IsFalse(victoryCalled,
                "119에서 이탈 시 승리가 선언되어서는 안 된다 (GDD Edge Case).");
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-VE002-7: 여러 플레이어 동시 진입 — 독립 게이지
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("RoyalGaugeSystem")]
    public class ThroneGaugeMultiPlayerIndependenceTests
    {
        private TestableRoyalGaugeSystem _gauge;
        private const ulong ClientA = 1UL;
        private const ulong ClientB = 2UL;

        [SetUp]
        public void SetUp()
        {
            _gauge = new TestableRoyalGaugeSystem();
            _gauge.OnPlayerEnter(ClientA);
            _gauge.OnPlayerEnter(ClientB);
        }

        /// <summary>TC-VE002-7: 두 플레이어가 동시 진입해도 각 게이지는 독립적으로 상승한다.</summary>
        [Test]
        public void test_royalGauge_multiPlayer_independentGaugesAfter30Seconds()
        {
            // Arrange — SetUp에서 A, B 동시 진입 완료

            // Act
            _gauge.Tick(30f);

            // Assert
            Assert.AreEqual(30f, _gauge._gauges[ClientA], 0.1f,
                "플레이어 A의 게이지가 30이어야 한다.");
            Assert.AreEqual(30f, _gauge._gauges[ClientB], 0.1f,
                "플레이어 B의 게이지가 30이어야 한다.");

            // A 이탈 후에도 B 게이지 유지 확인
            _gauge.OnPlayerExit(ClientA);
            _gauge.Tick(10f);

            Assert.AreEqual(0f, _gauge._gauges[ClientA], 0.001f,
                "A 이탈 후 A의 게이지가 0이어야 한다.");
            Assert.AreEqual(40f, _gauge._gauges[ClientB], 0.1f,
                "A 이탈과 무관하게 B의 게이지가 독립적으로 계속 상승해야 한다.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-VE002-8: 영역 내 0명 — SyncCallCount = 0
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("RoyalGaugeSystem")]
    public class ThroneGaugeEmptyZoneTests
    {
        private TestableRoyalGaugeSystem _gauge;

        [SetUp]
        public void SetUp() => _gauge = new TestableRoyalGaugeSystem();

        /// <summary>TC-VE002-8: 영역 내 플레이어 0명일 때 Tick 실행해도 SyncRpc 호출 없음.</summary>
        [Test]
        public void test_royalGauge_emptyZone_tickDoesNotCallSyncRpc()
        {
            // Arrange
            Assert.AreEqual(0, _gauge._inZone.Count, "사전 조건: 영역 내 플레이어 없어야 함.");
            _gauge.ResetSyncCount();

            // Act
            _gauge.Tick(1f);

            // Assert
            Assert.AreEqual(0, _gauge.SyncCallCount,
                "영역 내 플레이어가 없으면 SyncGaugeClientRpc가 호출되지 않아야 한다.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-VE002-9: GetHighestCumulativePlayer — 타임아웃 승자 선정
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("RoyalGaugeSystem")]
    public class ThroneGaugeHighestCumulativeTests
    {
        private TestableRoyalGaugeSystem _gauge;
        private const ulong ClientA = 1UL;
        private const ulong ClientB = 2UL;
        private const ulong ClientC = 3UL;

        [SetUp]
        public void SetUp() => _gauge = new TestableRoyalGaugeSystem();

        /// <summary>TC-VE002-9a: 누적 포인트가 다를 때 최고값 보유자가 반환된다.</summary>
        [Test]
        public void test_highestCumulative_differentScores_returnsHighestPlayer()
        {
            // Arrange
            _gauge._cumulative[ClientA] = 80f;
            _gauge._cumulative[ClientB] = 120f;
            _gauge._cumulative[ClientC] = 50f;

            // Act
            ulong? winner = _gauge.GetHighestCumulativePlayer();

            // Assert
            Assert.AreEqual(ClientB, winner,
                "누적 포인트가 가장 높은 B(120)가 승자여야 한다.");
        }

        /// <summary>TC-VE002-9b: inZone 상태인 플레이어의 현재 게이지도 합산된다.</summary>
        [Test]
        public void test_highestCumulative_inZoneBonus_changesRanking()
        {
            // Arrange — A는 누적 100, B는 누적 80이지만 inZone + gauge 30
            _gauge._cumulative[ClientA] = 100f;
            _gauge._cumulative[ClientB] = 80f;
            _gauge._gauges[ClientB] = 30f;
            _gauge._inZone.Add(ClientB);

            // Act
            ulong? winner = _gauge.GetHighestCumulativePlayer();

            // Assert — B(80+30=110) > A(100+0=100)
            Assert.AreEqual(ClientB, winner,
                "inZone 보정 포함 시 B(80+30=110)가 A(100)보다 높아 B가 승자여야 한다.");
        }

        /// <summary>TC-VE002-9c: 동점 시 clientId 오름차순(낮은 값)이 승자가 된다.</summary>
        [Test]
        public void test_highestCumulative_tieBreak_lowerClientIdWins_TBD()
        {
            // Arrange — A(id=1)와 B(id=2) 동점
            _gauge._cumulative[ClientA] = 100f;
            _gauge._cumulative[ClientB] = 100f;

            // Act
            ulong? winner = _gauge.GetHighestCumulativePlayer();

            // Assert — 낮은 clientId 우선 (TBD: 생존 시간 기준으로 변경 예정)
            Assert.AreEqual(ClientA, winner,
                "[TBD] 동점 시 clientId 오름차순(A=1)이 승자여야 한다. " +
                "향후 생존 시간 기준으로 정책 변경 예정.");
        }

        /// <summary>TC-VE002-9d: 추적된 플레이어 없을 때 null을 반환한다.</summary>
        [Test]
        public void test_highestCumulative_noTrackedPlayers_returnsNull()
        {
            // Arrange — _cumulative, _inZone 모두 비어있음

            // Act
            ulong? winner = _gauge.GetHighestCumulativePlayer();

            // Assert
            Assert.IsNull(winner,
                "추적된 플레이어가 없을 때 GetHighestCumulativePlayer()는 null을 반환해야 한다.");
        }
    }
}
