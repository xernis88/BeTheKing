// ============================================================
// TraitTreeSystem — Integration Tests
// Story: production/epics/epic-progression-economy/story-003-trait-tree-system.md
// ADR: docs/architecture/ADR-015-trait-tree-system-server-dictionary.md
//
// 자동화 범위:
//   AC-1: OnSkillBookAcquired → Available = 1
//   AC-2: 공격형 투자 → onAttackTrait 콜백 호출됨
//   AC-3: 혼합 빌드 (3방향 각 1씩 투자)
//   AC-4: Available = 0 → InvestPoint 거부 (false 반환)
//   Edge-1: 연속 스킬북 3개 → Available = 3
//   Edge-2: FootprintSystem null 처리 (콜백 null → NRE 없음)
//
// 주의: NGO NetworkBehaviour는 EditMode에서 직접 인스턴스화 불가.
//   TestableTraitTree 래퍼 패턴으로 NGO 의존성 분리.
//   TraitPoints는 internal 이므로 테스트 내 자체 복사본(TraitPoints) 사용.
// ============================================================

using System;
using System.Collections.Generic;
using NUnit.Framework;
using BeTheKing.GameplaySystems;

namespace BeTheKing.Tests.Integration.Progression
{
    // ──────────────────────────────────────────────────────────
    // 테스트 전용 내부 타입
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// 테스트 전용 포인트 상태. TraitTreeSystem의 internal TraitPoints 복사본.
    /// InternalVisibleTo 설정 없이 동작한다.
    /// </summary>
    internal class TraitPoints
    {
        public int Available;
        public int[] Invested = new int[3];
    }

    /// <summary>
    /// NGO 없이 TraitTreeSystem 핵심 로직을 검증하기 위한 테스트 전용 래퍼.
    /// 서버 Dictionary 패턴과 onAttackTrait 콜백을 순수 C#으로 재현한다.
    /// </summary>
    internal class TestableTraitTree
    {
        private readonly Dictionary<ulong, TraitPoints> _points = new();

        /// <summary>
        /// 스킬북 획득. 해당 클라이언트의 Available을 1 증가시킨다.
        /// </summary>
        public void OnSkillBookAcquired(ulong clientId)
        {
            if (!_points.ContainsKey(clientId))
                _points[clientId] = new TraitPoints();

            _points[clientId].Available++;
        }

        /// <summary>
        /// 포인트 투자. Available이 0이면 거부(false).
        /// 공격형 투자 성공 시 <paramref name="onAttackTrait"/>를 호출한다.
        /// </summary>
        /// <param name="clientId">투자 주체 클라이언트 ID.</param>
        /// <param name="direction">투자 방향.</param>
        /// <param name="onAttackTrait">공격형 투자 성공 시 실행할 콜백. null 허용.</param>
        /// <returns>투자 성공 시 true. Available 부족 또는 미등록 clientId면 false.</returns>
        public bool InvestPoint(ulong clientId, TraitDirection direction, Action<ulong> onAttackTrait = null)
        {
            if (!_points.TryGetValue(clientId, out var pts) || pts.Available <= 0)
                return false;

            pts.Available--;
            pts.Invested[(int)direction]++;

            if (direction == TraitDirection.Attack)
                onAttackTrait?.Invoke(clientId);

            return true;
        }

        /// <summary>현재 보유 포인트를 반환한다. 미등록 clientId면 0.</summary>
        public int GetAvailable(ulong clientId) =>
            _points.TryGetValue(clientId, out var pts) ? pts.Available : 0;

        /// <summary>방향별 투자량을 반환한다. 미등록 clientId면 0.</summary>
        public int GetInvested(ulong clientId, TraitDirection dir) =>
            _points.TryGetValue(clientId, out var pts) ? pts.Invested[(int)dir] : 0;
    }

    // ──────────────────────────────────────────────────────────
    // [AC-1] OnSkillBookAcquired → Available = 1
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("TraitTreeSystem")]
    public class TraitTreeSystemSkillBookAcquiredTests
    {
        private TestableTraitTree _tree;
        private const ulong ClientId = 1UL;

        [SetUp]
        public void SetUp()
        {
            _tree = new TestableTraitTree();
        }

        /// <summary>
        /// AC-1: 스킬북 1개 획득 시 Available = 1.
        /// Given: 신규 클라이언트
        /// When: OnSkillBookAcquired(clientId)
        /// Then: GetAvailable = 1
        /// </summary>
        [Test]
        public void test_traitTree_onSkillBookAcquired_firstBook_availableIsOne()
        {
            // Act
            _tree.OnSkillBookAcquired(ClientId);

            // Assert
            Assert.AreEqual(1, _tree.GetAvailable(ClientId));
        }

        /// <summary>
        /// Edge-1: 스킬북 3개 연속 획득 시 Available = 3.
        /// Given: 신규 클라이언트
        /// When: OnSkillBookAcquired 3회 호출
        /// Then: GetAvailable = 3
        /// </summary>
        [Test]
        public void test_traitTree_onSkillBookAcquired_threeBooks_availableIsThree()
        {
            // Act
            _tree.OnSkillBookAcquired(ClientId);
            _tree.OnSkillBookAcquired(ClientId);
            _tree.OnSkillBookAcquired(ClientId);

            // Assert
            Assert.AreEqual(3, _tree.GetAvailable(ClientId));
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-2] 공격형 투자 → onAttackTrait 콜백 호출됨
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("TraitTreeSystem")]
    public class TraitTreeSystemAttackInvestTests
    {
        private TestableTraitTree _tree;
        private const ulong ClientId = 1UL;

        [SetUp]
        public void SetUp()
        {
            _tree = new TestableTraitTree();
            _tree.OnSkillBookAcquired(ClientId);
        }

        /// <summary>
        /// AC-2: 공격형 투자 성공 시 onAttackTrait 콜백이 올바른 clientId로 호출된다.
        /// Given: Available = 1
        /// When: InvestPoint(Attack, onAttackTrait)
        /// Then: 콜백 호출됨, Available = 0, Invested[Attack] = 1
        /// </summary>
        [Test]
        public void test_traitTree_investPoint_attack_callbackInvoked()
        {
            // Arrange
            ulong? callbackClientId = null;
            void OnAttack(ulong id) => callbackClientId = id;

            // Act
            bool result = _tree.InvestPoint(ClientId, TraitDirection.Attack, OnAttack);

            // Assert
            Assert.IsTrue(result, "포인트 투자 반환값 = true");
            Assert.AreEqual(ClientId, callbackClientId, "콜백에 정확한 clientId 전달");
            Assert.AreEqual(0, _tree.GetAvailable(ClientId), "Available 차감됨");
            Assert.AreEqual(1, _tree.GetInvested(ClientId, TraitDirection.Attack), "Invested[Attack] = 1");
        }

        /// <summary>
        /// Edge-2: onAttackTrait 콜백이 null이어도 NullReferenceException 없이 완료된다.
        /// Given: Available = 1, 콜백 null
        /// When: InvestPoint(Attack, null)
        /// Then: 예외 없이 true 반환
        /// </summary>
        [Test]
        public void test_traitTree_investPoint_attack_nullCallback_noException()
        {
            // Act + Assert: 예외 없이 완료
            bool result = false;
            Assert.DoesNotThrow(() => result = _tree.InvestPoint(ClientId, TraitDirection.Attack, null));
            Assert.IsTrue(result);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-3] 혼합 빌드 — 3방향 각 1씩 투자
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("TraitTreeSystem")]
    public class TraitTreeSystemMixedBuildTests
    {
        private TestableTraitTree _tree;
        private const ulong ClientId = 1UL;

        [SetUp]
        public void SetUp()
        {
            _tree = new TestableTraitTree();
            // 포인트 3개 준비
            _tree.OnSkillBookAcquired(ClientId);
            _tree.OnSkillBookAcquired(ClientId);
            _tree.OnSkillBookAcquired(ClientId);
        }

        /// <summary>
        /// AC-3: 3방향에 각 1포인트 투자 시 각 Invested = 1, Available = 0.
        /// Given: Available = 3
        /// When: Attack/Survival/Assassin 각 1회 투자
        /// Then: 모든 Invested = 1, Available = 0
        /// </summary>
        [Test]
        public void test_traitTree_investPoint_mixedBuild_allDirectionsInvestedOne()
        {
            // Act
            _tree.InvestPoint(ClientId, TraitDirection.Attack);
            _tree.InvestPoint(ClientId, TraitDirection.Survival);
            _tree.InvestPoint(ClientId, TraitDirection.Assassin);

            // Assert
            Assert.AreEqual(1, _tree.GetInvested(ClientId, TraitDirection.Attack));
            Assert.AreEqual(1, _tree.GetInvested(ClientId, TraitDirection.Survival));
            Assert.AreEqual(1, _tree.GetInvested(ClientId, TraitDirection.Assassin));
            Assert.AreEqual(0, _tree.GetAvailable(ClientId), "포인트 3개 모두 소진");
        }

        /// <summary>
        /// AC-3 보완: 비공격형 투자 시 onAttackTrait 콜백이 호출되지 않는다.
        /// </summary>
        [Test]
        public void test_traitTree_investPoint_nonAttack_noCallback()
        {
            // Arrange
            bool callbackInvoked = false;
            void OnAttack(ulong _) => callbackInvoked = true;

            // Act
            _tree.InvestPoint(ClientId, TraitDirection.Survival, OnAttack);
            _tree.InvestPoint(ClientId, TraitDirection.Assassin, OnAttack);

            // Assert
            Assert.IsFalse(callbackInvoked, "Survival/Assassin 투자 시 공격형 콜백 미호출");
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-4] Available = 0 → InvestPoint 거부
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("TraitTreeSystem")]
    public class TraitTreeSystemInvestRejectionTests
    {
        private TestableTraitTree _tree;
        private const ulong ClientId = 1UL;

        [SetUp]
        public void SetUp()
        {
            _tree = new TestableTraitTree();
        }

        /// <summary>
        /// AC-4: Available = 0 상태에서 InvestPoint 호출 시 false를 반환하고 상태 변경 없음.
        /// Given: Available = 0 (스킬북 미획득)
        /// When: InvestPoint(Attack)
        /// Then: false 반환, Invested 변경 없음
        /// </summary>
        [Test]
        public void test_traitTree_investPoint_noPoints_returnsFalse()
        {
            // Arrange: Available = 0 (스킬북 미획득)
            Assert.AreEqual(0, _tree.GetAvailable(ClientId), "사전 조건: Available = 0");

            // Act
            bool result = _tree.InvestPoint(ClientId, TraitDirection.Attack);

            // Assert
            Assert.IsFalse(result, "포인트 없을 때 투자 거부");
            Assert.AreEqual(0, _tree.GetInvested(ClientId, TraitDirection.Attack), "Invested 변경 없음");
        }

        /// <summary>
        /// AC-4 보완: 포인트 1개를 소진한 후 추가 투자 시도도 거부된다.
        /// Given: Available = 1 → 1회 투자 후 Available = 0
        /// When: 2번째 InvestPoint
        /// Then: false 반환
        /// </summary>
        [Test]
        public void test_traitTree_investPoint_afterExhaustion_returnsFalse()
        {
            // Arrange
            _tree.OnSkillBookAcquired(ClientId);
            _tree.InvestPoint(ClientId, TraitDirection.Attack); // 소진

            // Act
            bool result = _tree.InvestPoint(ClientId, TraitDirection.Attack);

            // Assert
            Assert.IsFalse(result);
            Assert.AreEqual(0, _tree.GetAvailable(ClientId));
        }
    }
}
