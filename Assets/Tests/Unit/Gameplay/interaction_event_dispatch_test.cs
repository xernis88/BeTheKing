// ============================================================
// InteractionSystem 이벤트 디스패치 — Unit Tests
// Story: Sprint 6 / TECH-004
// Target: JobInteractionSystem.OnSkillCheckBegin/OnSkillCheckEnd
//         GeneralInteractionSystem.HoldProgress (프로퍼티 기반)
//
// 자동화 범위: static event 발행 인자 검증, null-safe 호출, 복수 구독자,
//              HoldProgress 초기화/진행 프로퍼티 값 검증
// 플레이테스트 범위: NGO 환경에서 실제 ClientRpc → 이벤트 체인 (NGO 의존)
// ============================================================

using NUnit.Framework;
using BeTheKing.GameplaySystems;

namespace BeTheKing.Tests.Unit.Gameplay
{
    // ──────────────────────────────────────────────────────────
    // TC-TECH004-1: RaiseOnSkillCheckBegin → 인자 검증
    // TC-TECH004-2: 구독자 없을 때 NullReferenceException 없음
    // TC-TECH004-3: 복수 구독자 전원 수신
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("StaticEventDispatch")]
    public class SkillCheckBeginEventTests
    {
        private float _lastZoneStart;
        private float _lastZoneEnd;
        private int   _callCount;

        private void CaptureBegin(float zoneStart, float zoneEnd)
        {
            _lastZoneStart = zoneStart;
            _lastZoneEnd   = zoneEnd;
            _callCount++;
        }

        [SetUp]
        public void SetUp()
        {
            _lastZoneStart = -1f; _lastZoneEnd = -1f; _callCount = 0;
        }

        [TearDown]
        public void TearDown()
        {
            JobInteractionSystem.OnSkillCheckBegin -= CaptureBegin;
        }

        /// <summary>
        /// TC-TECH004-1: RaiseOnSkillCheckBegin(30f, 70f) 호출 시
        /// OnSkillCheckBegin이 zoneStart=30f, zoneEnd=70f로 발행된다.
        /// </summary>
        [Test]
        public void test_skillCheckBegin_raiseInvoked_eventFiredWithCorrectArgs()
        {
            JobInteractionSystem.OnSkillCheckBegin += CaptureBegin;

            JobInteractionSystem.RaiseOnSkillCheckBegin(zoneStart: 30f, zoneEnd: 70f);

            Assert.AreEqual(30f, _lastZoneStart, 0.001f, "zoneStart가 그대로 전달되어야 한다.");
            Assert.AreEqual(70f, _lastZoneEnd,   0.001f, "zoneEnd가 그대로 전달되어야 한다.");
            Assert.AreEqual(1,   _callCount,             "이벤트는 정확히 1회 발행되어야 한다.");
        }

        /// <summary>
        /// TC-TECH004-2: 구독자 없을 때 RaiseOnSkillCheckBegin 호출 시
        /// NullReferenceException이 발생하지 않는다.
        /// </summary>
        [Test]
        public void test_skillCheckBegin_noSubscribers_doesNotThrow()
        {
            Assert.DoesNotThrow(
                () => JobInteractionSystem.RaiseOnSkillCheckBegin(0f, 360f),
                "구독자가 없어도 null-conditional invoke로 예외 없이 실행되어야 한다.");
        }

        /// <summary>
        /// TC-TECH004-3: 구독자 3개 등록 시 모두 수신한다.
        /// </summary>
        [Test]
        public void test_skillCheckBegin_multipleSubscribers_allReceive()
        {
            int count = 0;
            void Sub1(float _, float __) => count++;
            void Sub2(float _, float __) => count++;
            void Sub3(float _, float __) => count++;

            JobInteractionSystem.OnSkillCheckBegin += Sub1;
            JobInteractionSystem.OnSkillCheckBegin += Sub2;
            JobInteractionSystem.OnSkillCheckBegin += Sub3;

            try
            {
                JobInteractionSystem.RaiseOnSkillCheckBegin(45f, 90f);
                Assert.AreEqual(3, count, "3개 구독자 모두 이벤트를 수신해야 한다.");
            }
            finally
            {
                JobInteractionSystem.OnSkillCheckBegin -= Sub1;
                JobInteractionSystem.OnSkillCheckBegin -= Sub2;
                JobInteractionSystem.OnSkillCheckBegin -= Sub3;
            }
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-TECH004-4: RaiseOnSkillCheckEnd(true/false) → 인자 검증
    // TC-TECH004-5: 구독자 없을 때 안전 처리
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("StaticEventDispatch")]
    public class SkillCheckEndEventTests
    {
        private bool _lastSuccess;
        private int  _callCount;

        private void CaptureEnd(bool success) { _lastSuccess = success; _callCount++; }

        [SetUp]
        public void SetUp() { _lastSuccess = false; _callCount = 0; }

        [TearDown]
        public void TearDown() { JobInteractionSystem.OnSkillCheckEnd -= CaptureEnd; }

        /// <summary>TC-TECH004-4a: success=true로 발행 시 이벤트 인자 검증.</summary>
        [Test]
        public void test_skillCheckEnd_success_eventFiredWithTrue()
        {
            JobInteractionSystem.OnSkillCheckEnd += CaptureEnd;
            JobInteractionSystem.RaiseOnSkillCheckEnd(success: true);
            Assert.IsTrue(_lastSuccess, "success=true가 그대로 전달되어야 한다.");
            Assert.AreEqual(1, _callCount);
        }

        /// <summary>TC-TECH004-4b: success=false로 발행 시 이벤트 인자 검증.</summary>
        [Test]
        public void test_skillCheckEnd_failure_eventFiredWithFalse()
        {
            JobInteractionSystem.OnSkillCheckEnd += CaptureEnd;
            JobInteractionSystem.RaiseOnSkillCheckEnd(success: false);
            Assert.IsFalse(_lastSuccess, "success=false가 그대로 전달되어야 한다.");
        }

        /// <summary>TC-TECH004-5: 구독자 없을 때 안전 처리.</summary>
        [Test]
        public void test_skillCheckEnd_noSubscribers_doesNotThrow()
        {
            Assert.DoesNotThrow(
                () => JobInteractionSystem.RaiseOnSkillCheckEnd(true),
                "구독자가 없어도 예외 없이 실행되어야 한다.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-TECH004-6: GeneralInteractionSystem.HoldProgress 프로퍼티 기반 검증
    //   (instance event OnHoldProgress는 IsOwner + NGO 의존 → 플레이테스트 대상)
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("StaticEventDispatch")]
    public class HoldProgressTests
    {
        private GeneralInteractionSystem _sys;

        [SetUp]
        public void SetUp()
        {
            var go = new UnityEngine.GameObject("TestGIS");
            _sys = go.AddComponent<GeneralInteractionSystem>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_sys != null)
                UnityEngine.Object.DestroyImmediate(_sys.gameObject);
        }

        /// <summary>TC-TECH004-6a: CancelHold 호출 시 HoldProgress가 0f로 초기화된다.</summary>
        [Test]
        public void test_holdProgress_afterCancel_isZero()
        {
            _sys.CancelHold();
            Assert.AreEqual(0f, _sys.HoldProgress, 0.001f,
                "CancelHold 후 HoldProgress는 0이어야 한다.");
        }

        /// <summary>TC-TECH004-6b: HoldDuration 프로퍼티가 양수를 반환한다.</summary>
        [Test]
        public void test_holdDuration_isPositive()
        {
            Assert.Greater(_sys.HoldDuration, 0f,
                "HoldDuration은 0보다 커야 한다.");
        }
    }
}
