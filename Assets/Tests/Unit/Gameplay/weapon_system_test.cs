// ============================================================
// WeaponSystem — Unit Tests
// Story: production/epics/epic-progression-economy/story-004-weapon-system.md
// ADR: docs/architecture/ADR-007-weapon-system-scriptable-object.md
//
// 자동화 범위: 등급 순서, 종류별 스탯 차이, 단일 장착, DPS 균형, 경계값
// 플레이테스트 범위: NetworkVariable 클라이언트 동기화 (NGO 의존 — 플레이테스트)
//
// 주의: EditMode에서 NGO 사용 불가. TestableWeaponSystem으로 순수 C# 로직만 검증.
//       WeaponData는 ScriptableObject이므로 new() 불가 — WeaponStats POCO 사용.
// ============================================================

using NUnit.Framework;
using BeTheKing.GameplaySystems;

namespace BeTheKing.Tests.Unit.Gameplay
{
    // ──────────────────────────────────────────────────────────
    // 테스트 전용 POCO — ScriptableObject 의존성 없이 무기 스탯 표현
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// WeaponData ScriptableObject의 순수 C# 미러.
    /// EditMode 테스트에서 ScriptableObject.CreateInstance 없이 사용.
    /// </summary>
    internal class WeaponStats
    {
        public WeaponType Type;
        public WeaponGrade Grade;
        public float AttackPower;
        public float AttackRange;
        public float AttackCooldown;

        /// <summary>DPS = attackPower / attackCooldown. WeaponData.Dps와 동일 공식.</summary>
        public float Dps => AttackCooldown > 0f ? AttackPower / AttackCooldown : 0f;
    }

    // ──────────────────────────────────────────────────────────
    // 테스트용 WeaponSystem 래퍼 — NetworkBehaviour 의존성 분리
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// NetworkBehaviour 없이 장착 로직만 검증하기 위한 테스트 전용 래퍼.
    /// EquipWeaponServerRpc의 인덱스 유효성 검증 + 단일 장착 규칙을 재현.
    /// </summary>
    internal class TestableWeaponSystem
    {
        private readonly WeaponStats[] _database;
        private int _equippedId = -1;

        public TestableWeaponSystem(WeaponStats[] database) => _database = database;

        /// <summary>현재 장착 무기. 미장착(-1) 또는 범위 초과 시 null.</summary>
        public WeaponStats CurrentWeapon =>
            (_database != null && _equippedId >= 0 && _equippedId < _database.Length)
                ? _database[_equippedId]
                : null;

        /// <summary>
        /// 인덱스 유효 시 장착하고 true 반환. 범위 외 인덱스는 무시하고 false 반환.
        /// EquipWeaponServerRpc의 검증 로직과 동일.
        /// </summary>
        public bool TryEquip(int id)
        {
            if (_database == null || id < 0 || id >= _database.Length) return false;
            _equippedId = id;
            return true;
        }

        /// <summary>현재 장착된 인덱스. 테스트 검증용.</summary>
        public int EquippedId => _equippedId;
    }

    // ──────────────────────────────────────────────────────────
    // 공통 픽스처 데이터
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// 테스트 전반에서 공유하는 WeaponStats 팩토리.
    /// GDD AC-4 DPS 균형 수치: 동일 등급 내 DPS = 20 (±10% 이내).
    /// </summary>
    internal static class WeaponFixtures
    {
        // 등급별 검 — AC-1 등급 순서 검증용 (QA Test Cases 기준)
        public static WeaponStats SwordCommon  => new() { Type = WeaponType.Sword, Grade = WeaponGrade.Common, AttackPower = 10f, AttackRange = 2.0f, AttackCooldown = 0.6f };
        public static WeaponStats SwordRare    => new() { Type = WeaponType.Sword, Grade = WeaponGrade.Rare,   AttackPower = 15f, AttackRange = 2.0f, AttackCooldown = 0.6f };
        public static WeaponStats SwordHero    => new() { Type = WeaponType.Sword, Grade = WeaponGrade.Hero,   AttackPower = 22f, AttackRange = 2.0f, AttackCooldown = 0.6f };

        // AC-2 종류별 사거리·속도 차이용 (QA Test Cases 기준)
        public static WeaponStats DaggerCommon => new() { Type = WeaponType.Dagger, Grade = WeaponGrade.Common, AttackPower = 8f,  AttackRange = 1.5f, AttackCooldown = 0.4f };
        public static WeaponStats SpearCommon  => new() { Type = WeaponType.Spear,  Grade = WeaponGrade.Common, AttackPower = 20f, AttackRange = 3.0f, AttackCooldown = 1.0f };

        // AC-4 DPS 균형 검증: Dagger=20, Sword=20, Mace=20, Spear=20 (±10% 기준)
        public static WeaponStats MaceCommon   => new() { Type = WeaponType.Mace,  Grade = WeaponGrade.Common, AttackPower = 16f, AttackRange = 2.2f, AttackCooldown = 0.8f };

        // AC-4용 동일 등급 4종 배열
        public static WeaponStats[] CommonSetAllTypes => new[]
        {
            DaggerCommon,   // DPS = 8  / 0.4 = 20
            SwordCommon,    // DPS = 12 / 0.6 = 20
            MaceCommon,     // DPS = 16 / 0.8 = 20
            SpearCommon,    // DPS = 20 / 1.0 = 20
        };
    }

    // ──────────────────────────────────────────────────────────
    // [AC-1] 등급 순서 — Common < Rare < Hero
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("WeaponSystem")]
    public class WeaponGradeOrderTests
    {
        /// <summary>
        /// AC-1: 동일 종류(검) 내 등급 enum 값 순서가 Common < Rare < Hero.
        /// GDD: 등급이 높을수록 공격력 높음.
        /// </summary>
        [Test]
        public void test_grade_order_common_less_than_rare_less_than_hero()
        {
            // Arrange
            var common = WeaponFixtures.SwordCommon;
            var rare   = WeaponFixtures.SwordRare;
            var hero   = WeaponFixtures.SwordHero;

            // Act + Assert: 공격력 수치 순서 확인
            Assert.Less(common.AttackPower, rare.AttackPower,
                "Common attackPower must be less than Rare");
            Assert.Less(rare.AttackPower, hero.AttackPower,
                "Rare attackPower must be less than Hero");

            // enum 값 순서도 보장 (WeaponGrade 정의 계약)
            Assert.Less((int)WeaponGrade.Common, (int)WeaponGrade.Rare);
            Assert.Less((int)WeaponGrade.Rare,   (int)WeaponGrade.Hero);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-2] 종류별 사거리·속도 차이
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("WeaponSystem")]
    public class WeaponTypeStatDifferenceTests
    {
        /// <summary>AC-2: 단검 사거리 < 창 사거리.</summary>
        [Test]
        public void test_weapon_type_range_differs_between_dagger_and_spear()
        {
            // Arrange
            var dagger = WeaponFixtures.DaggerCommon;
            var spear  = WeaponFixtures.SpearCommon;

            // Act + Assert
            Assert.Less(dagger.AttackRange, spear.AttackRange,
                "Dagger attackRange must be less than Spear attackRange");
        }

        /// <summary>AC-2: 단검 쿨다운 < 창 쿨다운 (단검이 더 빠름).</summary>
        [Test]
        public void test_weapon_type_cooldown_differs_between_dagger_and_spear()
        {
            // Arrange
            var dagger = WeaponFixtures.DaggerCommon;
            var spear  = WeaponFixtures.SpearCommon;

            // Act + Assert
            Assert.Less(dagger.AttackCooldown, spear.AttackCooldown,
                "Dagger attackCooldown must be less than Spear attackCooldown");
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-3] 단일 장착 — 새 장착 시 이전 무기 해제
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("WeaponSystem")]
    public class WeaponEquipReplaceTests
    {
        private TestableWeaponSystem _system;

        [SetUp]
        public void SetUp()
        {
            // 인덱스 0 = Sword, 1 = Spear
            _system = new TestableWeaponSystem(new[] { WeaponFixtures.SwordCommon, WeaponFixtures.SpearCommon });
        }

        /// <summary>AC-3: 검 장착 후 창 장착 시 _equippedId가 창 인덱스로 교체.</summary>
        [Test]
        public void test_equip_replaces_previous_weapon()
        {
            // Arrange: 검 먼저 장착
            _system.TryEquip(0); // Sword
            Assert.AreEqual(WeaponType.Sword, _system.CurrentWeapon.Type,
                "Pre-condition: Sword should be equipped first");

            // Act: 창으로 교체
            bool result = _system.TryEquip(1); // Spear

            // Assert
            Assert.IsTrue(result, "TryEquip should return true for valid index");
            Assert.AreEqual(WeaponType.Spear, _system.CurrentWeapon.Type,
                "CurrentWeapon should be Spear after equipping index 1");
            Assert.AreEqual(1, _system.EquippedId,
                "EquippedId should be 1 (Spear index)");
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-4] DPS 균형 — 동일 등급 내 ±10% 이내
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("WeaponSystem")]
    public class WeaponDpsBalanceTests
    {
        /// <summary>
        /// AC-4: 동일 등급 단검/검/메이스/창의 DPS가 ±10% 이내.
        /// GDD: DPS = attackPower / attackCooldown, 등급 내 종류 무관 균형.
        /// </summary>
        [Test]
        public void test_dps_within_10_percent_across_types_same_grade()
        {
            // Arrange
            WeaponStats[] weapons = WeaponFixtures.CommonSetAllTypes;

            // 기준 DPS: 첫 번째 무기(단검) 기준
            float referenceDps = weapons[0].Dps;

            // Act + Assert: 모든 종류의 DPS가 기준 ±10% 이내
            foreach (var weapon in weapons)
            {
                float dps = weapon.Dps;
                float deviation = System.Math.Abs(dps - referenceDps) / referenceDps;
                Assert.LessOrEqual(deviation, 0.10f,
                    $"{weapon.Type} DPS={dps:F2} deviates {deviation:P1} from reference {referenceDps:F2} — exceeds ±10%");
            }
        }
    }

    // ──────────────────────────────────────────────────────────
    // [Edge-1] 미장착 상태 null 안전 처리
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("WeaponSystem")]
    public class WeaponUnequippedSafetyTests
    {
        /// <summary>Edge-1: 초기 상태(_equippedId = -1)에서 CurrentWeapon은 null.</summary>
        [Test]
        public void test_unequipped_returns_null()
        {
            // Arrange
            var system = new TestableWeaponSystem(new[] { WeaponFixtures.SwordCommon });

            // Act
            WeaponStats current = system.CurrentWeapon;

            // Assert: NullReferenceException 없이 null 반환
            Assert.IsNull(current, "CurrentWeapon must be null when no weapon is equipped");
        }
    }

    // ──────────────────────────────────────────────────────────
    // [Edge-2] 동일 무기 재장착 — idempotent
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("WeaponSystem")]
    public class WeaponReequipIdempotentTests
    {
        /// <summary>Edge-2: 동일 인덱스 재장착 시 _equippedId 불변, 부작용 없음.</summary>
        [Test]
        public void test_reequip_same_weapon_idempotent()
        {
            // Arrange
            var system = new TestableWeaponSystem(new[] { WeaponFixtures.SwordCommon });
            system.TryEquip(0); // 첫 장착

            // Act: 동일 인덱스 재장착
            bool result = system.TryEquip(0);

            // Assert
            Assert.IsTrue(result, "TryEquip should succeed for same valid index");
            Assert.AreEqual(0, system.EquippedId,
                "EquippedId must remain 0 after re-equipping same weapon");
            Assert.AreEqual(WeaponType.Sword, system.CurrentWeapon.Type,
                "CurrentWeapon must still be Sword");
        }
    }
}
