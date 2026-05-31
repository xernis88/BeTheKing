// ============================================================
// Static Event Dispatch — Unit Tests
// Story: Sprint 4 / TECH-002
// Target: RoyalGaugeSystem.OnGaugeSyncedToClient + VictoryManager.OnVictoryAnnounced
//
// 자동화 범위: static event 발행 인자 검증, null-safe 호출, 복수 구독자,
//              기존 게이지 로직 비간섭 확인
// 플레이테스트 범위: NGO 환경에서 실제 ClientRpc → 이벤트 체인 (NGO 의존)
// ============================================================

using NUnit.Framework;
using BeTheKing.GameplaySystems;

namespace BeTheKing.Tests.Unit.Gameplay
{
    // ──────────────────────────────────────────────────────────
    // TC-TECH002-1: SyncGaugeClientRpc → OnGaugeSyncedToClient 인자 검증
    // TC-TECH002-4a: 구독자 없을 때 NullReferenceException 없음
    // TC-TECH002-5: 복수 구독자 — 모두 수신
    // TC-TECH002-6: 이벤트 추가 후 기존 게이지 로직 비간섭
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("StaticEventDispatch")]
    public class GaugeSyncedEventTests
    {
        // 수신 기록 — 각 테스트에서 초기화
        private ulong _lastClientId;
        private float _lastGauge;
        private float _lastCumulative;
        private int   _callCount;

        private void CaptureGaugeSync(ulong id, float g, float c)
        {
            _lastClientId   = id;
            _lastGauge      = g;
            _lastCumulative = c;
            _callCount++;
        }

        [SetUp]
        public void SetUp()
        {
            _lastClientId = 0; _lastGauge = 0f; _lastCumulative = 0f; _callCount = 0;
        }

        [TearDown]
        public void TearDown()
        {
            RoyalGaugeSystem.OnGaugeSyncedToClient -= CaptureGaugeSync;
        }

        /// <summary>
        /// TC-TECH002-1: RaiseOnGaugeSyncedToClient(7, 30f, 87f) 호출 시
        /// OnGaugeSyncedToClient가 동일 인자로 발행된다.
        /// </summary>
        [Test]
        public void test_gaugeSynced_raiseInvoked_eventFiredWithSameArgs()
        {
            // Arrange
            RoyalGaugeSystem.OnGaugeSyncedToClient += CaptureGaugeSync;

            // Act
            RoyalGaugeSystem.RaiseOnGaugeSyncedToClient(clientId: 7, gauge: 30f, cumulative: 87f);

            // Assert
            Assert.AreEqual(7UL,  _lastClientId,   "clientId가 그대로 전달되어야 한다.");
            Assert.AreEqual(30f,  _lastGauge,       "gauge가 그대로 전달되어야 한다.");
            Assert.AreEqual(87f,  _lastCumulative,  "cumulative가 그대로 전달되어야 한다.");
            Assert.AreEqual(1,    _callCount,       "이벤트는 정확히 1회 발행되어야 한다.");
        }

        /// <summary>
        /// TC-TECH002-4a: 구독자 0명일 때 RaiseOnGaugeSyncedToClient 호출 시
        /// NullReferenceException이 발생하지 않는다.
        /// </summary>
        [Test]
        public void test_gaugeSynced_noSubscribers_doesNotThrow()
        {
            // Arrange: CaptureGaugeSync 미등록 — 구독자 없음

            // Act + Assert
            Assert.DoesNotThrow(
                () => RoyalGaugeSystem.RaiseOnGaugeSyncedToClient(1, 50f, 100f),
                "구독자가 없어도 null-conditional invoke로 예외 없이 실행되어야 한다.");
        }

        /// <summary>
        /// TC-TECH002-5: 구독자 3개 등록 시 모든 구독자가 이벤트를 수신한다.
        /// </summary>
        [Test]
        public void test_gaugeSynced_multipleSubscribers_allReceive()
        {
            // Arrange
            int receivedCount = 0;
            void Sub1(ulong _, float __, float ___) => receivedCount++;
            void Sub2(ulong _, float __, float ___) => receivedCount++;
            void Sub3(ulong _, float __, float ___) => receivedCount++;

            RoyalGaugeSystem.OnGaugeSyncedToClient += Sub1;
            RoyalGaugeSystem.OnGaugeSyncedToClient += Sub2;
            RoyalGaugeSystem.OnGaugeSyncedToClient += Sub3;

            try
            {
                // Act
                RoyalGaugeSystem.RaiseOnGaugeSyncedToClient(3, 60f, 120f);

                // Assert
                Assert.AreEqual(3, receivedCount, "3개 구독자 모두 이벤트를 수신해야 한다.");
            }
            finally
            {
                RoyalGaugeSystem.OnGaugeSyncedToClient -= Sub1;
                RoyalGaugeSystem.OnGaugeSyncedToClient -= Sub2;
                RoyalGaugeSystem.OnGaugeSyncedToClient -= Sub3;
            }
        }

        /// <summary>
        /// TC-TECH002-6: static event 추가 후 TestableRoyalGaugeSystem의
        /// 게이지 상승(Tick) 로직이 영향받지 않는다.
        /// </summary>
        [Test]
        public void test_gaugeSynced_staticEventAddition_doesNotAffectGaugeLogic()
        {
            // Arrange
            var gauge = new TestableRoyalGaugeSystem();
            const ulong ClientA = 1UL;
            gauge.OnPlayerEnter(ClientA);
            RoyalGaugeSystem.OnGaugeSyncedToClient += CaptureGaugeSync;

            // Act — Tick 10초 (TestableRoyalGaugeSystem은 RaiseOnGaugeSyncedToClient 미호출)
            gauge.Tick(10f);

            // Assert — 게이지 값은 10으로 정상 상승, static event 구독은 간섭 없음
            Assert.AreEqual(10f, gauge._gauges[ClientA], 0.01f,
                "static event 추가 후에도 게이지 상승 로직은 동일하게 동작해야 한다.");
            // TestableRoyalGaugeSystem은 프로덕션 RaiseOnGaugeSyncedToClient를 호출하지 않으므로 이벤트 미발행 확인
            Assert.AreEqual(0, _callCount,
                "TestableRoyalGaugeSystem.Tick은 static event를 발행하지 않아야 한다.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-TECH002-2: AnnounceWinnerClientRpc(GaugeFull) → OnVictoryAnnounced
    // TC-TECH002-3: AnnounceWinnerClientRpc(TimeUp) → OnVictoryAnnounced
    // TC-TECH002-4b: 구독자 없을 때 NullReferenceException 없음
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("StaticEventDispatch")]
    public class VictoryAnnouncedEventTests
    {
        private ulong         _lastWinnerId;
        private VictoryReason _lastReason;
        private int           _callCount;

        private void CaptureVictory(ulong id, VictoryReason r)
        {
            _lastWinnerId = id;
            _lastReason   = r;
            _callCount++;
        }

        [SetUp]
        public void SetUp()
        {
            _lastWinnerId = 0; _lastReason = default; _callCount = 0;
        }

        [TearDown]
        public void TearDown()
        {
            VictoryManager.OnVictoryAnnounced -= CaptureVictory;
        }

        /// <summary>
        /// TC-TECH002-2: RaiseOnVictoryAnnounced(5, GaugeFull) 호출 시
        /// OnVictoryAnnounced가 winnerId=5, reason=GaugeFull로 발행된다.
        /// </summary>
        [Test]
        public void test_victoryAnnounced_gaugeFull_eventFiredWithCorrectArgs()
        {
            // Arrange
            VictoryManager.OnVictoryAnnounced += CaptureVictory;

            // Act
            VictoryManager.RaiseOnVictoryAnnounced(winnerId: 5, VictoryReason.GaugeFull);

            // Assert
            Assert.AreEqual(5UL,                    _lastWinnerId, "winnerId가 그대로 전달되어야 한다.");
            Assert.AreEqual(VictoryReason.GaugeFull, _lastReason,  "reason이 GaugeFull이어야 한다.");
            Assert.AreEqual(1,                      _callCount,    "이벤트는 정확히 1회 발행되어야 한다.");
        }

        /// <summary>
        /// TC-TECH002-3: RaiseOnVictoryAnnounced(2, TimeUp) 호출 시
        /// OnVictoryAnnounced가 winnerId=2, reason=TimeUp으로 발행된다.
        /// </summary>
        [Test]
        public void test_victoryAnnounced_timeUp_eventFiredWithCorrectArgs()
        {
            // Arrange
            VictoryManager.OnVictoryAnnounced += CaptureVictory;

            // Act
            VictoryManager.RaiseOnVictoryAnnounced(winnerId: 2, VictoryReason.TimeUp);

            // Assert
            Assert.AreEqual(2UL,                 _lastWinnerId, "winnerId가 그대로 전달되어야 한다.");
            Assert.AreEqual(VictoryReason.TimeUp, _lastReason,  "reason이 TimeUp이어야 한다.");
        }

        /// <summary>
        /// TC-TECH002-4b: 구독자 0명일 때 RaiseOnVictoryAnnounced 호출 시
        /// NullReferenceException이 발생하지 않는다.
        /// </summary>
        [Test]
        public void test_victoryAnnounced_noSubscribers_doesNotThrow()
        {
            // Arrange: CaptureVictory 미등록 — 구독자 없음

            // Act + Assert
            Assert.DoesNotThrow(
                () => VictoryManager.RaiseOnVictoryAnnounced(0, VictoryReason.LastSurvivor),
                "구독자가 없어도 null-conditional invoke로 예외 없이 실행되어야 한다.");
        }
    }
}
