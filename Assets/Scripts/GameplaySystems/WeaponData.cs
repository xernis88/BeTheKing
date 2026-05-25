// Implements: design/gdd/03-progression-economy.md — WeaponSystem
// Story: production/epics/epic-progression-economy/story-004-weapon-system.md
// ADR: docs/architecture/ADR-007-weapon-system-scriptable-object.md
//
// 설계 결정:
//   무기 스탯을 ScriptableObject로 정의 — Inspector에서 밸런스 조정 가능.
//   DPS = attackPower / attackCooldown 계산 프로퍼티로 등급 내 균형 검증 지원.

using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>무기 등급. 숫자값이 클수록 상위 등급. GDD: 일반 < 희귀 < 영웅.</summary>
    public enum WeaponGrade { Common = 0, Rare = 1, Hero = 2 }

    /// <summary>무기 종류. 각 종류별 사거리·공격속도가 다르다.</summary>
    public enum WeaponType { Dagger, Sword, Mace, Spear }

    /// <summary>
    /// 무기 1개의 스탯 데이터. ScriptableObject이므로 에셋으로 저장하고 Inspector에서 편집.
    /// <para>
    ///   ADR-007: 에셋은 <c>BeTheKing/Assets/Data/Weapons/</c> 아래에 생성.
    ///   <c>WeaponSystem._weaponDatabase</c> 배열에 등록해 인덱스로 참조.
    /// </para>
    /// </summary>
    [CreateAssetMenu(menuName = "BeTheKing/WeaponData")]
    public class WeaponData : ScriptableObject
    {
        [Header("분류")]
        [Tooltip("무기 종류 (단검/검/메이스/창).")]
        public WeaponType type;

        [Tooltip("무기 등급 (일반/희귀/영웅).")]
        public WeaponGrade grade;

        [Header("전투 스탯 — 밸런스 시 확정")]
        [Tooltip("기본 공격력. 등급이 높을수록 크다.")]
        public float attackPower;

        [Tooltip("공격 사거리 (Unity 유닛). 단검 최소, 창 최대.")]
        public float attackRange;

        [Tooltip("공격 쿨다운(초). 1 / 공격속도. 단검 최소, 창 최대.")]
        public float attackCooldown;

        /// <summary>
        /// 초당 딜량 (DPS). 등급 내 무기 종류 무관 동일하게 설계.
        /// GDD: DPS = attackPower / attackCooldown, ±10% 이내 균형.
        /// </summary>
        public float Dps => attackCooldown > 0f ? attackPower / attackCooldown : 0f;
    }
}
