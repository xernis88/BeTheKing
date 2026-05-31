// TheftSystem — Unit Tests
// Story: production/epics/epic-gameplay-systems/story-010-theft-system.md
//
// 자동화 범위:
//   AC-1: detectionChance=0 → Success, LoseFame 미호출
//   AC-2: detectionChance=1 → Detected, LoseFame 호출
//   AC-3: 쿨타임 미경과 → OnCooldown
//   AC-4: 쿨타임 경과 후 재시도 가능
//   AC-5: IsServer 가드 — NetworkBehaviour.IsServer 의존으로 플레이테스트 범위 (주석 설명)
//   Edge-1: 복수 도둑 독립 쿨타임
//
// 플레이테스트 범위: NGO IsServer 가드 (NetworkBehaviour.IsServer 의존)

using NUnit.Framework;
using System;
using System.Collections.Generic;
using BeTheKing.GameplaySystems;

namespace BeTheKing.Tests.Unit.Gameplay
{
    // ──────────────────────────────────────────────────────────
    // NGO 의존성을 제거한 순수 C# 래퍼
    // NetworkBehaviour.IsServer가 EditMode에서 항상 false이므로
    // 서버 로직만 추출하여 검증한다. (fame_system_test.cs 동일 패턴)
    //
    // AC-5 (IsServer 가드) 는 NetworkBehaviour.IsServer 를 직접 제어할 수 없어
    // 자동화 불가. 플레이테스트에서 클라이언트 호출 시 default(TheftResult.Success)
    // 가 반환되는지 확인한다.
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// TheftSystem의 핵심 로직을 NGO 없이 검증하기 위한 테스트 전용 래퍼.
    /// Time.time 의존성을 <see cref="SetTime"/>으로 주입하여 결정론적으로 실행된다.
    /// </summary>
    internal class TestableTheftSystem
    {
        private readonly float _theftCooldown;
        private readonly float _detectionChance;
        private readonly int _famePenalty;
        private readonly Dictionary<ulong, float> _lastAttemptTime = new();
        private float _currentTime = 0f;

        /// <param name="theftCooldown">쿨타임(초). 기본 5초.</param>
        /// <param name="detectionChance">발각 확률. 0 = 항상 성공, 1 = 항상 발각.</param>
        /// <param name="famePenalty">발각 시 명성치 패널티.</param>
        public TestableTheftSystem(
            float theftCooldown = 5f,
            float detectionChance = 0.4f,
            int famePenalty = 15)
        {
            _theftCooldown = theftCooldown;
            _detectionChance = detectionChance;
            _famePenalty = famePenalty;
        }

        /// <summary>테스트에서 현재 시각을 주입한다 (Time.time 대체).</summary>
        public void SetTime(float time) => _currentTime = time;

        /// <summary>
        /// 도둑질을 시도한다.
        /// </summary>
        /// <param name="thieverId">도둑 ID.</param>
        /// <param name="targetId">대상 ID.</param>
        /// <param name="onDetected">발각 시 호출할 콜백 (clientId, famePenalty). null 허용.</param>
        /// <returns>도둑질 결과.</returns>
        public TheftResult AttemptTheft(
            ulong thieverId,
            ulong targetId,
            Action<ulong, int> onDetected = null)
        {
            // 쿨타임 체크
            float lastAttempt = _lastAttemptTime.GetValueOrDefault(thieverId, float.NegativeInfinity);
            if (_currentTime - lastAttempt < _theftCooldown)
            {
                return TheftResult.OnCooldown;
            }

            // 쿨타임 갱신
            _lastAttemptTime[thieverId] = _currentTime;

            // 발각 여부 판정 (detectionChance 직접 비교 — 결정론적)
            // Random.value 대신 임계값 비교: chance >= 1f → 항상 발각, chance <= 0f → 항상 성공
            if (_detectionChance >= 1f)
            {
                onDetected?.Invoke(thieverId, _famePenalty);
                return TheftResult.Detected;
            }

            return TheftResult.Success;
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-1] detectionChance=0 → 항상 Success, LoseFame 미호출
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("TheftSystem")]
    public class TheftSystemSuccessTests
    {
        private TestableTheftSystem _theft;
        private int _loseFameCallCount;

        private const ulong Thief = 1UL;
        private const ulong Target = 2UL;

        [SetUp]
        public void SetUp()
        {
            _theft = new TestableTheftSystem(detectionChance: 0f);
            _loseFameCallCount = 0;
        }

        /// <summary>AC-1: detectionChance=0일 때 AttemptTheft는 Success를 반환한다.</summary>
        [Test]
        public void test_theftSystem_attemptTheft_detectionChanceZero_returnsSuccess()
        {
            // Arrange
            // detectionChance = 0 (항상 성공)

            // Act
            TheftResult result = _theft.AttemptTheft(Thief, Target,
                onDetected: (id, penalty) => _loseFameCallCount++);

            // Assert
            Assert.AreEqual(TheftResult.Success, result);
        }

        /// <summary>AC-1: detectionChance=0일 때 LoseFame 콜백이 호출되지 않는다.</summary>
        [Test]
        public void test_theftSystem_attemptTheft_detectionChanceZero_loseFameNotCalled()
        {
            // Arrange
            // detectionChance = 0 (항상 성공)

            // Act
            _theft.AttemptTheft(Thief, Target,
                onDetected: (id, penalty) => _loseFameCallCount++);

            // Assert
            Assert.AreEqual(0, _loseFameCallCount, "성공 시 LoseFame이 호출되어서는 안 된다");
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-2] detectionChance=1 → 항상 Detected, LoseFame 호출
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("TheftSystem")]
    public class TheftSystemDetectedTests
    {
        private TestableTheftSystem _theft;

        private const ulong Thief = 1UL;
        private const ulong Target = 2UL;
        private const int ExpectedPenalty = 15;

        [SetUp]
        public void SetUp()
        {
            _theft = new TestableTheftSystem(detectionChance: 1f, famePenalty: ExpectedPenalty);
        }

        /// <summary>AC-2: detectionChance=1일 때 AttemptTheft는 Detected를 반환한다.</summary>
        [Test]
        public void test_theftSystem_attemptTheft_detectionChanceOne_returnsDetected()
        {
            // Arrange
            // detectionChance = 1 (항상 발각)

            // Act
            TheftResult result = _theft.AttemptTheft(Thief, Target);

            // Assert
            Assert.AreEqual(TheftResult.Detected, result);
        }

        /// <summary>AC-2: 발각 시 올바른 도둑 ID와 패널티로 LoseFame 콜백이 호출된다.</summary>
        [Test]
        public void test_theftSystem_attemptTheft_detected_callsLoseFameWithCorrectArgs()
        {
            // Arrange
            ulong capturedId = 0;
            int capturedPenalty = 0;

            // Act
            _theft.AttemptTheft(Thief, Target,
                onDetected: (id, penalty) =>
                {
                    capturedId = id;
                    capturedPenalty = penalty;
                });

            // Assert
            Assert.AreEqual(Thief, capturedId, "LoseFame은 도둑의 ID로 호출되어야 한다");
            Assert.AreEqual(ExpectedPenalty, capturedPenalty, "LoseFame은 설정된 패널티 값으로 호출되어야 한다");
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-3, AC-4] 쿨타임 — 미경과 시 OnCooldown, 경과 후 재시도 가능
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("TheftSystem")]
    public class TheftSystemCooldownTests
    {
        private TestableTheftSystem _theft;

        private const ulong Thief = 1UL;
        private const ulong Target = 2UL;
        private const float Cooldown = 5f;

        [SetUp]
        public void SetUp()
        {
            _theft = new TestableTheftSystem(theftCooldown: Cooldown, detectionChance: 0f);
        }

        /// <summary>AC-3: 쿨타임 미경과 상태에서 재시도하면 OnCooldown을 반환한다.</summary>
        [Test]
        public void test_theftSystem_attemptTheft_beforeCooldownExpires_returnsOnCooldown()
        {
            // Arrange
            _theft.SetTime(0f);
            _theft.AttemptTheft(Thief, Target); // 첫 번째 시도 (쿨타임 시작)
            _theft.SetTime(Cooldown - 0.1f);   // 쿨타임 직전

            // Act
            TheftResult result = _theft.AttemptTheft(Thief, Target);

            // Assert
            Assert.AreEqual(TheftResult.OnCooldown, result);
        }

        /// <summary>AC-4: 쿨타임 경과 후 재시도하면 OnCooldown이 아닌 결과를 반환한다.</summary>
        [Test]
        public void test_theftSystem_attemptTheft_afterCooldownExpires_allowsRetry()
        {
            // Arrange
            _theft.SetTime(0f);
            _theft.AttemptTheft(Thief, Target); // 첫 번째 시도
            _theft.SetTime(Cooldown);            // 정확히 쿨타임 경과

            // Act
            TheftResult result = _theft.AttemptTheft(Thief, Target);

            // Assert
            Assert.AreNotEqual(TheftResult.OnCooldown, result, "쿨타임 경과 후에는 재시도가 가능해야 한다");
        }

        /// <summary>AC-4: 쿨타임 경과 후 재시도는 성공 결과를 반환한다 (detectionChance=0).</summary>
        [Test]
        public void test_theftSystem_attemptTheft_afterCooldownExpires_returnsSuccess()
        {
            // Arrange
            _theft.SetTime(0f);
            _theft.AttemptTheft(Thief, Target); // 첫 번째 시도
            _theft.SetTime(Cooldown + 1f);       // 쿨타임 + 여유

            // Act
            TheftResult result = _theft.AttemptTheft(Thief, Target);

            // Assert
            Assert.AreEqual(TheftResult.Success, result);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [Edge-1] 복수 도둑 독립 쿨타임
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("TheftSystem")]
    public class TheftSystemMultiThiefTests
    {
        private TestableTheftSystem _theft;

        private const ulong ThiefA = 1UL;
        private const ulong ThiefB = 2UL;
        private const ulong Target = 3UL;
        private const float Cooldown = 5f;

        [SetUp]
        public void SetUp()
        {
            _theft = new TestableTheftSystem(theftCooldown: Cooldown, detectionChance: 0f);
        }

        /// <summary>Edge-1: 도둑A의 쿨타임이 도둑B의 시도에 영향을 주지 않는다.</summary>
        [Test]
        public void test_theftSystem_multipleThieves_cooldownsAreIndependent()
        {
            // Arrange
            _theft.SetTime(0f);
            _theft.AttemptTheft(ThiefA, Target); // ThiefA 쿨타임 시작
            _theft.SetTime(Cooldown - 0.1f);     // ThiefA 쿨타임 미경과

            // Act — ThiefB는 첫 시도이므로 쿨타임 없음
            TheftResult resultB = _theft.AttemptTheft(ThiefB, Target);

            // Assert
            Assert.AreEqual(TheftResult.Success, resultB,
                "ThiefB는 ThiefA의 쿨타임과 무관하게 시도할 수 있어야 한다");
        }

        /// <summary>Edge-1: 도둑A 쿨타임 중 도둑A 재시도는 OnCooldown을 반환한다 (독립성 역확인).</summary>
        [Test]
        public void test_theftSystem_thiefA_stillOnCooldown_whileThiefBSucceeds()
        {
            // Arrange
            _theft.SetTime(0f);
            _theft.AttemptTheft(ThiefA, Target); // ThiefA 쿨타임 시작
            _theft.AttemptTheft(ThiefB, Target); // ThiefB 쿨타임 시작
            _theft.SetTime(Cooldown - 0.1f);     // 둘 다 쿨타임 미경과

            // Act
            TheftResult resultA = _theft.AttemptTheft(ThiefA, Target);
            TheftResult resultB = _theft.AttemptTheft(ThiefB, Target);

            // Assert
            Assert.AreEqual(TheftResult.OnCooldown, resultA, "ThiefA는 쿨타임 중이어야 한다");
            Assert.AreEqual(TheftResult.OnCooldown, resultB, "ThiefB도 쿨타임 중이어야 한다");
        }
    }
}
