// ============================================================
// JobInteractionSystem — Unit Tests
// Story: production/epics/epic-gameplay-systems/story-005-job-interaction.md
// ADR: docs/architecture/ADR-013-job-interaction-skill-check.md
//
// 자동화 범위:
//   AC-2: 구간 내 각도 → 성공, goldAwarded=true
//   AC-3: 구간 밖 각도 → 실패, suspicionReported=true
//   AC-4: CancelSkillCheck → 이후 SubmitInput NotActive
//   Edge-1: currentAngle == zoneStart (하한 경계) → Success
//   Edge-2: currentAngle == zoneStart + zoneSize (상한 경계) → Success
//   Edge-3: currentAngle == zoneStart + zoneSize + 1 → Failure
//   Extra: 세션 없는 상태에서 SubmitInput → NotActive
//
// 플레이테스트 범위: ServerRpc/ClientRpc 동기화 (NGO 의존 — 플레이테스트)
// ============================================================

using NUnit.Framework;
using System.Collections.Generic;

namespace BeTheKing.Tests.Unit
{
    // ──────────────────────────────────────────────────────────
    // NGO 의존성을 제거한 순수 C# 래퍼
    // NetworkBehaviour.IsServer가 EditMode에서 항상 false이므로
    // 로직만 추출하여 검증한다. (currency_system_test.cs 동일 패턴)
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// SkillCheckState는 internal이므로 테스트 어셈블리에서 직접 사용하기 위한 로컬 복사본.
    /// TestableJobInteractionSystem 내부 전용.
    /// </summary>
    internal class TestSkillCheckState
    {
        public float TargetZoneStart;
        public float SuccessZoneSize;
        public bool IsActive;
    }

    /// <summary>SubmitInput 판정 결과.</summary>
    public enum SkillCheckResult { Success, Failure, NotActive }

    /// <summary>
    /// JobInteractionSystem의 핵심 로직을 NGO 없이 검증하기 위한 테스트 전용 래퍼.
    /// NextZoneStart로 Random.Range 의존성을 제거하여 결정론적 테스트를 가능하게 한다.
    /// AwardCalls/ReportCalls로 CurrencySystem/SuspicionSystem 호출 여부를 검증한다.
    /// </summary>
    internal class TestableJobInteractionSystem
    {
        private readonly float _successZoneSize;
        private readonly int _rewardAmount;
        private readonly Dictionary<ulong, TestSkillCheckState> _activeSessions = new();

        /// <summary>BeginSkillCheck 호출 시 목표 구간 시작 각도로 사용할 고정값. 테스트에서 설정.</summary>
        public float NextZoneStart = 100f;

        /// <summary>Award가 호출된 (clientId, amount) 기록. AC-2 검증에 사용.</summary>
        public List<(ulong clientId, int amount)> AwardCalls = new();

        /// <summary>Report가 호출된 clientId 기록. AC-3 검증에 사용.</summary>
        public List<ulong> ReportCalls = new();

        /// <param name="successZoneSize">목표 구간 크기(도). 기본값 40.</param>
        /// <param name="rewardAmount">성공 시 지급 금화량. 기본값 10.</param>
        public TestableJobInteractionSystem(float successZoneSize = 40f, int rewardAmount = 10)
        {
            _successZoneSize = successZoneSize;
            _rewardAmount = rewardAmount;
        }

        /// <summary>
        /// 스킬체크 세션을 시작한다. NextZoneStart를 목표 구간 시작 각도로 사용.
        /// </summary>
        /// <param name="clientId">세션 대상 클라이언트 ID.</param>
        public void BeginSkillCheck(ulong clientId)
        {
            _activeSessions[clientId] = new TestSkillCheckState
            {
                TargetZoneStart = NextZoneStart,
                SuccessZoneSize = _successZoneSize,
                IsActive = true
            };
        }

        /// <summary>
        /// 입력 각도를 제출하여 성공/실패를 판정한다.
        /// </summary>
        /// <param name="angle">제출할 게이지 각도(도).</param>
        /// <param name="clientId">제출하는 클라이언트 ID.</param>
        /// <returns>
        ///   Success: 구간 내 각도, Failure: 구간 밖 각도, NotActive: 세션 없거나 비활성.
        /// </returns>
        public SkillCheckResult SubmitInput(float angle, ulong clientId)
        {
            if (!_activeSessions.TryGetValue(clientId, out var state) || !state.IsActive)
                return SkillCheckResult.NotActive;

            bool isSuccess = angle >= state.TargetZoneStart &&
                             angle <= state.TargetZoneStart + state.SuccessZoneSize;

            if (isSuccess)
                AwardCalls.Add((clientId, _rewardAmount));
            else
                ReportCalls.Add(clientId);

            state.IsActive = false;
            return isSuccess ? SkillCheckResult.Success : SkillCheckResult.Failure;
        }

        /// <summary>
        /// 진행 중인 세션을 취소한다. 취소 후 SubmitInput은 NotActive를 반환한다.
        /// </summary>
        /// <param name="clientId">취소할 세션의 클라이언트 ID.</param>
        public void Cancel(ulong clientId)
        {
            if (_activeSessions.TryGetValue(clientId, out var state))
                state.IsActive = false;
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-2] 구간 내 각도 → 성공
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("JobInteractionSystem")]
    public class JobInteractionSuccessTests
    {
        private TestableJobInteractionSystem _system;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp()
        {
            _system = new TestableJobInteractionSystem(successZoneSize: 40f, rewardAmount: 10);
            _system.NextZoneStart = 100f; // 목표 구간: [100, 140]
        }

        /// <summary>AC-2: 구간 내 각도 제출 시 Success를 반환한다.</summary>
        [Test]
        public void test_jobInteraction_submitInput_angleInsideZone_returnsSuccess()
        {
            // Arrange
            _system.BeginSkillCheck(ClientA);

            // Act
            var result = _system.SubmitInput(120f, ClientA); // 120 ∈ [100, 140]

            // Assert
            Assert.AreEqual(SkillCheckResult.Success, result);
        }

        /// <summary>AC-2: 구간 내 각도 성공 시 Award가 호출된다 (goldAwarded=true).</summary>
        [Test]
        public void test_jobInteraction_submitInput_angleInsideZone_awardsGold()
        {
            // Arrange
            _system.BeginSkillCheck(ClientA);

            // Act
            _system.SubmitInput(120f, ClientA);

            // Assert
            Assert.AreEqual(1, _system.AwardCalls.Count);
            Assert.AreEqual((ClientA, 10), _system.AwardCalls[0]);
        }

        /// <summary>AC-2: 성공 시 SuspicionSystem.Report가 호출되지 않는다.</summary>
        [Test]
        public void test_jobInteraction_submitInput_success_doesNotReport()
        {
            // Arrange
            _system.BeginSkillCheck(ClientA);

            // Act
            _system.SubmitInput(120f, ClientA);

            // Assert
            Assert.AreEqual(0, _system.ReportCalls.Count);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-3] 구간 밖 각도 → 실패
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("JobInteractionSystem")]
    public class JobInteractionFailureTests
    {
        private TestableJobInteractionSystem _system;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp()
        {
            _system = new TestableJobInteractionSystem(successZoneSize: 40f, rewardAmount: 10);
            _system.NextZoneStart = 100f; // 목표 구간: [100, 140]
        }

        /// <summary>AC-3: 구간 밖 각도 제출 시 Failure를 반환한다.</summary>
        [Test]
        public void test_jobInteraction_submitInput_angleOutsideZone_returnsFailure()
        {
            // Arrange
            _system.BeginSkillCheck(ClientA);

            // Act
            var result = _system.SubmitInput(50f, ClientA); // 50 < 100, 구간 밖

            // Assert
            Assert.AreEqual(SkillCheckResult.Failure, result);
        }

        /// <summary>AC-3: 구간 밖 각도 실패 시 SuspicionSystem.Report가 호출된다 (suspicionReported=true).</summary>
        [Test]
        public void test_jobInteraction_submitInput_angleOutsideZone_reportsSuspicion()
        {
            // Arrange
            _system.BeginSkillCheck(ClientA);

            // Act
            _system.SubmitInput(50f, ClientA);

            // Assert
            Assert.AreEqual(1, _system.ReportCalls.Count);
            Assert.AreEqual(ClientA, _system.ReportCalls[0]);
        }

        /// <summary>AC-3: 실패 시 CurrencySystem.Award가 호출되지 않는다.</summary>
        [Test]
        public void test_jobInteraction_submitInput_failure_doesNotAwardGold()
        {
            // Arrange
            _system.BeginSkillCheck(ClientA);

            // Act
            _system.SubmitInput(50f, ClientA);

            // Assert
            Assert.AreEqual(0, _system.AwardCalls.Count);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-4] CancelSkillCheck → 이후 SubmitInput NotActive
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("JobInteractionSystem")]
    public class JobInteractionCancelTests
    {
        private TestableJobInteractionSystem _system;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp()
        {
            _system = new TestableJobInteractionSystem();
            _system.NextZoneStart = 100f;
        }

        /// <summary>AC-4: Cancel 후 SubmitInput이 NotActive를 반환한다.</summary>
        [Test]
        public void test_jobInteraction_cancel_thenSubmitInput_returnsNotActive()
        {
            // Arrange
            _system.BeginSkillCheck(ClientA);

            // Act
            _system.Cancel(ClientA);
            var result = _system.SubmitInput(120f, ClientA);

            // Assert
            Assert.AreEqual(SkillCheckResult.NotActive, result);
        }

        /// <summary>AC-4: Cancel 후 Award 및 Report 모두 호출되지 않는다.</summary>
        [Test]
        public void test_jobInteraction_cancel_noSideEffectsOnSubmit()
        {
            // Arrange
            _system.BeginSkillCheck(ClientA);

            // Act
            _system.Cancel(ClientA);
            _system.SubmitInput(120f, ClientA);

            // Assert
            Assert.AreEqual(0, _system.AwardCalls.Count);
            Assert.AreEqual(0, _system.ReportCalls.Count);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [Edge-1, Edge-2, Edge-3] 경계 케이스
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("JobInteractionSystem")]
    public class JobInteractionBoundaryTests
    {
        private TestableJobInteractionSystem _system;
        private const ulong ClientA = 1UL;

        // 목표 구간: [100, 140] (zoneStart=100, zoneSize=40)
        private const float ZoneStart = 100f;
        private const float ZoneSize = 40f;

        [SetUp]
        public void SetUp()
        {
            _system = new TestableJobInteractionSystem(successZoneSize: ZoneSize);
            _system.NextZoneStart = ZoneStart;
        }

        /// <summary>Edge-1: currentAngle == zoneStart (하한 경계 정확히) → Success.</summary>
        [Test]
        public void test_jobInteraction_submitInput_angleExactlyAtZoneStart_returnsSuccess()
        {
            // Arrange
            _system.BeginSkillCheck(ClientA);

            // Act
            var result = _system.SubmitInput(ZoneStart, ClientA); // 100 == 100

            // Assert
            Assert.AreEqual(SkillCheckResult.Success, result);
        }

        /// <summary>Edge-2: currentAngle == zoneStart + zoneSize (상한 경계 정확히) → Success.</summary>
        [Test]
        public void test_jobInteraction_submitInput_angleExactlyAtZoneEnd_returnsSuccess()
        {
            // Arrange
            _system.BeginSkillCheck(ClientA);

            // Act
            var result = _system.SubmitInput(ZoneStart + ZoneSize, ClientA); // 140 == 140

            // Assert
            Assert.AreEqual(SkillCheckResult.Success, result);
        }

        /// <summary>Edge-3: currentAngle == zoneStart + zoneSize + 1 (상한 초과 1도) → Failure.</summary>
        [Test]
        public void test_jobInteraction_submitInput_angleOneAboveZoneEnd_returnsFailure()
        {
            // Arrange
            _system.BeginSkillCheck(ClientA);

            // Act
            var result = _system.SubmitInput(ZoneStart + ZoneSize + 1f, ClientA); // 141 > 140

            // Assert
            Assert.AreEqual(SkillCheckResult.Failure, result);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [Extra] 세션 없는 상태에서 SubmitInput → NotActive
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("JobInteractionSystem")]
    public class JobInteractionNoSessionTests
    {
        private TestableJobInteractionSystem _system;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp() => _system = new TestableJobInteractionSystem();

        /// <summary>Extra: BeginSkillCheck 없이 SubmitInput 호출 시 NotActive를 반환한다.</summary>
        [Test]
        public void test_jobInteraction_submitInput_withoutBegin_returnsNotActive()
        {
            // Arrange
            // BeginSkillCheck 호출 없음

            // Act
            var result = _system.SubmitInput(120f, ClientA);

            // Assert
            Assert.AreEqual(SkillCheckResult.NotActive, result);
        }

        /// <summary>Extra: 세션 없는 상태에서 SubmitInput 호출 시 Award/Report 모두 호출되지 않는다.</summary>
        [Test]
        public void test_jobInteraction_submitInput_withoutBegin_noSideEffects()
        {
            // Arrange
            // BeginSkillCheck 호출 없음

            // Act
            _system.SubmitInput(120f, ClientA);

            // Assert
            Assert.AreEqual(0, _system.AwardCalls.Count);
            Assert.AreEqual(0, _system.ReportCalls.Count);
        }
    }
}
