// ============================================================
// CurrencySystem — Unit Tests
// Story: production/epics/epic-progression-economy/story-001-currency-system.md
// ADR: docs/architecture/ADR-006-currency-system-server-dictionary.md
//
// 자동화 범위: Award, TrySpend 성공/실패, 신규 클라이언트 초기화, 연속 연산 정확성
// 플레이테스트 범위: ClientRpc 동기화, SyncBalanceClientRpc LocalClientId 필터링 (NGO 의존)
// ============================================================

using NUnit.Framework;
using System.Collections.Generic;

namespace BeTheKing.Tests.Unit
{
    // ──────────────────────────────────────────────────────────
    // NGO 의존성을 제거한 순수 C# 래퍼
    // NetworkBehaviour.IsServer가 EditMode에서 항상 false이므로
    // 로직만 추출하여 검증한다. (stamina_system_test.cs 동일 패턴)
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// CurrencySystem의 핵심 로직을 NGO 없이 검증하기 위한 테스트 전용 래퍼.
    /// SyncCalls로 ClientRpc가 올바른 인자로 호출됐는지 검증한다.
    /// </summary>
    internal class TestableCurrencySystem
    {
        private readonly Dictionary<ulong, int> _balances = new();

        /// <summary>Award/TrySpend 호출 시 SyncBalanceClientRpc에 전달될 인자 기록.</summary>
        public List<(ulong clientId, int balance)> SyncCalls = new();

        public void Award(ulong clientId, int amount)
        {
            _balances.TryAdd(clientId, 0);
            _balances[clientId] += amount;
            SyncCalls.Add((clientId, _balances[clientId]));
        }

        public bool TrySpend(ulong clientId, int amount)
        {
            if (!_balances.TryGetValue(clientId, out var bal) || bal < amount) return false;
            _balances[clientId] -= amount;
            SyncCalls.Add((clientId, _balances[clientId]));
            return true;
        }

        public int GetBalance(ulong clientId) => _balances.TryGetValue(clientId, out var v) ? v : 0;
    }

    // ──────────────────────────────────────────────────────────
    // [AC-1] Award — 잔액 증가 및 Sync 호출 검증
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("CurrencySystem")]
    public class CurrencySystemAwardTests
    {
        private TestableCurrencySystem _currency;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp() => _currency = new TestableCurrencySystem();

        /// <summary>AC-1: Award 후 잔액이 정확히 증가한다.</summary>
        [Test]
        public void test_award_increases_balance_correctly()
        {
            // Arrange
            // 초기 잔액 0

            // Act
            _currency.Award(ClientA, 10);

            // Assert
            Assert.AreEqual(10, _currency.GetBalance(ClientA));
        }

        /// <summary>AC-1: Award 후 SyncBalanceClientRpc가 올바른 인자로 호출된다.</summary>
        [Test]
        public void test_sync_called_after_award()
        {
            // Arrange
            // 초기 잔액 0

            // Act
            _currency.Award(ClientA, 10);

            // Assert
            Assert.AreEqual(1, _currency.SyncCalls.Count);
            Assert.AreEqual((ClientA, 10), _currency.SyncCalls[0]);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-3] TrySpend — 잔액 부족 시 거부
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("CurrencySystem")]
    public class CurrencySystemSpendFailureTests
    {
        private TestableCurrencySystem _currency;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp()
        {
            _currency = new TestableCurrencySystem();
            _currency.Award(ClientA, 5); // 잔액 5로 시작
            _currency.SyncCalls.Clear(); // Award Sync 기록 초기화
        }

        /// <summary>AC-3: 잔액 부족 시 TrySpend가 false를 반환한다.</summary>
        [Test]
        public void test_tryspend_returns_false_when_insufficient()
        {
            // Arrange
            // 잔액 = 5, 소비 요청 = 50

            // Act
            bool result = _currency.TrySpend(ClientA, 50);

            // Assert
            Assert.IsFalse(result);
        }

        /// <summary>AC-3: 잔액 부족 시 TrySpend 후 잔액이 변하지 않는다.</summary>
        [Test]
        public void test_tryspend_does_not_modify_on_failure()
        {
            // Arrange
            // 잔액 = 5

            // Act
            _currency.TrySpend(ClientA, 50);

            // Assert
            Assert.AreEqual(5, _currency.GetBalance(ClientA));
            Assert.AreEqual(0, _currency.SyncCalls.Count, "실패 시 SyncClientRpc가 호출되지 않아야 한다");
        }
    }

    // ──────────────────────────────────────────────────────────
    // [Edge-1, Edge-2, Edge-3] 경계 케이스
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("CurrencySystem")]
    public class CurrencySystemEdgeCaseTests
    {
        private TestableCurrencySystem _currency;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp() => _currency = new TestableCurrencySystem();

        /// <summary>Edge-1: Dictionary에 없는 신규 클라이언트에게 Award 시 0에서 시작한다.</summary>
        [Test]
        public void test_new_client_award_starts_from_zero()
        {
            // Arrange
            // ClientA는 _balances에 미등록 상태

            // Act
            _currency.Award(ClientA, 10);

            // Assert
            Assert.AreEqual(10, _currency.GetBalance(ClientA), "신규 클라이언트는 TryAdd로 0 초기화 후 지급되어야 한다");
        }

        /// <summary>Edge-2: 잔액 == amount 정확히 일치 시 TrySpend 성공하고 잔액 0이 된다.</summary>
        [Test]
        public void test_tryspend_succeeds_when_balance_exact()
        {
            // Arrange
            _currency.Award(ClientA, 50); // 잔액 50

            // Act
            bool result = _currency.TrySpend(ClientA, 50);

            // Assert
            Assert.IsTrue(result);
            Assert.AreEqual(0, _currency.GetBalance(ClientA));
        }

        /// <summary>Edge-3: Award 연속 + TrySpend 후 잔액이 정확히 계산된다.</summary>
        [Test]
        public void test_sequential_award_and_spend_accuracy()
        {
            // Arrange
            // 잔액 0에서 시작

            // Act: Award(10), Award(20), TrySpend(15)
            _currency.Award(ClientA, 10);
            _currency.Award(ClientA, 20);
            _currency.TrySpend(ClientA, 15);

            // Assert: 0 + 10 + 20 - 15 = 15
            Assert.AreEqual(15, _currency.GetBalance(ClientA));
        }
    }
}
