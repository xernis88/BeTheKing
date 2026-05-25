// Implements: design/gdd/03-progression-economy.md — WeaponSystem
// Story: production/epics/epic-progression-economy/story-004-weapon-system.md
// ADR: docs/architecture/ADR-007-weapon-system-scriptable-object.md
//
// 설계 결정:
//   장착 상태는 WeaponData 배열 인덱스를 NetworkVariable<int>로 동기화 (서버 권위).
//   ScriptableObject는 NGO 직렬화 불가 — 인덱스 참조 방식이 표준 패턴 (ADR-007).
//   미장착 상태: _equippedWeaponId = -1, CurrentWeapon = null.

using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 플레이어의 무기 장착 상태를 관리한다. 서버 권위적.
    /// <para>
    ///   - <see cref="EquipWeaponServerRpc"/>: 서버에서 인덱스 유효성 검증 후 장착.
    ///   - <see cref="CurrentWeapon"/>: 현재 장착 무기의 ScriptableObject (클라이언트 읽기 가능).
    ///   - CombatSystem은 <see cref="CurrentWeapon"/>을 통해 공격력·사거리를 참조한다.
    /// </para>
    /// <para>
    ///   ADR-007: <c>_weaponDatabase</c> 배열은 모든 클라이언트에서 동일 순서로 설정 필수.
    ///   ScriptableObject는 네트워크 전송 불가 — 빌드 시 순서 보장.
    /// </para>
    /// </summary>
    public class WeaponSystem : NetworkBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("무기 데이터베이스 — 모든 클라이언트에서 동일 순서 필수 (ADR-007)")]
        [Tooltip("WeaponData ScriptableObject 배열. 인덱스가 네트워크 동기화 키.")]
        [SerializeField] private WeaponData[] _weaponDatabase;

        // ── 네트워크 상태 ──────────────────────────────────────────────────────

        // ADR-007: 서버 쓰기 / 클라이언트 읽기. int 1개 동기화로 네트워크 부하 최소.
        private readonly NetworkVariable<int> _equippedWeaponId = new(
            -1,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );

        // ── 프로퍼티 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 현재 장착 무기. 미장착(-1) 또는 인덱스 범위 초과 시 null.
        /// CombatSystem은 null 체크 후 스탯을 참조해야 한다.
        /// </summary>
        public WeaponData CurrentWeapon => GetWeaponData(_equippedWeaponId.Value);

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 지정한 인덱스의 무기를 장착한다. 서버에서만 실행.
        /// 기존 장착 무기는 즉시 해제된다 (단일 장착 규칙 — GDD).
        /// </summary>
        /// <param name="weaponDataId"><c>_weaponDatabase</c> 배열 인덱스. 유효 범위 외 무시.</param>
        [ServerRpc]
        public void EquipWeaponServerRpc(int weaponDataId)
        {
            // ADR-007: 서버에서 인덱스 유효성 검증 필수.
            if (_weaponDatabase == null) return;
            if (weaponDataId < 0 || weaponDataId >= _weaponDatabase.Length) return;

            _equippedWeaponId.Value = weaponDataId;
        }

        // ── 내부 ──────────────────────────────────────────────────────────────

        private WeaponData GetWeaponData(int id)
        {
            if (_weaponDatabase == null || id < 0 || id >= _weaponDatabase.Length)
                return null;
            return _weaponDatabase[id];
        }
    }
}
