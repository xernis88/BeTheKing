// ============================================================
// PrinceNPCAI — Unit Tests
// Story: production/epics/epic-gameplay-systems/story-008-prince-npc-ai.md
// Requirement: TR-GAME-011
//
// 자동화 범위: 무적 플래그, 대관식 활성화, 중복 활성화 방지, 처치 시 ThroneZone 개방
// 플레이테스트 범위: NGO NetworkBehaviour 동기화, AI 추적/공격 (물리 의존)
//
// 주의: EditMode에서 NGO 사용 불가. TestablePrinceNPCAI로 순수 C# 로직만 검증.
//       ThroneZone 의존성은 MockThroneZone으로 대체한다.
// ============================================================

using NUnit.Framework;
using BeTheKing.GameplaySystems;

namespace BeTheKing.Tests.Unit.Gameplay
{
    // ──────────────────────────────────────────────────────────
    // ThroneZone Mock — OpenFully 호출 감지용
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// TC-GS008-5: OpenFully 호출 여부를 감지하기 위한 테스트 전용 mock.
    /// NetworkBehaviour 없이 순수 C# 로직만 검증.
    /// </summary>
    internal class MockThroneZone
    {
        public int OpenFullyCallCount { get; private set; } = 0;

        public void OpenFully()
        {
            OpenFullyCallCount++;
        }
    }

    // ──────────────────────────────────────────────────────────
    // TestablePrinceNPCAI — NetworkBehaviour 의존성 분리
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// NetworkBehaviour 없이 PrinceNPCAI 핵심 로직만 검증하기 위한 테스트 전용 래퍼.
    /// IsServer를 항상 true로 간주하여 서버 코드 경로를 테스트한다.
    /// </summary>
    internal class TestablePrinceNPCAI
    {
        // 상태 필드 — PrinceNPCAI와 동일한 초기값.
        private bool _isInvincible = true;
        private bool _isActive = false;
        private bool _gameObjectActive = false;
        private float _currentHp;
        private float _maxHp;

        private readonly MockThroneZone _throneZone;

        public bool IsInvincible => _isInvincible;
        public bool IsActive => _isActive;
        public bool GameObjectActive => _gameObjectActive;
        public float CurrentHp => _currentHp;

        public TestablePrinceNPCAI(float maxHp = 300f, MockThroneZone throneZone = null)
        {
            _maxHp = maxHp;
            _currentHp = maxHp;
            _throneZone = throneZone;
        }

        /// <summary>
        /// 피해를 처리한다. 무적 상태(_isInvincible)이면 false 반환.
        /// PrinceNPCAI.TakeDamage와 동일한 로직.
        /// </summary>
        public bool TakeDamage(float amount)
        {
            if (_isInvincible) return false;
            if (_currentHp <= 0f) return false;

            _currentHp -= amount;

            if (_currentHp <= 0f)
            {
                _currentHp = 0f;
                OnDeath();
            }

            return true;
        }

        /// <summary>
        /// 대관식 이벤트 핸들러. 중복 호출 시 무시.
        /// PrinceNPCAI.Activate와 동일한 로직.
        /// </summary>
        public void Activate()
        {
            // 중복 호출 방지.
            if (_isActive) return;

            _isInvincible = false;
            _isActive = true;
            _gameObjectActive = true;
        }

        private void OnDeath()
        {
            _isActive = false;
            _throneZone?.OpenFully();
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-GS008-1, TC-GS008-2: 무적 상태 피해 차단
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("PrinceNPCAI")]
    public class PrinceNPCAIInvincibleTests
    {
        private TestablePrinceNPCAI _prince;

        [SetUp]
        public void SetUp() => _prince = new TestablePrinceNPCAI(maxHp: 300f);

        /// <summary>
        /// TC-GS008-1: 대관식 전 무적 — 일반 피해 차단.
        /// Given: _isInvincible = true
        /// When: TakeDamage(50)
        /// Then: false 반환, HP 변화 없음
        /// </summary>
        [Test]
        public void test_princeNpcAI_takeDamage_whileInvincible_returnsFalseAndHpUnchanged()
        {
            // Arrange
            float hpBefore = _prince.CurrentHp;
            Assert.IsTrue(_prince.IsInvincible, "전제: 초기 무적 상태여야 한다.");

            // Act
            bool result = _prince.TakeDamage(50f);

            // Assert
            Assert.IsFalse(result, "무적 상태에서는 false를 반환해야 한다.");
            Assert.AreEqual(hpBefore, _prince.CurrentHp, 0.001f, "HP는 변화 없어야 한다.");
        }

        /// <summary>
        /// TC-GS008-2: 경계값 — 무적 상태에서 TakeDamage(0)도 false 반환.
        /// Given: _isInvincible = true
        /// When: TakeDamage(0)
        /// Then: false 반환
        /// </summary>
        [Test]
        public void test_princeNpcAI_takeDamage_zeroDamageWhileInvincible_returnsFalse()
        {
            // Arrange
            Assert.IsTrue(_prince.IsInvincible, "전제: 초기 무적 상태여야 한다.");

            // Act
            bool result = _prince.TakeDamage(0f);

            // Assert
            Assert.IsFalse(result, "무적 상태에서는 0 피해도 false를 반환해야 한다.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-GS008-3, TC-GS008-4: 대관식 활성화 및 중복 호출 방지
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("PrinceNPCAI")]
    public class PrinceNPCAIActivationTests
    {
        private TestablePrinceNPCAI _prince;

        [SetUp]
        public void SetUp() => _prince = new TestablePrinceNPCAI(maxHp: 300f);

        /// <summary>
        /// TC-GS008-3: 대관식 후 활성화.
        /// Given: _isInvincible = true, _isActive = false, _gameObjectActive = false
        /// When: Activate() 호출
        /// Then: _isInvincible = false, _isActive = true, GameObjectActive = true
        /// </summary>
        [Test]
        public void test_princeNpcAI_activate_onCoronationStarted_setsStateCorrectly()
        {
            // Arrange
            Assert.IsTrue(_prince.IsInvincible, "전제: 초기 무적 상태여야 한다.");
            Assert.IsFalse(_prince.IsActive, "전제: 초기 비활성 상태여야 한다.");
            Assert.IsFalse(_prince.GameObjectActive, "전제: gameObject 비활성 상태여야 한다.");

            // Act
            _prince.Activate();

            // Assert
            Assert.IsFalse(_prince.IsInvincible, "활성화 후 무적이 해제되어야 한다.");
            Assert.IsTrue(_prince.IsActive, "활성화 후 _isActive = true여야 한다.");
            Assert.IsTrue(_prince.GameObjectActive, "활성화 후 gameObject가 활성화되어야 한다.");
        }

        /// <summary>
        /// TC-GS008-4: Activate() 중복 호출 방지.
        /// Given: _isActive = true (이미 활성화)
        /// When: Activate() 재호출
        /// Then: 상태 변화 없음
        /// </summary>
        [Test]
        public void test_princeNpcAI_activate_duplicateCall_noStateChange()
        {
            // Arrange
            _prince.Activate(); // 첫 번째 활성화

            bool invincibleAfterFirst = _prince.IsInvincible;
            bool activeAfterFirst = _prince.IsActive;
            bool gameObjectAfterFirst = _prince.GameObjectActive;

            // Act: 중복 호출
            _prince.Activate();

            // Assert: 상태 변화 없음
            Assert.AreEqual(invincibleAfterFirst, _prince.IsInvincible, "중복 호출 시 무적 상태가 변해서는 안 된다.");
            Assert.AreEqual(activeAfterFirst, _prince.IsActive, "중복 호출 시 _isActive가 변해서는 안 된다.");
            Assert.AreEqual(gameObjectAfterFirst, _prince.GameObjectActive, "중복 호출 시 gameObject 상태가 변해서는 안 된다.");
        }

        /// <summary>
        /// 활성화 후 TakeDamage는 피해를 적용한다 (무적 해제 확인).
        /// </summary>
        [Test]
        public void test_princeNpcAI_takeDamage_afterActivation_returnsTrueAndReducesHp()
        {
            // Arrange
            _prince.Activate();
            float hpBefore = _prince.CurrentHp;

            // Act
            bool result = _prince.TakeDamage(50f);

            // Assert
            Assert.IsTrue(result, "활성화 후 피해는 true를 반환해야 한다.");
            Assert.AreEqual(hpBefore - 50f, _prince.CurrentHp, 0.001f, "HP가 50 감소해야 한다.");
        }
    }

    // ──────────────────────────────────────────────────────────
    // TC-GS008-5: 처치 시 왕좌 영역 개방
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("PrinceNPCAI")]
    public class PrinceNPCAIDeathTests
    {
        private TestablePrinceNPCAI _prince;
        private MockThroneZone _mockThroneZone;

        [SetUp]
        public void SetUp()
        {
            _mockThroneZone = new MockThroneZone();
            _prince = new TestablePrinceNPCAI(maxHp: 100f, throneZone: _mockThroneZone);
            _prince.Activate(); // 피해 받을 수 있도록 활성화
        }

        /// <summary>
        /// TC-GS008-5: 처치 시 ThroneZone.OpenFully() 호출됨.
        /// Given: 왕자 NPC HP = 100
        /// When: TakeDamage(100) — 즉사
        /// Then: _throneZone.OpenFully() 호출됨
        /// </summary>
        [Test]
        public void test_princeNpcAI_onDeath_callsThroneZoneOpenFully()
        {
            // Arrange
            Assert.AreEqual(0, _mockThroneZone.OpenFullyCallCount, "전제: 아직 OpenFully 미호출.");

            // Act
            _prince.TakeDamage(100f); // HP 0으로 즉사

            // Assert
            Assert.AreEqual(1, _mockThroneZone.OpenFullyCallCount, "처치 시 OpenFully가 정확히 1회 호출되어야 한다.");
            Assert.AreEqual(0f, _prince.CurrentHp, 0.001f, "처치 후 HP는 0이어야 한다.");
        }

        /// <summary>
        /// 처치 후 추가 피해는 OpenFully를 재호출하지 않는다.
        /// </summary>
        [Test]
        public void test_princeNpcAI_takeDamage_afterDeath_doesNotCallOpenFullyAgain()
        {
            // Arrange
            _prince.TakeDamage(100f); // 처치
            int callCountAfterFirstDeath = _mockThroneZone.OpenFullyCallCount;

            // Act: 이미 처치된 상태에서 추가 피해 — _currentHp <= 0f 가드로 즉시 false 반환.
            _prince.TakeDamage(10f);

            // Assert
            Assert.AreEqual(callCountAfterFirstDeath, _mockThroneZone.OpenFullyCallCount,
                "이미 처치된 이후 추가 피해는 OpenFully를 재호출하면 안 된다.");
        }
    }
}
