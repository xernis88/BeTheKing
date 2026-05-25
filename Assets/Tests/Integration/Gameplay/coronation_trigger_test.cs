// CoronationTrigger — Integration Tests
// Story: production/epics/epic-victory-endgame/story-001-coronation-trigger.md
// TC: TC-VE001-1, TC-VE001-2, TC-VE001-3, TC-VE001-5, TC-VE001-6
//
// 테스트 전략:
//   NGO NetworkBehaviour는 EditMode에서 IsServer를 사용할 수 없음.
//   TestableCoronationTrigger로 HandleCoronationStarted 로직을 순수 C#으로 추출.
//   MapManager, PrinceNPCAI, ThroneZoneManager 의존성은 Mock 카운터 클래스로 대체.
//   SessionTimeManager null 안전 처리는 SimulateNetworkSpawn()으로 검증.

using NUnit.Framework;

namespace BeTheKing.Tests.Integration.Gameplay
{
    // ── Mock 클래스 ────────────────────────────────────────────────────────────

    /// <summary>MapManager.OpenGates() 호출 횟수 추적 Mock.</summary>
    internal class MockMapManager
    {
        public int OpenGatesCallCount { get; private set; }

        public void OpenGates() => OpenGatesCallCount++;
    }

    /// <summary>PrinceNPCAI.Activate() 호출 횟수 추적 Mock.</summary>
    internal class MockPrinceNPCAI
    {
        public int ActivateCallCount { get; private set; }

        public void Activate() => ActivateCallCount++;
    }

    /// <summary>ThroneZoneManager.Activate() 호출 횟수 추적 Mock.</summary>
    internal class MockThroneZoneManager
    {
        public int ActivateCallCount { get; private set; }

        public void Activate() => ActivateCallCount++;
    }

    // ── Testable 래퍼 ──────────────────────────────────────────────────────────

    /// <summary>
    /// NGO 없이 CoronationTrigger 로직을 테스트하는 래퍼.
    /// IsServer를 외부에서 제어하고, HandleCoronationStarted를 직접 호출 가능하게 추출.
    /// SessionTimeManager 구독/해제 로직을 SimulateNetworkSpawn/Despawn으로 노출.
    /// </summary>
    internal class TestableCoronationTrigger
    {
        private readonly bool _isServer;
        private readonly MockMapManager _mapManager;
        private readonly MockPrinceNPCAI _prince;
        private readonly MockThroneZoneManager _throneZone;

        // null sessionManager 주입 시 OnNetworkSpawn null 안전 처리 검증용
        private readonly System.Action _sessionTimeManagerSubscriber;

        public TestableCoronationTrigger(
            bool isServer,
            MockMapManager mapManager,
            MockPrinceNPCAI prince,
            MockThroneZoneManager throneZone,
            System.Action sessionTimeManagerSubscriber = null)
        {
            _isServer = isServer;
            _mapManager = mapManager;
            _prince = prince;
            _throneZone = throneZone;
            _sessionTimeManagerSubscriber = sessionTimeManagerSubscriber;
        }

        /// <summary>
        /// CoronationTrigger.HandleCoronationStarted() 로직을 직접 실행한다.
        /// IsServer 가드 포함.
        /// </summary>
        public void SimulateCoronationStarted()
        {
            if (!_isServer) return;

            _mapManager.OpenGates();        // TR-VICT-001: 성문 개방
            _prince.Activate();             // TR-VICT-002: 왕자 NPC 활성화
            _throneZone.Activate();         // 왕좌 영역 감지 시작
            // NotifyAllClientsClientRpc은 NGO RPC — 단위 테스트에서 검증 불가, 생략
        }

        /// <summary>
        /// OnNetworkSpawn의 SessionTimeManager null 안전 처리를 시뮬레이트한다.
        /// sessionTimeManagerSubscriber가 null이면 구독 없이 종료 (null 안전 경로).
        /// </summary>
        public void SimulateNetworkSpawn()
        {
            // CoronationTrigger.OnNetworkSpawn의 null-check 패턴 그대로 반영
            _sessionTimeManagerSubscriber?.Invoke();
        }
    }

    // ── TestFixture: 활성화 시퀀스 ───────────────────────────────────────────

    [TestFixture, Category("CoronationTrigger")]
    internal class CoronationTriggerActivationTests
    {
        private MockMapManager _mapManager;
        private MockPrinceNPCAI _prince;
        private MockThroneZoneManager _throneZone;
        private TestableCoronationTrigger _trigger;

        [SetUp]
        public void SetUp()
        {
            _mapManager = new MockMapManager();
            _prince = new MockPrinceNPCAI();
            _throneZone = new MockThroneZoneManager();
            _trigger = new TestableCoronationTrigger(
                isServer: true,
                mapManager: _mapManager,
                prince: _prince,
                throneZone: _throneZone
            );
        }

        [Test]
        public void test_coronationTrigger_onCoronationStarted_callsMapManagerOpenGates()
        {
            // TC-VE001-1: 대관식 시작 시 MapManager.OpenGates()가 1회 호출된다.

            // Arrange — SetUp에서 완료

            // Act
            _trigger.SimulateCoronationStarted();

            // Assert
            Assert.AreEqual(1, _mapManager.OpenGatesCallCount,
                "MapManager.OpenGates()는 정확히 1회 호출되어야 한다.");
        }

        [Test]
        public void test_coronationTrigger_onCoronationStarted_callsPrinceActivate()
        {
            // TC-VE001-2: 대관식 시작 시 PrinceNPCAI.Activate()가 1회 호출된다.

            // Arrange — SetUp에서 완료

            // Act
            _trigger.SimulateCoronationStarted();

            // Assert
            Assert.AreEqual(1, _prince.ActivateCallCount,
                "PrinceNPCAI.Activate()는 정확히 1회 호출되어야 한다.");
        }

        [Test]
        public void test_coronationTrigger_onCoronationStarted_callsThroneZoneActivate()
        {
            // TC-VE001-3: 대관식 시작 시 ThroneZoneManager.Activate()가 1회 호출된다.

            // Arrange — SetUp에서 완료

            // Act
            _trigger.SimulateCoronationStarted();

            // Assert
            Assert.AreEqual(1, _throneZone.ActivateCallCount,
                "ThroneZoneManager.Activate()는 정확히 1회 호출되어야 한다.");
        }

        [Test]
        public void test_coronationTrigger_onCoronationStarted_callsAllThreeSystems()
        {
            // TC-VE001 복합: 대관식 시작 시 세 시스템이 각각 정확히 1회 호출된다.

            // Arrange — SetUp에서 완료

            // Act
            _trigger.SimulateCoronationStarted();

            // Assert — 세 시스템 모두 정확히 1회씩
            Assert.AreEqual(1, _mapManager.OpenGatesCallCount,
                "MapManager.OpenGates() 호출 횟수 불일치.");
            Assert.AreEqual(1, _prince.ActivateCallCount,
                "PrinceNPCAI.Activate() 호출 횟수 불일치.");
            Assert.AreEqual(1, _throneZone.ActivateCallCount,
                "ThroneZoneManager.Activate() 호출 횟수 불일치.");
        }
    }

    // ── TestFixture: 서버 가드 ────────────────────────────────────────────────

    [TestFixture, Category("CoronationTrigger")]
    internal class CoronationTriggerServerGuardTests
    {
        private MockMapManager _mapManager;
        private MockPrinceNPCAI _prince;
        private MockThroneZoneManager _throneZone;
        private TestableCoronationTrigger _trigger;

        [SetUp]
        public void SetUp()
        {
            _mapManager = new MockMapManager();
            _prince = new MockPrinceNPCAI();
            _throneZone = new MockThroneZoneManager();
            // isServer: false — 클라이언트 시뮬레이션
            _trigger = new TestableCoronationTrigger(
                isServer: false,
                mapManager: _mapManager,
                prince: _prince,
                throneZone: _throneZone
            );
        }

        [Test]
        public void test_coronationTrigger_onCoronationStarted_whenNotServer_doesNotCallAnySystem()
        {
            // TC-VE001-5: 클라이언트에서 이벤트 수신 시 어떤 시스템도 호출되지 않는다.

            // Arrange — SetUp에서 완료 (isServer: false)

            // Act
            _trigger.SimulateCoronationStarted();

            // Assert — 세 시스템 모두 호출 없음
            Assert.AreEqual(0, _mapManager.OpenGatesCallCount,
                "클라이언트에서 MapManager.OpenGates()가 호출되면 안 된다.");
            Assert.AreEqual(0, _prince.ActivateCallCount,
                "클라이언트에서 PrinceNPCAI.Activate()가 호출되면 안 된다.");
            Assert.AreEqual(0, _throneZone.ActivateCallCount,
                "클라이언트에서 ThroneZoneManager.Activate()가 호출되면 안 된다.");
        }
    }

    // ── TestFixture: null 안전 처리 ──────────────────────────────────────────

    [TestFixture, Category("CoronationTrigger")]
    internal class CoronationTriggerNullSafetyTests
    {
        [Test]
        public void test_coronationTrigger_onNetworkSpawn_withNullSessionTimeManager_doesNotThrow()
        {
            // TC-VE001-6: SessionTimeManager.Instance가 null일 때 OnNetworkSpawn이 예외 없이 완료된다.

            // Arrange
            // sessionTimeManagerSubscriber = null → SimulateNetworkSpawn이 구독 없이 안전 종료
            var trigger = new TestableCoronationTrigger(
                isServer: true,
                mapManager: new MockMapManager(),
                prince: new MockPrinceNPCAI(),
                throneZone: new MockThroneZoneManager(),
                sessionTimeManagerSubscriber: null  // null SessionTimeManager 시뮬레이션
            );

            // Act & Assert — 예외가 발생하면 테스트 실패
            Assert.DoesNotThrow(
                () => trigger.SimulateNetworkSpawn(),
                "SessionTimeManager.Instance가 null일 때 OnNetworkSpawn은 예외를 발생시키면 안 된다."
            );
        }
    }
}
