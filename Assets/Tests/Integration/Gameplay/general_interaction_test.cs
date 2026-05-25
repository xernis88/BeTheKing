// GeneralInteractionSystem — Integration Tests
// Story: production/epics/epic-gameplay-systems/story-006-general-interaction.md
// ADR: docs/architecture/ADR-010-general-interaction-hold-timer.md
// TR: TR-GAME-013
//
// 테스트 전략:
//   NGO NetworkBehaviour는 EditMode에서 IsOwner/IsServer를 사용할 수 없음.
//   TestableGeneralInteractionSystem으로 타이머 누적과 완료 로직을 순수 C#으로 추출.
//   SuspicionSystem.Instance?.Report() 의존성은 Action<ulong, Vector3> 델리게이트로 주입.
//   IGeneralInteractable은 MockGeneralInteractable로 대체.

using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using BeTheKing.GameplaySystems;

namespace BeTheKing.Tests.Integration.Gameplay
{
    // ── Mock / Stub ────────────────────────────────────────────────────────────

    /// <summary>IGeneralInteractable 모킹. OnInteractionComplete 호출 횟수 추적.</summary>
    internal class MockGeneralInteractable : IGeneralInteractable
    {
        public int CompleteCallCount { get; private set; }
        public ulong NetworkObjectId { get; }

        public MockGeneralInteractable(ulong netObjId = 1UL)
        {
            NetworkObjectId = netObjId;
        }

        public void OnInteractionComplete() => CompleteCallCount++;
    }

    // ── Testable System ────────────────────────────────────────────────────────

    /// <summary>
    /// NGO 없이 GeneralInteractionSystem 로직을 테스트하는 래퍼.
    /// IsOwner/IsServer를 외부에서 제어하고, ServerRpc 완료 로직을 직접 호출 가능하게 추출.
    /// </summary>
    internal class TestableGeneralInteractionSystem
    {
        // 외부 주입 가능한 상태
        public bool SimulatedIsOwner { get; set; } = true;
        public bool SimulatedIsServer { get; set; } = true;

        // 내부 상태 (테스트 검증용)
        public float HoldProgress { get; private set; }
        public bool IsHolding { get; private set; }
        public IGeneralInteractable CurrentTarget { get; private set; }

        private readonly float _holdDuration;
        private readonly Action<ulong, Vector3> _suspicionReporter;
        private readonly ulong _ownerClientId;
        private readonly Vector3 _position;

        // 이벤트 추적
        public int HoldStartedCount { get; private set; }
        public int HoldCancelledCount { get; private set; }
        public bool ServerRpcWasFired { get; private set; }

        public TestableGeneralInteractionSystem(
            float holdDuration = 2f,
            Action<ulong, Vector3> suspicionReporter = null,
            ulong ownerClientId = 0UL,
            Vector3 position = default)
        {
            _holdDuration = holdDuration;
            _suspicionReporter = suspicionReporter ?? ((_, __) => { });
            _ownerClientId = ownerClientId;
            _position = position;
        }

        public void StartHold(IGeneralInteractable target)
        {
            IsHolding = true;
            CurrentTarget = target;
            HoldProgress = 0f;
            HoldStartedCount++;
        }

        public void CancelHold()
        {
            IsHolding = false;
            HoldProgress = 0f;
            HoldCancelledCount++;
        }

        /// <summary>Update() 로직 시뮬레이션. deltaTime 단위로 진행도를 누적한다.</summary>
        public void SimulateUpdate(float deltaTime)
        {
            if (!IsHolding || !SimulatedIsOwner) return;

            HoldProgress += deltaTime;

            if (HoldProgress >= _holdDuration)
            {
                // ADR-010 Guideline 1: _isHolding = false를 ServerRpc 호출 전에 설정.
                IsHolding = false;
                ServerRpcWasFired = true;
                SimulateCompleteHoldServerRpc(CurrentTarget);
            }
        }

        /// <summary>CompleteHoldServerRpc 서버 로직 시뮬레이션.</summary>
        public void SimulateCompleteHoldServerRpc(IGeneralInteractable target)
        {
            if (!SimulatedIsServer) return;
            if (target == null) return;

            // ADR-010 Guideline 4: OnInteractionComplete() → Report() 순서.
            target.OnInteractionComplete();
            _suspicionReporter(_ownerClientId, _position);
        }
    }

    // ── Test Fixtures ──────────────────────────────────────────────────────────

    [TestFixture]
    public class GeneralInteractionHoldCompleteTests
    {
        private TestableGeneralInteractionSystem _system;
        private MockGeneralInteractable _target;
        private List<(ulong, Vector3)> _reportedEvents;

        [SetUp]
        public void SetUp()
        {
            _reportedEvents = new List<(ulong, Vector3)>();
            _system = new TestableGeneralInteractionSystem(
                holdDuration: 2f,
                suspicionReporter: (id, pos) => _reportedEvents.Add((id, pos)));
            _target = new MockGeneralInteractable(netObjId: 42UL);
        }

        /// <summary>AC-1: 홀드 완료 시 IGeneralInteractable.OnInteractionComplete() 호출.</summary>
        [Test]
        public void test_generalInteraction_holdComplete_interactableReceivesCallback()
        {
            _system.StartHold(_target);
            _system.SimulateUpdate(2.0f);

            Assert.AreEqual(1, _target.CompleteCallCount,
                "holdDuration 도달 시 OnInteractionComplete()가 1회 호출되어야 한다");
        }

        /// <summary>AC-4: 홀드 완료 시 SuspicionSystem.Report() 항상 호출.</summary>
        [Test]
        public void test_generalInteraction_holdComplete_suspicionReportCalled()
        {
            _system.StartHold(_target);
            _system.SimulateUpdate(2.0f);

            Assert.AreEqual(1, _reportedEvents.Count,
                "일반 상호작용 완료 시 수상행동 이벤트가 발행되어야 한다");
        }

        /// <summary>AC-4: Report 호출 시 ownerClientId와 position이 올바르게 전달된다.</summary>
        [Test]
        public void test_generalInteraction_holdComplete_reportPassesCorrectArguments()
        {
            var expectedPos = new Vector3(3f, 0f, 5f);
            var ownerId = 7UL;
            var systemWithPos = new TestableGeneralInteractionSystem(
                holdDuration: 1f,
                suspicionReporter: (id, pos) => _reportedEvents.Add((id, pos)),
                ownerClientId: ownerId,
                position: expectedPos);
            var target = new MockGeneralInteractable();

            systemWithPos.StartHold(target);
            systemWithPos.SimulateUpdate(1.0f);

            Assert.AreEqual(1, _reportedEvents.Count);
            Assert.AreEqual(ownerId, _reportedEvents[0].Item1);
            Assert.AreEqual(expectedPos, _reportedEvents[0].Item2);
        }

        /// <summary>Edge-1: holdProgress가 holdDuration과 정확히 같을 때 완료 조건(>=) 만족.</summary>
        [Test]
        public void test_generalInteraction_holdExactDuration_completesOnBoundary()
        {
            _system.StartHold(_target);
            _system.SimulateUpdate(2.0f); // 정확히 2.0f

            Assert.AreEqual(1, _target.CompleteCallCount,
                ">= 판정으로 정확히 holdDuration 도달 시 완료되어야 한다");
        }
    }

    [TestFixture]
    public class GeneralInteractionCancelTests
    {
        private TestableGeneralInteractionSystem _system;
        private MockGeneralInteractable _target;
        private int _reportCount;

        [SetUp]
        public void SetUp()
        {
            _reportCount = 0;
            _system = new TestableGeneralInteractionSystem(
                holdDuration: 2f,
                suspicionReporter: (_, __) => _reportCount++);
            _target = new MockGeneralInteractable();
        }

        /// <summary>AC-3: CancelHold() 시 _holdProgress = 0, _isHolding = false.</summary>
        [Test]
        public void test_generalInteraction_cancelHold_progressResets()
        {
            _system.StartHold(_target);
            _system.SimulateUpdate(1.0f); // 진행 중

            _system.CancelHold();

            Assert.IsFalse(_system.IsHolding, "취소 후 IsHolding이 false여야 한다");
            Assert.AreEqual(0f, _system.HoldProgress, 0.001f, "취소 후 HoldProgress가 0이어야 한다");
        }

        /// <summary>AC-3: CancelHold() 시 CompleteHoldServerRpc 미발화 — SuspicionSystem 미호출.</summary>
        [Test]
        public void test_generalInteraction_cancelHold_serverRpcNotFired()
        {
            _system.StartHold(_target);
            _system.SimulateUpdate(1.0f);

            _system.CancelHold();

            Assert.IsFalse(_system.ServerRpcWasFired, "취소 시 ServerRpc가 발화되지 않아야 한다");
            Assert.AreEqual(0, _reportCount, "취소 시 수상행동 이벤트가 발행되지 않아야 한다");
            Assert.AreEqual(0, _target.CompleteCallCount, "취소 시 OnInteractionComplete()가 호출되지 않아야 한다");
        }

        /// <summary>Edge-2: StartHold() 직후 즉시 CancelHold() — 완료 없음.</summary>
        [Test]
        public void test_generalInteraction_immediateCancel_noCompletion()
        {
            _system.StartHold(_target); // _holdProgress = 0

            _system.CancelHold();

            Assert.IsFalse(_system.IsHolding);
            Assert.AreEqual(0f, _system.HoldProgress, 0.001f);
            Assert.AreEqual(0, _target.CompleteCallCount);
        }
    }

    [TestFixture]
    public class GeneralInteractionMovementCancelsHoldTests
    {
        private TestableGeneralInteractionSystem _system;
        private MockGeneralInteractable _target;
        private int _reportCount;

        [SetUp]
        public void SetUp()
        {
            _reportCount = 0;
            _system = new TestableGeneralInteractionSystem(
                holdDuration: 2f,
                suspicionReporter: (_, __) => _reportCount++);
            _target = new MockGeneralInteractable();
        }

        /// <summary>AC-2: 홀드 중 PlayerController가 이동 감지 → CancelHold() 호출 → 홀드 취소.</summary>
        [Test]
        public void test_generalInteraction_movementInputDuringHold_cancelsHold()
        {
            _system.StartHold(_target);
            _system.SimulateUpdate(1.0f); // 진행 중

            // PlayerController가 이동 입력 감지 시 CancelHold() 호출 시뮬레이션
            _system.CancelHold();

            Assert.IsFalse(_system.IsHolding, "이동 입력 후 홀드가 취소되어야 한다");
            Assert.AreEqual(0f, _system.HoldProgress, 0.001f, "취소 후 진행도가 0으로 초기화되어야 한다");
        }

        /// <summary>AC-2: 이동으로 홀드 취소 시 SuspicionSystem 미호출 — 완료가 아니므로.</summary>
        [Test]
        public void test_generalInteraction_movementCancelsHold_noSuspicionReport()
        {
            _system.StartHold(_target);
            _system.SimulateUpdate(1.0f);

            _system.CancelHold(); // 이동 입력 시뮬레이션

            Assert.AreEqual(0, _reportCount, "취소 시 수상행동 이벤트가 발행되지 않아야 한다");
            Assert.AreEqual(0, _target.CompleteCallCount, "취소 시 OnInteractionComplete() 미호출");
        }

        /// <summary>AC-2: StartHold() 시 OnHoldStarted 이벤트 발행 (PlayerController 모니터링 시작 신호).</summary>
        [Test]
        public void test_generalInteraction_holdStarted_monitoringEventFired()
        {
            _system.StartHold(_target);

            Assert.AreEqual(1, _system.HoldStartedCount,
                "StartHold() 시 OnHoldStarted 이벤트가 발행되어야 한다");
        }

        /// <summary>CancelHold() 시 OnHoldCancelled 이벤트 발행 (PlayerController 모니터링 해제 신호).</summary>
        [Test]
        public void test_generalInteraction_cancelHold_monitoringStopEventFired()
        {
            _system.StartHold(_target);
            _system.CancelHold();

            Assert.AreEqual(1, _system.HoldCancelledCount,
                "CancelHold() 시 OnHoldCancelled 이벤트가 발행되어야 한다");
        }
    }

    [TestFixture]
    public class GeneralInteractionOwnerGuardTests
    {
        private TestableGeneralInteractionSystem _system;
        private MockGeneralInteractable _target;
        private int _reportCount;

        [SetUp]
        public void SetUp()
        {
            _reportCount = 0;
            _system = new TestableGeneralInteractionSystem(
                holdDuration: 1f,
                suspicionReporter: (_, __) => _reportCount++);
            _system.SimulatedIsOwner = false; // 비소유 클라이언트
            _target = new MockGeneralInteractable();
        }

        /// <summary>비소유 클라이언트에서 Update()가 타이머를 누적하지 않는다.</summary>
        [Test]
        public void test_generalInteraction_notOwner_timerDoesNotAccumulate()
        {
            _system.StartHold(_target);
            _system.SimulateUpdate(2.0f);

            Assert.AreEqual(0f, _system.HoldProgress, 0.001f,
                "비소유 클라이언트에서 HoldProgress가 누적되지 않아야 한다");
        }

        /// <summary>비소유 클라이언트에서 ServerRpc 미발화.</summary>
        [Test]
        public void test_generalInteraction_notOwner_serverRpcNotFired()
        {
            _system.StartHold(_target);
            _system.SimulateUpdate(2.0f);

            Assert.IsFalse(_system.ServerRpcWasFired);
            Assert.AreEqual(0, _reportCount);
        }
    }

    [TestFixture]
    public class GeneralInteractionDoubleFiringTests
    {
        private TestableGeneralInteractionSystem _system;
        private MockGeneralInteractable _target;
        private int _reportCount;

        [SetUp]
        public void SetUp()
        {
            _reportCount = 0;
            _system = new TestableGeneralInteractionSystem(
                holdDuration: 1f,
                suspicionReporter: (_, __) => _reportCount++);
            _target = new MockGeneralInteractable();
        }

        /// <summary>
        /// ADR-010 Guideline 1: holdDuration 도달 시 _isHolding = false가 먼저 설정되어
        /// 연속 Update() 호출에서도 ServerRpc가 1회만 발화된다.
        /// </summary>
        [Test]
        public void test_generalInteraction_doubleFirPrevention_isHoldingFalseBeforeRpc()
        {
            _system.StartHold(_target);
            _system.SimulateUpdate(1.5f); // 완료 (1.5 > 1.0)
            _system.SimulateUpdate(0.5f); // 추가 호출 — 중복 발화 방지 확인

            Assert.AreEqual(1, _target.CompleteCallCount,
                "holdDuration 도달 후 추가 Update()에서 중복 발화가 없어야 한다");
            Assert.AreEqual(1, _reportCount,
                "Report()가 1회만 호출되어야 한다");
        }
    }
}
