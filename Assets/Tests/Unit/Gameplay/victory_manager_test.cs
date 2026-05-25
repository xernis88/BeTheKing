// ============================================================
// VictoryManager — Unit Tests
// Story: production/epics/epic-victory-endgame/story-003-victory-manager.md
// GDD: design/gdd/04-victory-endgame.md
// Requirement: TR-VICT-005, TR-VICT-007, TR-VICT-009
//
// 자동화 범위: 게이지 승리, 타임아웃 승리, 생존자 1명 승리, 중복 판정 방지,
//              서버 가드, 동점 처리(TBD 마킹), GameOver 전환
// 플레이테스트 범위: AnnounceWinnerClientRpc 실제 클라이언트 동기화 (NGO 의존)
// ============================================================

using System.Collections.Generic;
using NUnit.Framework;
using BeTheKing.GameplaySystems;

namespace BeTheKing.Tests.Unit.Gameplay
{
    // ──────────────────────────────────────────────────────────
    // TestableVictoryManager — NetworkBehaviour 의존성 분리
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// NGO 없이 VictoryManager 로직만 검증하기 위한 테스트 전용 래퍼.
    /// IsServer를 필드로 시뮬레이션하고, 의존 시스템(PlayerManager, RoyalGaugeSystem,
    /// GameStateManager, SessionTimeManager)을 Mock 카운터 또는 주입 가능한 데이터로 대체한다.
    /// </summary>
    internal class TestableVictoryManager
    {
        // ── IsServer 시뮬레이션 ─────────────────────────────────
        public bool IsServer { get; set; } = true;

        // ── 생존자 추적 (PlayerManager 대체) ──────────────────────
        public readonly HashSet<ulong> _alivePlayers = new();

        // ── 승리 상태 ──────────────────────────────────────────
        public bool _victoryDeclared;

        // ── 타임아웃 승리용 게이지 데이터 (RoyalGaugeSystem 대체) ──
        public readonly Dictionary<ulong, float> _mockCumulative = new();

        // ── 관찰 가능한 출력 ───────────────────────────────────
        public int  DeclareVictoryCallCount   { get; private set; }
        public bool GameOverTransitioned      { get; private set; }
        public int  AnnounceRpcCallCount      { get; private set; }
        public (ulong winnerId, VictoryReason reason) LastVictory { get; private set; }

        // ── 진입점 — PlayerManager 이벤트 시뮬레이션 ──────────────

        /// <summary>PlayerManager.OnPlayerSpawned 시뮬레이션.</summary>
        public void SimulatePlayerSpawned(ulong clientId) => _alivePlayers.Add(clientId);

        /// <summary>
        /// PlayerManager.OnPlayerDied 시뮬레이션.
        /// 사망 플레이어 제거 후 생존자 1명 조건 검사.
        /// </summary>
        public void SimulatePlayerDied(ulong clientId)
        {
            _alivePlayers.Remove(clientId);

            if (_victoryDeclared) return;

            if (_alivePlayers.Count == 1)
            {
                ulong lastAlive = 0UL;
                foreach (ulong id in _alivePlayers) { lastAlive = id; break; }
                DeclareVictory(lastAlive, VictoryReason.LastSurvivor);
            }
        }

        // ── 진입점 — RoyalGaugeSystem 이벤트 시뮬레이션 ───────────

        /// <summary>RoyalGaugeSystem.OnVictoryConditionMet 시뮬레이션.</summary>
        public void SimulateGaugeVictory(ulong winnerId, VictoryReason reason)
            => DeclareVictory(winnerId, reason);

        // ── 진입점 — SessionTimeManager 이벤트 시뮬레이션 ──────────

        /// <summary>
        /// SessionTimeManager.OnSessionEnded 시뮬레이션.
        /// _mockCumulative 기반으로 최고 누적 포인트 플레이어를 선정하여 DeclareVictory를 호출한다.
        /// </summary>
        public void SimulateSessionEnded()
        {
            if (_victoryDeclared) return;

            ulong? winner = GetHighestCumulativeFromMock();
            if (winner.HasValue)
                DeclareVictory(winner.Value, VictoryReason.TimeUp);
        }

        // ── 핵심 판정 로직 ─────────────────────────────────────

        /// <summary>
        /// VictoryManager.DeclareVictory에 대응하는 테스트용 진입점.
        /// IsServer 가드 및 중복 방지 포함.
        /// </summary>
        internal void DeclareVictory(ulong winnerId, VictoryReason reason)
        {
            if (!IsServer)  return;
            if (_victoryDeclared) return;

            _victoryDeclared = true;
            DeclareVictoryCallCount++;
            LastVictory = (winnerId, reason);

            // AnnounceWinnerClientRpc 시뮬레이션
            AnnounceRpcCallCount++;

            // GameStateManager.TransitionTo(GameOver) 시뮬레이션
            GameOverTransitioned = true;
        }

        // ── 내부 유틸 ──────────────────────────────────────────

        /// <summary>
        /// _mockCumulative에서 최고 누적 포인트 보유자를 반환한다.
        /// RoyalGaugeSystem.GetHighestCumulativePlayer()와 동일한 결정 로직(clientId 오름차순).
        /// </summary>
        private ulong? GetHighestCumulativeFromMock()
        {
            ulong? best = null;
            float bestScore = float.MinValue;

            var sorted = new List<ulong>(_mockCumulative.Keys);
            sorted.Sort();

            foreach (ulong id in sorted)
            {
                float score = _mockCumulative.GetValueOrDefault(id, 0f);
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
    // TC-VE003-1: 게이지 풀 승리
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("VictoryManager")]
    public class VictoryManagerGaugeFullTests
    {
        private TestableVictoryManager _vm;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp() => _vm = new TestableVictoryManager();

        /// <summary>
        /// TC-VE003-1: 게이지 120 도달 시 AnnounceWinnerClientRpc 호출 및 GameOver 전환.
        /// TR-VICT-005 검증.
        /// </summary>
        [Test]
        public void test_victoryManager_gaugeFull_declaresVictoryAndTransitionsToGameOver()
        {
            // Arrange — ClientA가 게이지를 모두 채운 상황 시뮬레이션

            // Act
            _vm.SimulateGaugeVictory(ClientA, VictoryReason.GaugeFull);

            // Assert
            Assert.IsTrue(_vm._victoryDeclared,
                "승리 선언 후 _victoryDeclared가 true여야 한다.");
            Assert.AreEqual(1, _vm.DeclareVictoryCallCount,
                "DeclareVictory가 정확히 1회 호출되어야 한다.");
            Assert.AreEqual(ClientA, _vm.LastVictory.winnerId,
                "승자 clientId가 ClientA여야 한다.");
            Assert.AreEqual(VictoryReason.GaugeFull, _vm.LastVictory.reason,
                "승리 이유가 GaugeFull이어야 한다.");
            Assert.AreEqual(1, _vm.AnnounceRpcCallCount,
                "AnnounceWinnerClientRpc가 정확히 1회 호출되어야 한다.");
            Assert.IsTrue(_vm.GameOverTransitioned,
                "GameStateManager.TransitionTo(GameOver)가 호출되어야 한다.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-VE003-2: 타임아웃 승리
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("VictoryManager")]
    public class VictoryManagerTimeoutTests
    {
        private TestableVictoryManager _vm;
        private const ulong ClientA = 1UL;
        private const ulong ClientB = 2UL;

        [SetUp]
        public void SetUp() => _vm = new TestableVictoryManager();

        /// <summary>
        /// TC-VE003-2: 세션 종료 시 누적 포인트 최고자(A=200, B=150)가 승리한다.
        /// TR-VICT-007 검증.
        /// </summary>
        [Test]
        public void test_victoryManager_sessionEnded_highestCumulativePlayerWins()
        {
            // Arrange
            _vm._mockCumulative[ClientA] = 200f;
            _vm._mockCumulative[ClientB] = 150f;

            // Act
            _vm.SimulateSessionEnded();

            // Assert
            Assert.IsTrue(_vm._victoryDeclared,
                "세션 종료 시 승리가 선언되어야 한다.");
            Assert.AreEqual(ClientA, _vm.LastVictory.winnerId,
                "누적 포인트 최고자(A=200)가 승자여야 한다.");
            Assert.AreEqual(VictoryReason.TimeUp, _vm.LastVictory.reason,
                "승리 이유가 TimeUp이어야 한다.");
            Assert.IsTrue(_vm.GameOverTransitioned,
                "GameStateManager.TransitionTo(GameOver)가 호출되어야 한다.");
        }

        /// <summary>
        /// TC-VE003-2b: 세션 종료 시 추적된 플레이어가 없으면 승리 선언이 발생하지 않는다.
        /// HandleSessionEnded의 null 분기 검증.
        /// </summary>
        [Test]
        public void test_victoryManager_sessionEnded_noTrackedPlayers_doesNotDeclareVictory()
        {
            // Arrange — _mockCumulative 비어있음 (플레이어 없음)
            Assert.AreEqual(0, _vm._mockCumulative.Count, "사전 조건: 추적된 플레이어 없어야 함.");

            // Act
            _vm.SimulateSessionEnded();

            // Assert
            Assert.IsFalse(_vm._victoryDeclared,
                "추적된 플레이어가 없을 때 승리가 선언되어서는 안 된다.");
            Assert.IsFalse(_vm.GameOverTransitioned,
                "추적된 플레이어가 없을 때 GameOver 전환이 발생해서는 안 된다.");
            Assert.AreEqual(0, _vm.DeclareVictoryCallCount,
                "추적된 플레이어가 없을 때 DeclareVictory가 호출되어서는 안 된다.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-VE003-3: 생존자 1명 즉시 승리
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("VictoryManager")]
    public class VictoryManagerLastSurvivorTests
    {
        private TestableVictoryManager _vm;
        private const ulong ClientA = 1UL;
        private const ulong ClientB = 2UL;

        [SetUp]
        public void SetUp()
        {
            _vm = new TestableVictoryManager();
            _vm.SimulatePlayerSpawned(ClientA);
            _vm.SimulatePlayerSpawned(ClientB);
        }

        /// <summary>
        /// TC-VE003-3: 생존자 2명 중 1명 사망 시 마지막 생존자가 즉시 승리한다.
        /// TR-VICT-009 검증.
        /// </summary>
        [Test]
        public void test_victoryManager_lastSurvivor_immediateVictoryForRemainingPlayer()
        {
            // Arrange — SetUp에서 A, B 스폰 완료

            // Act — B 사망
            _vm.SimulatePlayerDied(ClientB);

            // Assert
            Assert.IsTrue(_vm._victoryDeclared,
                "생존자 1명 조건에서 즉시 승리가 선언되어야 한다.");
            Assert.AreEqual(ClientA, _vm.LastVictory.winnerId,
                "마지막 생존자(A)가 승자여야 한다.");
            Assert.AreEqual(VictoryReason.LastSurvivor, _vm.LastVictory.reason,
                "승리 이유가 LastSurvivor여야 한다.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-VE003-4: 승리 후 중복 판정 방지
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("VictoryManager")]
    public class VictoryManagerDuplicatePreventionTests
    {
        private TestableVictoryManager _vm;
        private const ulong ClientA = 1UL;
        private const ulong ClientB = 2UL;
        private const ulong ClientC = 3UL;

        [SetUp]
        public void SetUp()
        {
            _vm = new TestableVictoryManager();
            _vm.SimulatePlayerSpawned(ClientA);
            _vm.SimulatePlayerSpawned(ClientB);
            _vm.SimulatePlayerSpawned(ClientC);
        }

        /// <summary>
        /// TC-VE003-4: 첫 번째 승리 선언 이후 추가 사망 이벤트가 발생해도
        /// DeclareVictory가 재호출되지 않는다.
        /// </summary>
        [Test]
        public void test_victoryManager_afterVictory_additionalDeathDoesNotRedeclare()
        {
            // Arrange — A가 게이지로 먼저 승리
            _vm.SimulateGaugeVictory(ClientA, VictoryReason.GaugeFull);
            Assert.AreEqual(1, _vm.DeclareVictoryCallCount, "사전 조건: 1회 승리 선언 완료.");

            // Act — 추가 사망 이벤트 발생
            _vm.SimulatePlayerDied(ClientB);
            _vm.SimulatePlayerDied(ClientC);

            // Assert — 승리 재선언 없음
            Assert.AreEqual(1, _vm.DeclareVictoryCallCount,
                "승리 선언은 정확히 1회만 발생해야 한다 (중복 방지).");
            Assert.AreEqual(1, _vm.AnnounceRpcCallCount,
                "AnnounceWinnerClientRpc도 정확히 1회만 호출되어야 한다.");
        }

        /// <summary>
        /// TC-VE003-4b: 세션 종료 이벤트도 이미 승리 선언 후에는 무시된다.
        /// </summary>
        [Test]
        public void test_victoryManager_afterVictory_sessionEndedDoesNotRedeclare()
        {
            // Arrange
            _vm._mockCumulative[ClientA] = 200f;
            _vm.SimulateGaugeVictory(ClientA, VictoryReason.GaugeFull);

            // Act
            _vm.SimulateSessionEnded();

            // Assert
            Assert.AreEqual(1, _vm.DeclareVictoryCallCount,
                "이미 승리 선언 후 세션 종료 이벤트는 무시되어야 한다.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-VE003-5: 서버 가드 — 클라이언트에서 승리 선언 불가
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("VictoryManager")]
    public class VictoryManagerServerGuardTests
    {
        private TestableVictoryManager _vm;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp()
        {
            _vm = new TestableVictoryManager { IsServer = false };
        }

        /// <summary>
        /// TC-VE003-5: IsServer=false일 때 DeclareVictory가 호출되어도 승리 선언이 발생하지 않는다.
        /// </summary>
        [Test]
        public void test_victoryManager_whenNotServer_declareVictoryDoesNothing()
        {
            // Arrange — SetUp에서 IsServer = false

            // Act
            _vm.DeclareVictory(ClientA, VictoryReason.GaugeFull);

            // Assert
            Assert.IsFalse(_vm._victoryDeclared,
                "클라이언트에서는 승리 선언이 되어서는 안 된다.");
            Assert.AreEqual(0, _vm.DeclareVictoryCallCount,
                "클라이언트에서는 DeclareVictory 카운터가 증가하면 안 된다.");
            Assert.IsFalse(_vm.GameOverTransitioned,
                "클라이언트에서는 GameOver 전환이 발생하면 안 된다.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-VE003-6: 동점 처리 — clientId 오름차순
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("VictoryManager")]
    public class VictoryManagerTieBreakTests
    {
        private TestableVictoryManager _vm;
        private const ulong ClientA = 1UL;
        private const ulong ClientB = 2UL;

        [SetUp]
        public void SetUp() => _vm = new TestableVictoryManager();

        /// <summary>
        /// TC-VE003-6: 동점 시 clientId 오름차순(낮은 값)이 승자가 된다.
        /// [TBD] GDD §5 — 동점 처리 정책 미확정, 향후 생존 시간 기준으로 변경 예정.
        /// </summary>
        [Test]
        public void test_victoryManager_tieBreak_lowerClientIdWins_TBD()
        {
            // Arrange — A(id=1)와 B(id=2) 동점
            _vm._mockCumulative[ClientA] = 100f;
            _vm._mockCumulative[ClientB] = 100f;

            // Act
            _vm.SimulateSessionEnded();

            // Assert — 낮은 clientId가 승자 (TBD: 생존 시간 기준으로 변경 예정)
            Assert.IsTrue(_vm._victoryDeclared,
                "동점에서도 승리가 선언되어야 한다.");
            Assert.AreEqual(ClientA, _vm.LastVictory.winnerId,
                "[TBD] 동점 시 clientId 오름차순(A=1)이 승자여야 한다. " +
                "향후 생존 시간 기준으로 정책 변경 예정.");
            Assert.AreEqual(VictoryReason.TimeUp, _vm.LastVictory.reason,
                "타임아웃으로 인한 승리 이유가 TimeUp이어야 한다.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-VE003-7: GameOver 전환
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("VictoryManager")]
    public class VictoryManagerGameOverTransitionTests
    {
        private TestableVictoryManager _vm;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp() => _vm = new TestableVictoryManager();

        /// <summary>
        /// TC-VE003-7: 승리 선언 시 GameStateManager.TransitionTo(GameOver)가 호출된다.
        /// 모든 승리 경로에서 GameOver 전환이 보장됨을 검증.
        /// </summary>
        [Test]
        public void test_victoryManager_onDeclareVictory_transitionsToGameOver()
        {
            // Arrange
            Assert.IsFalse(_vm.GameOverTransitioned,
                "사전 조건: GameOver 전환이 아직 발생하지 않아야 한다.");

            // Act
            _vm.DeclareVictory(ClientA, VictoryReason.GaugeFull);

            // Assert
            Assert.IsTrue(_vm.GameOverTransitioned,
                "DeclareVictory 호출 시 GameStateManager.TransitionTo(GameOver)가 호출되어야 한다.");
        }

        /// <summary>
        /// TC-VE003-7b: LastSurvivor 경로에서도 GameOver 전환이 발생한다.
        /// </summary>
        [Test]
        public void test_victoryManager_lastSurvivor_alsoTransitionsToGameOver()
        {
            // Arrange
            _vm.SimulatePlayerSpawned(ClientA);
            _vm.SimulatePlayerSpawned(2UL);

            // Act — 2번 플레이어 사망 → A가 마지막 생존자
            _vm.SimulatePlayerDied(2UL);

            // Assert
            Assert.IsTrue(_vm.GameOverTransitioned,
                "LastSurvivor 승리 경로에서도 GameOver 전환이 발생해야 한다.");
        }

        /// <summary>
        /// TC-VE003-7c: TimeUp 경로에서도 GameOver 전환이 발생한다.
        /// </summary>
        [Test]
        public void test_victoryManager_timeUp_alsoTransitionsToGameOver()
        {
            // Arrange
            _vm._mockCumulative[ClientA] = 50f;

            // Act
            _vm.SimulateSessionEnded();

            // Assert
            Assert.IsTrue(_vm.GameOverTransitioned,
                "TimeUp 승리 경로에서도 GameOver 전환이 발생해야 한다.");
        }
    }
}
