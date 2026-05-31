// ============================================================
// CollapsedState — Unit Tests
// Story: production/epics/epic-gameplay-systems/story-009-collapsed-state.md
// ADR: docs/architecture/ADR-005-stamina-system-network-variable.md
//
// 자동화 범위: Collapsed 진입/탈출, 자력 회복 타이머, 도움 회복, 소모 차단
// 플레이테스트 범위: NetworkVariable IsCollapsed 클라이언트 동기화 (NGO 의존 — 플레이테스트)
// ============================================================

using NUnit.Framework;
using System;
using UnityEngine;

namespace BeTheKing.Tests.Unit.Gameplay
{
    // ──────────────────────────────────────────────────────────
    // 테스트용 CollapsedState 래퍼 — NetworkBehaviour 의존성 분리
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// NetworkBehaviour 없이 CollapsedState 로직만 검증하기 위한 테스트 전용 래퍼.
    /// 시간 주입(SetTime)으로 서버 Update 타이머 로직을 시뮬레이션한다.
    /// </summary>
    internal class TestableCollapsedStaminaSystem
    {
        public const float Max = 100f;
        private const float RecoveryDelay = 5f;

        public float Current { get; private set; } = Max;
        public bool IsCollapsed { get; private set; }

        private float _collapsedEntryTime;
        private float _lastConsumeTime = float.NegativeInfinity;
        private readonly float _selfRecoveryTime;
        private readonly float _collapsedStaminaRestore;
        private readonly float _helpStaminaRestore;
        private readonly int _helpFameReward;
        private readonly float _recoveryRate;
        private float _currentTime = 0f;

        public TestableCollapsedStaminaSystem(
            float selfRecoveryTime = 10f,
            float collapsedRestore = 30f,
            float helpRestore = 60f,
            int helpFame = 10,
            float recoveryRate = 10f)
        {
            _selfRecoveryTime = selfRecoveryTime;
            _collapsedStaminaRestore = collapsedRestore;
            _helpStaminaRestore = helpRestore;
            _helpFameReward = helpFame;
            _recoveryRate = recoveryRate;
        }

        /// <summary>시뮬레이션 클럭을 절대 시각으로 설정.</summary>
        public void SetTime(float t) => _currentTime = t;

        public bool TryConsume(float amount)
        {
            if (IsCollapsed) return false;
            if (Current < amount) return false;

            Current -= amount;
            _lastConsumeTime = _currentTime;

            if (Current <= 0f)
                EnterCollapsed();

            return true;
        }

        /// <summary>단일 Update 스텝. deltaTime을 주입해 프레임률 독립성 검증.</summary>
        public void SimulateUpdate(float deltaTime)
        {
            _currentTime += deltaTime;

            if (IsCollapsed)
            {
                if (_currentTime - _collapsedEntryTime >= _selfRecoveryTime)
                    ExitCollapsed(_collapsedStaminaRestore);
                return;
            }

            if (Current >= Max) return;
            if (_currentTime - _lastConsumeTime < RecoveryDelay) return;
            Current = Mathf.Min(Max, Current + _recoveryRate * deltaTime);
        }

        /// <summary>도움 완료 시 호출. onGainFame에 지급 명성값 전달.</summary>
        public void OnHelpComplete(Action<int> onGainFame = null)
        {
            if (!IsCollapsed) return;
            onGainFame?.Invoke(_helpFameReward);
            ExitCollapsed(_helpStaminaRestore);
        }

        public bool CanAct => Current > 0f && !IsCollapsed;

        private void EnterCollapsed()
        {
            IsCollapsed = true;
            _collapsedEntryTime = _currentTime;
        }

        private void ExitCollapsed(float staminaRestore)
        {
            IsCollapsed = false;
            Current = Mathf.Min(Max, staminaRestore);
            _lastConsumeTime = _currentTime; // Collapsed 탈출 후 즉시 자연회복 방지
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-1] 스태미나 0 → Collapsed 진입
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("CollapsedState")]
    public class CollapsedStateEntryTests
    {
        private TestableCollapsedStaminaSystem _stamina;

        [SetUp]
        public void SetUp() => _stamina = new TestableCollapsedStaminaSystem();

        /// <summary>AC-1: 스태미나를 정확히 0으로 소모하면 IsCollapsed=true.</summary>
        [Test]
        public void test_collapsedState_consume_exactlyToZero_entersCollapsed()
        {
            // Arrange
            // 초기값 100

            // Act
            _stamina.TryConsume(100f);

            // Assert
            Assert.IsTrue(_stamina.IsCollapsed);
            Assert.AreEqual(0f, _stamina.Current, 0.001f);
        }

        /// <summary>AC-1: Collapsed 진입 후 TryConsume은 false 반환 (소모 차단).</summary>
        [Test]
        public void test_collapsedState_consume_whileCollapsed_returnsFalse()
        {
            // Arrange
            _stamina.TryConsume(100f); // Collapsed 진입

            // Act
            bool result = _stamina.TryConsume(1f);

            // Assert
            Assert.IsFalse(result);
            Assert.AreEqual(0f, _stamina.Current, 0.001f);
        }

        /// <summary>Edge-1: 잔량 부족으로 Collapsed 미진입 상태에서도 TryConsume false.</summary>
        [Test]
        public void test_collapsedState_consume_insufficientStamina_notCollapsed()
        {
            // Arrange
            _stamina.TryConsume(80f); // 잔량 20

            // Act
            bool result = _stamina.TryConsume(30f);

            // Assert
            Assert.IsFalse(result);
            Assert.IsFalse(_stamina.IsCollapsed);
            Assert.AreEqual(20f, _stamina.Current, 0.001f);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-2] 자력 회복 타이머
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("CollapsedState")]
    public class CollapsedStateSelfRecoveryTests
    {
        private TestableCollapsedStaminaSystem _stamina;

        [SetUp]
        public void SetUp() => _stamina = new TestableCollapsedStaminaSystem(selfRecoveryTime: 10f, collapsedRestore: 30f);

        /// <summary>AC-2: 자력 회복 시간(10s) 경과 전 — 여전히 Collapsed.</summary>
        [Test]
        public void test_collapsedState_selfRecovery_beforeTimeElapsed_staysCollapsed()
        {
            // Arrange
            _stamina.TryConsume(100f); // Collapsed 진입

            // Act: 9.9초 경과 (미달)
            _stamina.SimulateUpdate(9.9f);

            // Assert
            Assert.IsTrue(_stamina.IsCollapsed);
        }

        /// <summary>AC-2: 자력 회복 시간(10s) 경과 후 — IsCollapsed=false, 스태미나 30 복구.</summary>
        [Test]
        public void test_collapsedState_selfRecovery_afterTimeElapsed_exitsCollapsed()
        {
            // Arrange
            _stamina.TryConsume(100f); // Collapsed 진입

            // Act: 10초 + 1프레임
            _stamina.SimulateUpdate(10.0f);
            _stamina.SimulateUpdate(0.1f);

            // Assert
            Assert.IsFalse(_stamina.IsCollapsed);
            Assert.AreEqual(30f, _stamina.Current, 0.001f);
        }

        /// <summary>AC-4: Collapsed 중 자연회복 차단 — 10초 내 스태미나 0 유지.</summary>
        [Test]
        public void test_collapsedState_naturalRecovery_whileCollapsed_isBlocked()
        {
            // Arrange
            _stamina.TryConsume(100f); // Collapsed 진입

            // Act: 자력 회복 시간 미달로 여러 프레임 경과
            for (int i = 0; i < 50; i++)
                _stamina.SimulateUpdate(0.1f); // 5초 경과

            // Assert: 자연회복 없음, 스태미나 여전히 0
            Assert.IsTrue(_stamina.IsCollapsed);
            Assert.AreEqual(0f, _stamina.Current, 0.001f);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-3] 도움 회복
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("CollapsedState")]
    public class CollapsedStateHelpRecoveryTests
    {
        private TestableCollapsedStaminaSystem _stamina;

        [SetUp]
        public void SetUp() => _stamina = new TestableCollapsedStaminaSystem(helpRestore: 60f, helpFame: 10);

        /// <summary>AC-3: OnHelpComplete → IsCollapsed=false, 스태미나 60 복구.</summary>
        [Test]
        public void test_collapsedState_helpComplete_exitsCollapsedWithHigherRestore()
        {
            // Arrange
            _stamina.TryConsume(100f); // Collapsed 진입

            // Act
            _stamina.OnHelpComplete();

            // Assert
            Assert.IsFalse(_stamina.IsCollapsed);
            Assert.AreEqual(60f, _stamina.Current, 0.001f);
        }

        /// <summary>AC-3: OnHelpComplete → GainFame(10) 호출 확인.</summary>
        [Test]
        public void test_collapsedState_helpComplete_invokesGainFameWithCorrectAmount()
        {
            // Arrange
            _stamina.TryConsume(100f); // Collapsed 진입
            int capturedFame = -1;

            // Act
            _stamina.OnHelpComplete(fame => capturedFame = fame);

            // Assert
            Assert.AreEqual(10, capturedFame);
        }

        /// <summary>Edge-2: OnHelpComplete 후 TryConsume true (행동 가능 복귀).</summary>
        [Test]
        public void test_collapsedState_helpComplete_thenConsume_returnsTrue()
        {
            // Arrange
            _stamina.TryConsume(100f); // Collapsed 진입
            _stamina.OnHelpComplete(); // 도움으로 탈출, 잔량 60

            // Act
            bool result = _stamina.TryConsume(20f);

            // Assert
            Assert.IsTrue(result);
            Assert.AreEqual(40f, _stamina.Current, 0.001f);
        }

        /// <summary>AC-3: Collapsed 아닌 상태에서 OnHelpComplete 호출 시 무시됨.</summary>
        [Test]
        public void test_collapsedState_helpComplete_notCollapsed_isIgnored()
        {
            // Arrange
            // IsCollapsed=false (초기 상태)
            int fameCallCount = 0;

            // Act
            _stamina.OnHelpComplete(_ => fameCallCount++);

            // Assert
            Assert.IsFalse(_stamina.IsCollapsed);
            Assert.AreEqual(0, fameCallCount);
            Assert.AreEqual(100f, _stamina.Current, 0.001f);
        }
    }

    // ──────────────────────────────────────────────────────────
    // CanAct — Collapsed 반영
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("CollapsedState")]
    public class CollapsedStateCanActTests
    {
        private TestableCollapsedStaminaSystem _stamina;

        [SetUp]
        public void SetUp() => _stamina = new TestableCollapsedStaminaSystem();

        /// <summary>Collapsed 상태이면 CanAct=false (스태미나가 복구돼도 Collapsed 중은 행동 불가).</summary>
        [Test]
        public void test_collapsedState_canAct_whileCollapsed_returnsFalse()
        {
            // Arrange
            _stamina.TryConsume(100f); // Collapsed 진입

            // Assert
            Assert.IsFalse(_stamina.CanAct);
        }

        /// <summary>Collapsed 탈출 후 잔량 > 0이면 CanAct=true.</summary>
        [Test]
        public void test_collapsedState_canAct_afterExitCollapsed_returnsTrue()
        {
            // Arrange
            _stamina.TryConsume(100f); // Collapsed 진입
            _stamina.OnHelpComplete(); // 도움으로 탈출

            // Assert
            Assert.IsTrue(_stamina.CanAct);
        }
    }
}
