// FameSystem — Unit Tests
// Story: production/epics/epic-progression-economy/story-005-fame-system.md
// ADR: docs/architecture/ADR-012-fame-system-server-dictionary.md
//
// 자동화 범위:
//   AC-1: GainFame → GetFame 증가
//   AC-2: LoseFame + 0 하한선
//   AC-3: CheckFame 통과 (소비 없음 확인)
//   AC-4: CheckFame 실패
//   Edge-1: 미등록 클라이언트 CheckFame(required=0) → true
//   Edge-2: 복수 플레이어 독립 관리
//
// 플레이테스트 범위: NGO IsServer 가드 (NetworkBehaviour.IsServer 의존)

using NUnit.Framework;
using System.Collections.Generic;
using System;

namespace BeTheKing.Tests.Unit.Progression
{
    // ──────────────────────────────────────────────────────────
    // NGO 의존성을 제거한 순수 C# 래퍼
    // NetworkBehaviour.IsServer가 EditMode에서 항상 false이므로
    // 로직만 추출하여 검증한다. (currency_system_test.cs 동일 패턴)
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// FameSystem의 핵심 로직을 NGO 없이 검증하기 위한 테스트 전용 래퍼.
    /// </summary>
    internal class TestableFameSystem
    {
        private readonly Dictionary<ulong, int> _fame = new();

        public void GainFame(ulong clientId, int amount)
        {
            _fame[clientId] = GetFame(clientId) + amount;
        }

        public void LoseFame(ulong clientId, int amount)
        {
            _fame[clientId] = Math.Max(0, GetFame(clientId) - amount);
        }

        public bool CheckFame(ulong clientId, int required) => GetFame(clientId) >= required;

        public int GetFame(ulong clientId) => _fame.GetValueOrDefault(clientId, 0);
    }

    // ──────────────────────────────────────────────────────────
    // [AC-1] GainFame — 명성치 증가
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("FameSystem")]
    public class FameSystemGainTests
    {
        private TestableFameSystem _fame;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp() => _fame = new TestableFameSystem();

        /// <summary>AC-1: GainFame 후 GetFame이 정확히 증가한다.</summary>
        [Test]
        public void test_fameSystem_gainFame_increasesFameCorrectly()
        {
            // Arrange
            // 초기 명성치 0

            // Act
            _fame.GainFame(ClientA, 50);

            // Assert
            Assert.AreEqual(50, _fame.GetFame(ClientA));
        }

        /// <summary>AC-1: GainFame 연속 호출 시 누적 합산된다.</summary>
        [Test]
        public void test_fameSystem_gainFame_accumulatesOnMultipleCalls()
        {
            // Arrange
            // 초기 명성치 0

            // Act
            _fame.GainFame(ClientA, 30);
            _fame.GainFame(ClientA, 20);

            // Assert
            Assert.AreEqual(50, _fame.GetFame(ClientA));
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-2] LoseFame — 명성치 감소 + 0 하한선
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("FameSystem")]
    public class FameSystemLoseTests
    {
        private TestableFameSystem _fame;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp()
        {
            _fame = new TestableFameSystem();
            _fame.GainFame(ClientA, 30); // 명성치 30으로 시작
        }

        /// <summary>AC-2: LoseFame 후 명성치가 정확히 감소한다.</summary>
        [Test]
        public void test_fameSystem_loseFame_decreasesFameCorrectly()
        {
            // Arrange
            // 명성치 = 30

            // Act
            _fame.LoseFame(ClientA, 10);

            // Assert
            Assert.AreEqual(20, _fame.GetFame(ClientA));
        }

        /// <summary>AC-2: LoseFame 결과가 0 미만이 되면 0으로 고정된다.</summary>
        [Test]
        public void test_fameSystem_loseFame_clampedToZeroMinimum()
        {
            // Arrange
            // 명성치 = 30, 감소 요청 = 100 (초과)

            // Act
            _fame.LoseFame(ClientA, 100);

            // Assert
            Assert.AreEqual(0, _fame.GetFame(ClientA), "명성치는 0 미만으로 내려갈 수 없다");
        }

        /// <summary>AC-2: LoseFame이 정확히 0이 되는 경우 0을 반환한다.</summary>
        [Test]
        public void test_fameSystem_loseFame_exactZeroResult()
        {
            // Arrange
            // 명성치 = 30

            // Act
            _fame.LoseFame(ClientA, 30);

            // Assert
            Assert.AreEqual(0, _fame.GetFame(ClientA));
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-3, AC-4] CheckFame — 자격 검사 (소비 없음)
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("FameSystem")]
    public class FameSystemCheckTests
    {
        private TestableFameSystem _fame;
        private const ulong ClientA = 1UL;

        [SetUp]
        public void SetUp()
        {
            _fame = new TestableFameSystem();
            _fame.GainFame(ClientA, 50); // 명성치 50으로 시작
        }

        /// <summary>AC-3: 명성치가 요구치 이상이면 CheckFame이 true를 반환한다.</summary>
        [Test]
        public void test_fameSystem_checkFame_returnsTrueWhenSufficient()
        {
            // Arrange
            // 명성치 = 50, 요구치 = 50

            // Act
            bool result = _fame.CheckFame(ClientA, 50);

            // Assert
            Assert.IsTrue(result);
        }

        /// <summary>AC-3: CheckFame 후 명성치가 소비되지 않는다.</summary>
        [Test]
        public void test_fameSystem_checkFame_doesNotConsumeFame()
        {
            // Arrange
            // 명성치 = 50

            // Act
            _fame.CheckFame(ClientA, 30);

            // Assert
            Assert.AreEqual(50, _fame.GetFame(ClientA), "CheckFame은 명성치를 소비하지 않아야 한다");
        }

        /// <summary>AC-4: 명성치가 요구치 미만이면 CheckFame이 false를 반환한다.</summary>
        [Test]
        public void test_fameSystem_checkFame_returnsFalseWhenInsufficient()
        {
            // Arrange
            // 명성치 = 50, 요구치 = 100

            // Act
            bool result = _fame.CheckFame(ClientA, 100);

            // Assert
            Assert.IsFalse(result);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [Edge-1, Edge-2] 경계 케이스
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("FameSystem")]
    public class FameSystemEdgeCaseTests
    {
        private TestableFameSystem _fame;

        [SetUp]
        public void SetUp() => _fame = new TestableFameSystem();

        /// <summary>Edge-1: 미등록 클라이언트에게 CheckFame(required=0)은 true를 반환한다.</summary>
        [Test]
        public void test_fameSystem_unregisteredClient_checkFameZeroRequired_returnsTrue()
        {
            // Arrange
            const ulong UnknownClient = 99UL;
            // UnknownClient는 Dictionary에 미등록 상태

            // Act
            bool result = _fame.CheckFame(UnknownClient, 0);

            // Assert
            Assert.IsTrue(result, "미등록 클라이언트의 명성치 기본값 0은 required=0을 충족한다");
        }

        /// <summary>Edge-1: 미등록 클라이언트에게 GetFame은 0을 반환한다.</summary>
        [Test]
        public void test_fameSystem_unregisteredClient_getFame_returnsZero()
        {
            // Arrange
            const ulong UnknownClient = 99UL;

            // Act
            int fame = _fame.GetFame(UnknownClient);

            // Assert
            Assert.AreEqual(0, fame);
        }

        /// <summary>Edge-2: 복수 플레이어의 명성치가 독립적으로 관리된다.</summary>
        [Test]
        public void test_fameSystem_multipleClients_independentTracking()
        {
            // Arrange
            const ulong ClientA = 1UL;
            const ulong ClientB = 2UL;

            // Act
            _fame.GainFame(ClientA, 100);
            _fame.GainFame(ClientB, 30);
            _fame.LoseFame(ClientB, 10);

            // Assert
            Assert.AreEqual(100, _fame.GetFame(ClientA), "ClientA 명성치는 100이어야 한다");
            Assert.AreEqual(20, _fame.GetFame(ClientB), "ClientB 명성치는 20이어야 한다 (30-10)");
        }
    }
}
