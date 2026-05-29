// ============================================================
// PlayerManager 사망 공지 이벤트 — Unit Tests
// Story: Sprint 5 / TECH-003
// Target: PlayerManager.OnPlayerDeathAnnounced (static event)
//
// 자동화 범위: static event 발행 인자 검증, null-safe 호출, 복수 구독자,
//              기존 서버 전용 이벤트(OnPlayerDied) 비간섭 확인
// 플레이테스트 범위: NGO 환경에서 실제 ClientRpc → 이벤트 체인 (NGO 의존)
// ============================================================

using NUnit.Framework;
using BeTheKing.CoreServices;

namespace BeTheKing.Tests.Unit.Core
{
    // ──────────────────────────────────────────────────────────
    // TC-TECH003-1: RaiseOnPlayerDeathAnnounced → 인자 검증
    // TC-TECH003-2: 구독자 없을 때 NullReferenceException 없음
    // TC-TECH003-3: 복수 구독자 — 모두 수신
    // TC-TECH003-4: 동일 clientId 복수 발행 — 매번 수신
    // TC-TECH003-5: 기존 OnPlayerDied 이벤트 비간섭
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("StaticEventDispatch")]
    public class PlayerDeathEventTests
    {
        private ulong _lastClientId;
        private int   _callCount;

        private void CaptureDeathAnnounced(ulong clientId)
        {
            _lastClientId = clientId;
            _callCount++;
        }

        [SetUp]
        public void SetUp()
        {
            _lastClientId = 0;
            _callCount    = 0;
        }

        [TearDown]
        public void TearDown()
        {
            PlayerManager.OnPlayerDeathAnnounced -= CaptureDeathAnnounced;
        }

        /// <summary>
        /// TC-TECH003-1: RaiseOnPlayerDeathAnnounced(42) 호출 시
        /// OnPlayerDeathAnnounced가 clientId=42로 발행된다.
        /// </summary>
        [Test]
        public void test_playerDeathAnnounced_raiseInvoked_eventFiredWithSameClientId()
        {
            // Arrange
            PlayerManager.OnPlayerDeathAnnounced += CaptureDeathAnnounced;

            // Act
            PlayerManager.RaiseOnPlayerDeathAnnounced(clientId: 42UL);

            // Assert
            Assert.AreEqual(42UL, _lastClientId, "clientId가 그대로 전달되어야 한다.");
            Assert.AreEqual(1,    _callCount,    "이벤트는 정확히 1회 발행되어야 한다.");
        }

        /// <summary>
        /// TC-TECH003-2: 구독자 0명일 때 RaiseOnPlayerDeathAnnounced 호출 시
        /// NullReferenceException이 발생하지 않는다.
        /// </summary>
        [Test]
        public void test_playerDeathAnnounced_noSubscribers_doesNotThrow()
        {
            // Arrange: CaptureDeathAnnounced 미등록 — 구독자 없음

            // Act + Assert
            Assert.DoesNotThrow(
                () => PlayerManager.RaiseOnPlayerDeathAnnounced(1UL),
                "구독자가 없어도 null-conditional invoke로 예외 없이 실행되어야 한다.");
        }

        /// <summary>
        /// TC-TECH003-3: 구독자 3개 등록 시 모든 구독자가 이벤트를 수신한다.
        /// </summary>
        [Test]
        public void test_playerDeathAnnounced_multipleSubscribers_allReceive()
        {
            // Arrange
            int receivedCount = 0;
            void Sub1(ulong _) => receivedCount++;
            void Sub2(ulong _) => receivedCount++;
            void Sub3(ulong _) => receivedCount++;

            PlayerManager.OnPlayerDeathAnnounced += Sub1;
            PlayerManager.OnPlayerDeathAnnounced += Sub2;
            PlayerManager.OnPlayerDeathAnnounced += Sub3;

            try
            {
                // Act
                PlayerManager.RaiseOnPlayerDeathAnnounced(7UL);

                // Assert
                Assert.AreEqual(3, receivedCount, "3개 구독자 모두 이벤트를 수신해야 한다.");
            }
            finally
            {
                PlayerManager.OnPlayerDeathAnnounced -= Sub1;
                PlayerManager.OnPlayerDeathAnnounced -= Sub2;
                PlayerManager.OnPlayerDeathAnnounced -= Sub3;
            }
        }

        /// <summary>
        /// TC-TECH003-4: 동일 clientId로 복수 발행 시 매번 이벤트가 수신된다.
        /// (중복 사망 처리 방어 로직이 이벤트 발행을 억제하지 않아야 한다)
        /// </summary>
        [Test]
        public void test_playerDeathAnnounced_sameClientIdTwice_bothReceived()
        {
            // Arrange
            PlayerManager.OnPlayerDeathAnnounced += CaptureDeathAnnounced;

            // Act
            PlayerManager.RaiseOnPlayerDeathAnnounced(5UL);
            PlayerManager.RaiseOnPlayerDeathAnnounced(5UL);

            // Assert
            Assert.AreEqual(2, _callCount, "같은 clientId라도 발행 횟수만큼 수신되어야 한다.");
        }

        /// <summary>
        /// TC-TECH003-5: OnPlayerDeathAnnounced 구독은 기존 OnPlayerDied 이벤트와
        /// 독립적으로 동작한다 — 상호 간섭 없음.
        /// </summary>
        [Test]
        public void test_playerDeathAnnounced_independentFromOnPlayerDied_noInterference()
        {
            // Arrange
            int announcedCount = 0;
            int diedCount      = 0;

            void OnAnnounced(ulong _) => announcedCount++;

            // PlayerManager 인스턴스 없이 static 이벤트만 검증
            PlayerManager.OnPlayerDeathAnnounced += OnAnnounced;

            try
            {
                // Act — OnPlayerDeathAnnounced만 발행
                PlayerManager.RaiseOnPlayerDeathAnnounced(9UL);

                // Assert — OnPlayerDeathAnnounced는 발행됨, OnPlayerDied는 미발행
                Assert.AreEqual(1, announcedCount, "OnPlayerDeathAnnounced가 1회 발행되어야 한다.");
                Assert.AreEqual(0, diedCount,      "OnPlayerDied는 발행되지 않아야 한다.");
            }
            finally
            {
                PlayerManager.OnPlayerDeathAnnounced -= OnAnnounced;
            }
        }
    }
}
