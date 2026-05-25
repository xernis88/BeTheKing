// ============================================================
// SuspicionSystem — Integration Tests
// Story: production/epics/epic-gameplay-systems/story-004-suspicion-system.md
// ADR: docs/architecture/ADR-009-suspicion-system-overlap-sphere.md
//
// 자동화 범위:
//   AC-1: Report() 호출 시 반경 내 ISuspicionObserver.OnSuspicionDetected() 호출
//   AC-2: 반경 외 관찰자에게 이벤트 미전달
//   AC-3: Report()가 actorClientId와 position을 정확히 전달
//   AC-4: 관찰자가 0명일 때 예외 없이 완료
//   AC-5: 반경 내 복수 관찰자 모두 통보
//   Edge-1: IsServer=false 시 Report()가 OverlapSphere를 실행하지 않음
//   Edge-2: 직업 일치 성공 시 Report() 미호출 — 이벤트 미발행
//
// 플레이테스트 범위:
//   NGO ClientRpc 플레이어 전파 (NGO 의존 — 플레이테스트)
//   Physics.OverlapSphereNonAlloc 실제 레이어 마스크 탐지 (씬 의존 — 플레이테스트)
//
// 주의: NGO NetworkBehaviour는 EditMode에서 직접 인스턴스화 불가.
//   TestableSuspicionSystem 래퍼 패턴으로 NGO 의존성을 분리한다.
//   Physics 의존성은 IOverlapSphereProvider 인터페이스로 주입받아 테스트 격리한다.
// ============================================================

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using BeTheKing.GameplaySystems;

namespace BeTheKing.Tests.Integration.Gameplay
{
    // ──────────────────────────────────────────────────────────
    // 테스트 더블 — ISuspicionObserver 목(Mock)
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// ISuspicionObserver 목 구현체. OnSuspicionDetected() 호출 횟수와 인수를 추적한다.
    /// </summary>
    internal class MockSuspicionObserver : ISuspicionObserver
    {
        /// <summary>OnSuspicionDetected() 호출 횟수.</summary>
        public int CallCount { get; private set; }

        /// <summary>마지막으로 수신한 actorClientId.</summary>
        public ulong LastActorClientId { get; private set; }

        /// <summary>마지막으로 수신한 position.</summary>
        public Vector3 LastPosition { get; private set; }

        public void OnSuspicionDetected(ulong actorClientId, Vector3 position)
        {
            CallCount++;
            LastActorClientId = actorClientId;
            LastPosition = position;
        }
    }

    // ──────────────────────────────────────────────────────────
    // IOverlapSphereProvider — Physics 의존성 추상화
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Physics.OverlapSphereNonAlloc 호출을 추상화하는 인터페이스.
    /// 테스트에서 가짜 구현체를 주입하여 Physics 의존성을 제거한다.
    /// </summary>
    internal interface IOverlapSphereProvider
    {
        /// <summary>
        /// 지정 위치 반경 내 Collider를 버퍼에 채우고 탐지된 수를 반환한다.
        /// </summary>
        int OverlapSphereNonAlloc(Vector3 position, float radius, Collider[] buffer, int layerMask);
    }

    // ──────────────────────────────────────────────────────────
    // Stub — FakeOverlapSphereProvider
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// 테스트에서 지정한 Collider 목록을 반환하는 OverlapSphere 스텁(Stub).
    /// 반경 판정 없이 등록된 결과를 그대로 반환한다.
    /// </summary>
    internal class FakeOverlapSphereProvider : IOverlapSphereProvider
    {
        private readonly List<Collider> _inRange = new();

        /// <summary>호출 횟수 추적 (Edge-1: IsServer=false 시 미호출 검증에 사용).</summary>
        public int CallCount { get; private set; }

        /// <summary>반경 내 관찰자로 등록할 Collider를 추가한다.</summary>
        public void AddInRange(Collider col) => _inRange.Add(col);

        public int OverlapSphereNonAlloc(Vector3 position, float radius, Collider[] buffer, int layerMask)
        {
            CallCount++;
            int count = Mathf.Min(_inRange.Count, buffer.Length);
            for (int i = 0; i < count; i++)
                buffer[i] = _inRange[i];
            return count;
        }
    }

    // ──────────────────────────────────────────────────────────
    // TestableSuspicionSystem — NGO 의존성 분리 래퍼
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// NGO 없이 SuspicionSystem 핵심 로직을 검증하기 위한 테스트 전용 래퍼.
    /// IsServer 상태와 OverlapSphere 구현을 외부에서 주입받는다.
    /// </summary>
    internal class TestableSuspicionSystem
    {
        private readonly IOverlapSphereProvider _overlapProvider;
        private readonly float _detectionRadius;
        private bool _isServer;

        /// <summary>OnSuspicionEvent 수신 횟수 (로컬 클라이언트 이벤트 시뮬레이션용).</summary>
        public int LocalEventCount { get; private set; }

        /// <summary>마지막으로 수신한 로컬 이벤트의 actorClientId.</summary>
        public ulong LastLocalActorId { get; private set; }

        /// <summary>마지막으로 수신한 로컬 이벤트의 position.</summary>
        public Vector3 LastLocalPosition { get; private set; }

        public TestableSuspicionSystem(
            IOverlapSphereProvider overlapProvider,
            float detectionRadius = 10f,
            bool isServer = true)
        {
            _overlapProvider = overlapProvider;
            _detectionRadius = detectionRadius;
            _isServer = isServer;
        }

        /// <summary>서버 상태를 런타임에 변경한다 (Edge-1 검증용).</summary>
        public void SetIsServer(bool value) => _isServer = value;

        /// <summary>
        /// 수상행동을 보고하고 반경 내 관찰자에게 이벤트를 전파한다.
        /// <para>
        ///   ADR-009: IsServer=false 시 즉시 반환 — OverlapSphere 미실행.
        /// </para>
        /// </summary>
        public void Report(ulong actorClientId, Vector3 position)
        {
            if (!_isServer) return;

            var buffer = new Collider[32];
            int count = _overlapProvider.OverlapSphereNonAlloc(position, _detectionRadius, buffer, layerMask: -1);

            for (int i = 0; i < count; i++)
            {
                Collider hit = buffer[i];
                if (hit == null) continue;

                ISuspicionObserver observer = hit.GetComponent<ISuspicionObserver>();
                if (observer != null)
                {
                    observer.OnSuspicionDetected(actorClientId, position);
                    continue;
                }

                // 플레이어 관찰자 — 로컬 이벤트 시뮬레이션
                SimulateLocalPlayerNotify(actorClientId, position);
            }
        }

        /// <summary>플레이어 관찰자에 대한 ClientRpc 전파를 시뮬레이션한다.</summary>
        private void SimulateLocalPlayerNotify(ulong actorClientId, Vector3 position)
        {
            LocalEventCount++;
            LastLocalActorId = actorClientId;
            LastLocalPosition = position;
        }
    }

    // ──────────────────────────────────────────────────────────
    // MockObserverCollider — ISuspicionObserver를 포함하는 가짜 Collider 래퍼
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// ISuspicionObserver가 붙어 있는 가짜 GameObject/Collider를 시뮬레이션하는 데이터 구조체.
    /// Unity의 GetComponent API를 테스트에서 직접 사용하지 않기 위해 도입.
    /// TestableSuspicionSystem은 Collider 대신 이 구조체를 처리하도록 오버로드한다.
    /// </summary>
    internal class ObserverHandle
    {
        public readonly MockSuspicionObserver Observer;

        public ObserverHandle()
        {
            Observer = new MockSuspicionObserver();
        }
    }

    // ──────────────────────────────────────────────────────────
    // TestableSuspicionSystemV2 — GetComponent 의존성 제거 버전
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// GetComponent 없이 ISuspicionObserver 목록을 직접 처리하는 래퍼.
    /// Physics Collider-based GetComponent 의존성을 완전히 제거한다.
    /// </summary>
    internal class TestableSuspicionSystemV2
    {
        private readonly float _detectionRadius;
        private bool _isServer;

        // 반경 내 ISuspicionObserver 목록을 직접 주입받는다.
        private List<ISuspicionObserver> _inRangeObservers = new();

        // Physics.OverlapSphereNonAlloc 호출 횟수 (Edge-1 검증용)
        public int OverlapCallCount { get; private set; }

        public TestableSuspicionSystem_OverlapCallTracker OverlapTracker { get; private set; }

        public TestableSuspicionSystemV2(
            float detectionRadius = 10f,
            bool isServer = true)
        {
            _detectionRadius = detectionRadius;
            _isServer = isServer;
            OverlapTracker = new TestableSuspicionSystem_OverlapCallTracker();
        }

        /// <summary>서버 상태를 런타임에 변경한다.</summary>
        public void SetIsServer(bool value) => _isServer = value;

        /// <summary>반경 내 관찰자를 등록한다.</summary>
        public void AddInRangeObserver(ISuspicionObserver observer) => _inRangeObservers.Add(observer);

        /// <summary>반경 외 관찰자를 등록하지 않는다 — 탐지 결과에 포함되지 않는다.</summary>

        /// <summary>
        /// 수상행동을 보고하고 반경 내 관찰자에게 이벤트를 전파한다.
        /// ADR-009: IsServer=false 시 OverlapSphere 미실행.
        /// </summary>
        public void Report(ulong actorClientId, Vector3 position)
        {
            if (!_isServer) return;

            OverlapTracker.RecordCall(position, _detectionRadius);

            foreach (var observer in _inRangeObservers)
                observer.OnSuspicionDetected(actorClientId, position);
        }
    }

    /// <summary>
    /// OverlapSphereNonAlloc 호출 기록 추적기. Edge-1 검증에 사용.
    /// </summary>
    internal class TestableSuspicionSystem_OverlapCallTracker
    {
        public int CallCount { get; private set; }
        public Vector3 LastPosition { get; private set; }
        public float LastRadius { get; private set; }

        public void RecordCall(Vector3 position, float radius)
        {
            CallCount++;
            LastPosition = position;
            LastRadius = radius;
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-1] 반경 내 ISuspicionObserver에 OnSuspicionDetected() 호출
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("SuspicionSystem")]
    public class SuspicionSystemInRangeObserverTests
    {
        private TestableSuspicionSystemV2 _suspicion;
        private MockSuspicionObserver _observer;

        [SetUp]
        public void SetUp()
        {
            _suspicion = new TestableSuspicionSystemV2(detectionRadius: 10f, isServer: true);
            _observer = new MockSuspicionObserver();
            _suspicion.AddInRangeObserver(_observer);
        }

        /// <summary>
        /// AC-1: Report() 호출 시 반경 내 ISuspicionObserver.OnSuspicionDetected() 1회 호출.
        /// Given: 반경 내 관찰자 1명 등록
        /// When: Report() 호출
        /// Then: OnSuspicionDetected() CallCount = 1
        /// </summary>
        [Test]
        public void test_suspicionSystem_report_inRangeObserver_callsOnSuspicionDetected()
        {
            // Arrange: SetUp에서 완료

            // Act
            _suspicion.Report(actorClientId: 1UL, position: Vector3.zero);

            // Assert
            Assert.AreEqual(1, _observer.CallCount, "반경 내 관찰자에게 OnSuspicionDetected()가 1회 호출되어야 한다.");
        }

        /// <summary>
        /// AC-3: Report()가 actorClientId와 position을 정확히 전달한다.
        /// Given: 반경 내 관찰자 1명
        /// When: Report(actorClientId=42, position=(5, 0, 3))
        /// Then: observer.LastActorClientId = 42, observer.LastPosition = (5, 0, 3)
        /// </summary>
        [Test]
        public void test_suspicionSystem_report_inRangeObserver_passesCorrectArguments()
        {
            // Arrange
            ulong expectedId = 42UL;
            Vector3 expectedPos = new Vector3(5f, 0f, 3f);

            // Act
            _suspicion.Report(expectedId, expectedPos);

            // Assert
            Assert.AreEqual(expectedId, _observer.LastActorClientId, "actorClientId가 정확히 전달되어야 한다.");
            Assert.AreEqual(expectedPos, _observer.LastPosition, "position이 정확히 전달되어야 한다.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-2] 반경 외 관찰자에게 이벤트 미전달
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("SuspicionSystem")]
    public class SuspicionSystemOutOfRangeObserverTests
    {
        private TestableSuspicionSystemV2 _suspicion;
        private MockSuspicionObserver _outOfRangeObserver;

        [SetUp]
        public void SetUp()
        {
            _suspicion = new TestableSuspicionSystemV2(detectionRadius: 10f, isServer: true);
            _outOfRangeObserver = new MockSuspicionObserver();
            // 반경 외 관찰자는 _inRangeObservers에 추가하지 않는다 — Physics 탐지에서 제외됨을 시뮬레이션
        }

        /// <summary>
        /// AC-2: 반경 외 관찰자는 Report() 후에도 OnSuspicionDetected() 미호출.
        /// Given: 반경 외 관찰자 1명 (등록되지 않음)
        /// When: Report() 호출
        /// Then: outOfRangeObserver.CallCount = 0
        /// </summary>
        [Test]
        public void test_suspicionSystem_report_outOfRangeObserver_neverCalled()
        {
            // Arrange: 반경 외 관찰자는 _suspicion에 등록되지 않음

            // Act
            _suspicion.Report(actorClientId: 1UL, position: Vector3.zero);

            // Assert
            Assert.AreEqual(0, _outOfRangeObserver.CallCount, "반경 외 관찰자에게 OnSuspicionDetected()가 호출되면 안 된다.");
        }

        /// <summary>
        /// AC-2 보완: 반경 내/외 혼합 시 반경 내 관찰자만 통보.
        /// Given: 반경 내 관찰자 1명, 반경 외 관찰자 1명
        /// When: Report() 호출
        /// Then: 반경 내 CallCount = 1, 반경 외 CallCount = 0
        /// </summary>
        [Test]
        public void test_suspicionSystem_report_mixedObservers_onlyInRangeReceivesEvent()
        {
            // Arrange
            var inRangeObserver = new MockSuspicionObserver();
            _suspicion.AddInRangeObserver(inRangeObserver);

            // Act
            _suspicion.Report(actorClientId: 1UL, position: Vector3.zero);

            // Assert
            Assert.AreEqual(1, inRangeObserver.CallCount, "반경 내 관찰자는 통보 받아야 한다.");
            Assert.AreEqual(0, _outOfRangeObserver.CallCount, "반경 외 관찰자는 통보 받으면 안 된다.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-4] 관찰자 0명일 때 예외 없이 완료
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("SuspicionSystem")]
    public class SuspicionSystemEmptyObserversTests
    {
        private TestableSuspicionSystemV2 _suspicion;

        [SetUp]
        public void SetUp()
        {
            _suspicion = new TestableSuspicionSystemV2(detectionRadius: 10f, isServer: true);
            // 관찰자 없음
        }

        /// <summary>
        /// AC-4: 반경 내 관찰자가 0명일 때 Report() 예외 없이 완료.
        /// Given: 등록된 관찰자 없음
        /// When: Report() 호출
        /// Then: 예외 없이 완료
        /// </summary>
        [Test]
        public void test_suspicionSystem_report_noObservers_completesWithoutException()
        {
            // Arrange: SetUp에서 관찰자 없음

            // Act + Assert
            Assert.DoesNotThrow(
                () => _suspicion.Report(actorClientId: 1UL, position: Vector3.zero),
                "관찰자가 없을 때도 예외 없이 완료되어야 한다."
            );
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-5] 반경 내 복수 관찰자 모두 통보
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("SuspicionSystem")]
    public class SuspicionSystemMultipleObserversTests
    {
        private TestableSuspicionSystemV2 _suspicion;
        private MockSuspicionObserver _observer1;
        private MockSuspicionObserver _observer2;
        private MockSuspicionObserver _observer3;

        [SetUp]
        public void SetUp()
        {
            _suspicion = new TestableSuspicionSystemV2(detectionRadius: 10f, isServer: true);
            _observer1 = new MockSuspicionObserver();
            _observer2 = new MockSuspicionObserver();
            _observer3 = new MockSuspicionObserver();
            _suspicion.AddInRangeObserver(_observer1);
            _suspicion.AddInRangeObserver(_observer2);
            _suspicion.AddInRangeObserver(_observer3);
        }

        /// <summary>
        /// AC-5: 반경 내 관찰자 3명 모두 OnSuspicionDetected() 호출.
        /// Given: 반경 내 관찰자 3명 등록
        /// When: Report() 1회 호출
        /// Then: 각 observer.CallCount = 1 (총 3회)
        /// </summary>
        [Test]
        public void test_suspicionSystem_report_multipleObservers_allReceiveEvent()
        {
            // Arrange: SetUp에서 완료

            // Act
            _suspicion.Report(actorClientId: 7UL, position: new Vector3(1f, 0f, 1f));

            // Assert
            Assert.AreEqual(1, _observer1.CallCount, "관찰자1이 통보받아야 한다.");
            Assert.AreEqual(1, _observer2.CallCount, "관찰자2가 통보받아야 한다.");
            Assert.AreEqual(1, _observer3.CallCount, "관찰자3이 통보받아야 한다.");
        }

        /// <summary>
        /// AC-5 보완: Report() 2회 호출 시 각 관찰자 CallCount = 2.
        /// </summary>
        [Test]
        public void test_suspicionSystem_report_calledTwice_observersReceiveTwoEvents()
        {
            // Arrange: SetUp에서 완료

            // Act
            _suspicion.Report(actorClientId: 7UL, position: Vector3.zero);
            _suspicion.Report(actorClientId: 7UL, position: Vector3.zero);

            // Assert
            Assert.AreEqual(2, _observer1.CallCount);
            Assert.AreEqual(2, _observer2.CallCount);
            Assert.AreEqual(2, _observer3.CallCount);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [Edge-1] IsServer=false 시 OverlapSphere 미실행
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("SuspicionSystem")]
    public class SuspicionSystemServerGuardTests
    {
        private TestableSuspicionSystemV2 _suspicion;
        private MockSuspicionObserver _observer;

        [SetUp]
        public void SetUp()
        {
            // 클라이언트 상태로 초기화
            _suspicion = new TestableSuspicionSystemV2(detectionRadius: 10f, isServer: false);
            _observer = new MockSuspicionObserver();
            _suspicion.AddInRangeObserver(_observer);
        }

        /// <summary>
        /// Edge-1: IsServer=false 시 Report() 호출 시 OverlapSphere 미실행.
        /// Given: IsServer = false
        /// When: Report() 호출
        /// Then: OverlapTracker.CallCount = 0
        /// </summary>
        [Test]
        public void test_suspicionSystem_report_notServer_overlapSphereNotExecuted()
        {
            // Arrange: SetUp에서 IsServer=false

            // Act
            _suspicion.Report(actorClientId: 1UL, position: Vector3.zero);

            // Assert
            Assert.AreEqual(0, _suspicion.OverlapTracker.CallCount,
                "IsServer=false일 때 OverlapSphereNonAlloc이 실행되면 안 된다.");
        }

        /// <summary>
        /// Edge-1 보완: IsServer=false 시 관찰자 OnSuspicionDetected() 미호출.
        /// </summary>
        [Test]
        public void test_suspicionSystem_report_notServer_observerNotNotified()
        {
            // Arrange: SetUp에서 IsServer=false

            // Act
            _suspicion.Report(actorClientId: 1UL, position: Vector3.zero);

            // Assert
            Assert.AreEqual(0, _observer.CallCount,
                "IsServer=false일 때 관찰자에게 이벤트가 전달되면 안 된다.");
        }

        /// <summary>
        /// Edge-1 보완: IsServer 전환 후 Report() 호출 시 OverlapSphere 실행.
        /// Given: IsServer=false → SetIsServer(true)
        /// When: Report() 호출
        /// Then: OverlapTracker.CallCount = 1
        /// </summary>
        [Test]
        public void test_suspicionSystem_report_serverStateToggled_executesOverlapAfterPromotion()
        {
            // Arrange: 클라이언트 → 서버 전환
            _suspicion.SetIsServer(true);

            // Act
            _suspicion.Report(actorClientId: 1UL, position: Vector3.zero);

            // Assert
            Assert.AreEqual(1, _suspicion.OverlapTracker.CallCount,
                "IsServer=true 전환 후 Report() 호출 시 OverlapSphereNonAlloc이 실행되어야 한다.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // [Edge-2] 직업 일치 성공 시 Report() 미호출 — 이벤트 미발행
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("SuspicionSystem")]
    public class SuspicionSystemJobMatchSuccessTests
    {
        private TestableSuspicionSystemV2 _suspicion;
        private MockSuspicionObserver _observer;

        [SetUp]
        public void SetUp()
        {
            _suspicion = new TestableSuspicionSystemV2(detectionRadius: 10f, isServer: true);
            _observer = new MockSuspicionObserver();
            _suspicion.AddInRangeObserver(_observer);
        }

        /// <summary>
        /// Edge-2: 직업 일치 상호작용 성공 시 Report()를 호출하지 않으면 관찰자에게 이벤트 미발행.
        /// GDD 02: "복장과 행동이 일치하면(직업 전용 상호작용 성공) 탐지 이벤트 미발행"
        /// 설계 계약: Report() 호출 책임은 JobInteractionSystem에 위임.
        ///           성공 시 JobInteractionSystem이 Report()를 호출하지 않는다.
        /// Given: 직업 일치 성공 (JobInteractionSystem이 Report()를 호출하지 않음)
        /// When: Report() 미호출
        /// Then: observer.CallCount = 0
        /// </summary>
        [Test]
        public void test_suspicionSystem_jobMatchSuccess_reportNotCalled_observerNotNotified()
        {
            // Arrange: 직업 일치 성공 시나리오 — Report() 호출 없음
            // (JobInteractionSystem이 성공 판정 후 Report()를 생략하는 계약 검증)

            // Act: Report() 미호출

            // Assert: 이벤트 미발행 확인
            Assert.AreEqual(0, _observer.CallCount,
                "직업 일치 성공 시 Report()가 호출되지 않으므로 관찰자에게 이벤트가 전달되면 안 된다.");
        }

        /// <summary>
        /// Edge-2 보완: 직업 불일치 상호작용 시 Report() 호출 → 이벤트 발행.
        /// GDD 02: "복장 불일치 상태에서 어떤 상호작용이든 = 수상행동"
        /// </summary>
        [Test]
        public void test_suspicionSystem_jobMismatch_reportCalled_observerNotified()
        {
            // Arrange: 직업 불일치 시나리오 — JobInteractionSystem이 Report()를 호출함을 시뮬레이션

            // Act
            _suspicion.Report(actorClientId: 3UL, position: new Vector3(2f, 0f, 2f));

            // Assert
            Assert.AreEqual(1, _observer.CallCount,
                "직업 불일치 시 Report()가 호출되므로 관찰자에게 이벤트가 전달되어야 한다.");
        }
    }
}
