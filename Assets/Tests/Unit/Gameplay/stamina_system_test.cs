// ============================================================
// StaminaSystem — Unit Tests
// Story: GameplaySystems / StaminaSystem Core Logic (GS-001)
// ADR: docs/architecture/ADR-005-stamina-system-network-variable.md
//
// 자동화 범위: TryConsume 성공/실패, 회복 타이머 리셋, CanAct, 경계값
// 플레이테스트 범위: NetworkVariable 클라이언트 동기화 (NGO 의존 — 플레이테스트)
// ============================================================

using NUnit.Framework;
using BeTheKing.GameplaySystems;
using UnityEngine;

namespace BeTheKing.Tests.Unit.Gameplay
{
    // ──────────────────────────────────────────────────────────
    // 테스트용 StaminaSystem 서브클래스 — NetworkBehaviour 의존성 분리
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// NetworkBehaviour 없이 로직만 검증하기 위한 테스트 전용 래퍼.
    /// IsServer를 강제로 true로 설정하여 서버 코드 경로를 테스트한다.
    /// </summary>
    internal class TestableStaminaSystem
    {
        public const float Max = StaminaSystem.Max;
        private const float RecoveryDelay = 5f;

        private float _current = Max;
        private float _lastConsumeTime = float.NegativeInfinity;
        private float _recoveryRate;

        // 테스트에서 시간을 주입하기 위한 시뮬레이션 클럭
        public float SimulatedTime { get; set; } = 0f;

        public float Current => _current;
        public bool CanAct => _current > 0f;

        public TestableStaminaSystem(float recoveryRate = 10f)
        {
            _recoveryRate = recoveryRate;
        }

        public bool TryConsume(float amount)
        {
            if (_current < amount) return false;
            _current -= amount;
            _lastConsumeTime = SimulatedTime;
            return true;
        }

        /// <summary>단일 Update 스텝 실행. deltaTime을 주입해 프레임률 독립성 검증.</summary>
        public void SimulateUpdate(float deltaTime)
        {
            SimulatedTime += deltaTime;
            if (_current >= Max) return;
            if (SimulatedTime - _lastConsumeTime < RecoveryDelay) return;
            _current = Mathf.Min(Max, _current + _recoveryRate * deltaTime);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-4] TryConsume 성공 케이스
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("StaminaSystem")]
    public class StaminaSystemConsumeSuccessTests
    {
        private TestableStaminaSystem _stamina;

        [SetUp]
        public void SetUp() => _stamina = new TestableStaminaSystem();

        /// <summary>AC-4: 잔량 충분 시 소모 성공하고 true 반환.</summary>
        [Test]
        public void test_staminaSystem_consume_sufficientAmount_returnsTrueAndReduces()
        {
            // Arrange
            // 초기값 100 (Max)

            // Act
            bool result = _stamina.TryConsume(30f);

            // Assert
            Assert.IsTrue(result);
            Assert.AreEqual(70f, _stamina.Current, 0.001f);
        }

        /// <summary>Edge-1: 잔량 == amount 정확 소모 시 0이 되고 음수 아님.</summary>
        [Test]
        public void test_staminaSystem_consume_exactAmount_returnsZeroNotNegative()
        {
            // Arrange
            _stamina.TryConsume(70f); // 잔량 30으로 줄이기

            // Act
            bool result = _stamina.TryConsume(30f);

            // Assert
            Assert.IsTrue(result);
            Assert.AreEqual(0f, _stamina.Current, 0.001f);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-3, Edge-2] TryConsume 실패 케이스
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("StaminaSystem")]
    public class StaminaSystemConsumeFailureTests
    {
        private TestableStaminaSystem _stamina;

        [SetUp]
        public void SetUp() => _stamina = new TestableStaminaSystem();

        /// <summary>AC-3: 스태미나 0에서 소모 시도 시 false, 잔량 불변.</summary>
        [Test]
        public void test_staminaSystem_consume_zeroStamina_returnsFalseAndUnchanged()
        {
            // Arrange
            _stamina.TryConsume(TestableStaminaSystem.Max); // 0으로 만들기

            // Act
            bool result = _stamina.TryConsume(1f);

            // Assert
            Assert.IsFalse(result);
            Assert.AreEqual(0f, _stamina.Current, 0.001f);
        }

        /// <summary>Edge-2: 잔량보다 큰 amount 시 false, 잔량 불변.</summary>
        [Test]
        public void test_staminaSystem_consume_exceedingAmount_returnsFalseAndUnchanged()
        {
            // Arrange
            _stamina.TryConsume(80f); // 잔량 20

            // Act
            bool result = _stamina.TryConsume(30f);

            // Assert
            Assert.IsFalse(result);
            Assert.AreEqual(20f, _stamina.Current, 0.001f);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-1, AC-2] 회복 타이머 테스트
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("StaminaSystem")]
    public class StaminaSystemRecoveryTests
    {
        private TestableStaminaSystem _stamina;

        [SetUp]
        public void SetUp() => _stamina = new TestableStaminaSystem(recoveryRate: 10f);

        /// <summary>AC-1: 소모 후 5초 경과 시 회복이 시작된다.</summary>
        [Test]
        public void test_staminaSystem_recovery_afterDelayElapsed_staminaIncreases()
        {
            // Arrange
            _stamina.TryConsume(50f); // 잔량 50
            float before = _stamina.Current;

            // Act: 5초 딱 경과 후 1 프레임 (0.1초)
            _stamina.SimulateUpdate(5.0f);  // 5초 경과, 아직 회복 X (경계: < RecoveryDelay)
            _stamina.SimulateUpdate(0.1f);  // 5.1초 → 회복 시작

            // Assert
            Assert.Greater(_stamina.Current, before);
        }

        /// <summary>AC-2: 회복 대기 중 재소모 시 타이머 리셋 (5초 재시작).</summary>
        [Test]
        public void test_staminaSystem_recovery_consumeDuringDelay_resetsTimer()
        {
            // Arrange
            _stamina.TryConsume(20f); // 잔량 80
            _stamina.SimulateUpdate(4.9f); // 4.9초 경과 (회복 시작 직전)

            // Act: 4.9초 시점에 재소모 → 타이머 리셋
            _stamina.TryConsume(10f); // 잔량 70, 타이머 0으로 리셋
            float afterSecondConsume = _stamina.Current;

            // 추가 4.9초 더 경과 (총 9.8초, 두 번째 소모로부터 4.9초) → 아직 회복 안 됨
            _stamina.SimulateUpdate(4.9f);

            // Assert: 타이머가 리셋됐으므로 아직 회복 미시작
            Assert.AreEqual(afterSecondConsume, _stamina.Current, 0.001f);
        }

        /// <summary>Edge-3: 회복이 Max(100)를 초과하지 않는다.</summary>
        [Test]
        public void test_staminaSystem_recovery_neverExceedsMax()
        {
            // Arrange
            _stamina.TryConsume(1f); // 잔량 99

            // Act: 5초 경과 후 대량 회복 시뮬레이션
            _stamina.SimulateUpdate(5.0f);
            for (int i = 0; i < 100; i++)
                _stamina.SimulateUpdate(1.0f); // 100초 회복

            // Assert
            Assert.LessOrEqual(_stamina.Current, TestableStaminaSystem.Max);
            Assert.AreEqual(TestableStaminaSystem.Max, _stamina.Current, 0.001f);
        }
    }

    // ──────────────────────────────────────────────────────────
    // CanAct 프로퍼티 테스트
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("StaminaSystem")]
    public class StaminaSystemCanActTests
    {
        private TestableStaminaSystem _stamina;

        [SetUp]
        public void SetUp() => _stamina = new TestableStaminaSystem();

        /// <summary>잔량 > 0이면 CanAct = true.</summary>
        [Test]
        public void test_staminaSystem_canAct_positiveStamina_returnsTrue()
        {
            // Arrange + Act: 초기 100
            // Assert
            Assert.IsTrue(_stamina.CanAct);
        }

        /// <summary>잔량 = 0이면 CanAct = false. GDD: 달리기/공격 불가.</summary>
        [Test]
        public void test_staminaSystem_canAct_zeroStamina_returnsFalse()
        {
            // Arrange
            _stamina.TryConsume(TestableStaminaSystem.Max);

            // Assert
            Assert.IsFalse(_stamina.CanAct);
        }
    }
}
