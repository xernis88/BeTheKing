// Implements: production/epics/epic-ui-presentation/story-006-inventory-screen.md — 서버 권위 장착 컴포넌트
// GDD: design/GAME_RULE.md §6-B (전투중 장착·해제 차단)
// GDD: design/gdd/03-progression-economy.md — WeaponSystem, TraitTreeSystem
// ADR-004: NGO Host-Client 토폴로지 (서버 권위)
// ADR-007: WeaponSystem — 무기 등급은 서버가 데이터 조회 (클라이언트 결정 금지)
// ADR-015: TraitTreeSystem — 서버 Dictionary + ServerRpc 포인트 투자
//
// 설계 결정 (game-server-engineer):
//   - 이 컴포넌트는 "장착 요청의 서버 권위 게이트키퍼"다. 실제 무기 인덱스 상태는
//     WeaponSystem(ADR-007)이, 특성 포인트 상태는 TraitTreeSystem(ADR-015)이 보유한다.
//     본 컴포넌트는 보안 검증(소유·전투상태·레이트리밋)을 통과시킨 뒤 그 시스템에 위임하고,
//     UI가 구독할 instanceId 미러를 NetworkVariable로 노출한다.
//   - NetworkVariable 3종은 ReadPermission.Owner — 자기 자신에게만 노출 (신원 비공개 원칙).
//   - 결과 통지 ClientRpc는 TargetClientIds로 송신자 1인에게만 전송 (DisguiseSystem 패턴).
//   - 무기 grade는 절대 클라이언트가 보내지 않는다. 서버가 WeaponData.grade에서 조회 (ADR-007).
//
// 미구현 시스템 (인터페이스 스텁 + TODO — 코드 발명 금지 원칙):
//   - CombatSystem: IsInCombat 조회 API 부재. ResolveInCombat()가 격리 지점.
//   - 서버 인벤토리 소유 레코드: 부재. OwnsCostume/OwnsWeapon()이 격리 지점.
//   - TraitTreeSystem prerequisite/nodeId 그래프: 부재(3방향 단순 투자만 존재).
//     ResolveTraitNode()가 nodeId→TraitDirection 매핑 + 선행/중복 검증 격리 지점.

using System;
using System.Collections.Generic;
using BeTheKing.GameplaySystems;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.Networking
{
    /// <summary>
    /// 장착·해제·특성 투자 요청을 서버 권위로 처리하는 컴포넌트. PlayerObject에 부착한다.
    /// <para>
    ///   UI(ui-programmer 담당 InventoryScreen)는 이 컴포넌트의 <c>Request*</c> 공개 메서드로
    ///   요청을 보내고, <see cref="OnEquipResultReceived"/> / <see cref="OnStatsUpdated"/>
    ///   이벤트로 결과를 수신한다. UI는 ServerRpc를 직접 호출하지 않는다.
    /// </para>
    /// <para>
    ///   보안 원칙 (story-006 §서버 보안 제약):
    ///   <list type="number">
    ///     <item>모든 장착 ServerRpc는 <c>[ServerRpc(RequireOwnership = true)]</c> — 임퍼소네이션 방지.</item>
    ///     <item>송신자 식별은 페이로드가 아닌 <c>ServerRpcParams.Receive.SenderClientId</c>.</item>
    ///     <item>무기 grade는 서버가 데이터에서 조회 — 클라이언트 전송 금지 (ADR-007).</item>
    ///     <item>InCombat은 서버가 독립 재검증 (GAME_RULE §6-B). 클라이언트 차단은 UX용.</item>
    ///     <item>소유 검증은 서버 권위 인벤토리 레코드 기준.</item>
    ///     <item>특성 포인트: 잔여량·선행 노드·중복 투자 단일 트랜잭션 검증 (ADR-015).</item>
    ///     <item>장착 ServerRpc 레이트 리밋: 플레이어당 ≥100ms 간격, 초과분 드롭.</item>
    ///   </list>
    /// </para>
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class InventoryEquipComponent : NetworkBehaviour
    {
        // ── 상수 ───────────────────────────────────────────────────────────────

        /// <summary>장착 ServerRpc 최소 간격(초). story-006 §서버 보안 제약 7: 플레이어당 ≥100ms.</summary>
        private const double RpcMinIntervalSeconds = 0.1;

        /// <summary>NetworkVariable의 "미장착" 센티넬. 빈 FixedString과 동일.</summary>
        private static readonly FixedString64Bytes Unequipped = default;

        // ── 결과 코드 ──────────────────────────────────────────────────────────

        /// <summary>장착·해제·투자 요청의 서버 판정 결과. UI가 낙관적 업데이트 확정/롤백에 사용한다.</summary>
        public enum EquipResult
        {
            /// <summary>요청 성공 — 서버 상태 반영 완료.</summary>
            Success,

            /// <summary>해당 아이템을 서버 인벤토리 레코드에서 소유하지 않음.</summary>
            RejectedNotOwned,

            /// <summary>전투중(InCombat) 상태 — 장착·해제 차단 (GAME_RULE §6-B).</summary>
            RejectedInCombat,

            /// <summary>특성 포인트 잔여량 부족.</summary>
            RejectedInsufficientPoints,

            /// <summary>특성 선행 노드 미해금.</summary>
            RejectedPrerequisiteNotMet,

            /// <summary>이미 투자된 노드 — 중복 투자 거부 (idempotency).</summary>
            RejectedDuplicate,

            /// <summary>서버 미응답 — 클라이언트 측 타임아웃. 서버는 이 값을 발행하지 않는다.</summary>
            Timeout,
        }

        /// <summary>결과 ClientRpc가 어떤 요청 종류에 대한 응답인지 구분한다. UI 슬롯 롤백 대상 식별용.</summary>
        public enum EquipSlot
        {
            Costume = 0,
            Weapon  = 1,
            Trait   = 2,
        }

        // ── 네트워크 상태 (ReadPermission.Owner — 자기 자신에게만 노출) ─────────────
        //
        // 신원 비공개 원칙: 타 플레이어는 이 값을 읽을 수 없다. 복장 "외형"은 DisguiseSystem이
        // 별도로 동기화하며, 이 컴포넌트는 instanceId 미러만 보유한다 (외형 아님).

        private readonly NetworkVariable<FixedString64Bytes> _equippedCostumeId = new(
            Unequipped,
            NetworkVariableReadPermission.Owner,
            NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<FixedString64Bytes> _equippedWeaponId = new(
            Unequipped,
            NetworkVariableReadPermission.Owner,
            NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<int> _remainingTraitPoints = new(
            0,
            NetworkVariableReadPermission.Owner,
            NetworkVariableWritePermission.Server);

        // ── 서버 전용 상태 ─────────────────────────────────────────────────────

        // 레이트 리밋: clientId → 마지막 수락 ServerRpc 시각(서버 시간). 서버 메모리 전용.
        private readonly Dictionary<ulong, double> _lastRpcTime = new();

        // ── UI 구독 이벤트 (로컬 클라이언트 전용) ───────────────────────────────

        /// <summary>
        /// 서버 장착·해제·투자 판정 결과. 로컬 소유 클라이언트에만 발화된다.
        /// UI는 이를 받아 낙관적 업데이트를 확정하거나 롤백한다.
        /// </summary>
        public event Action<EquipSlot, EquipResult> OnEquipResultReceived;

        /// <summary>
        /// 특성 투자 후 변경된 스탯을 UI에 통지한다. 로컬 소유 클라이언트에만 발화.
        /// <para>인자: (nodeId, stat 값 배열). 스탯 배열의 의미는 TraitTreeSystem 스탯 스키마 확정 후 정의.</para>
        /// </summary>
        public event Action<string, float[]> OnStatsUpdated;

        // ── 외부 의존성 (서버 측, Inspector 또는 런타임 바인딩) ──────────────────

        [Header("의존 시스템 — 서버에서만 사용")]
        [Tooltip("같은 PlayerObject의 WeaponSystem. 비어 있으면 OnNetworkSpawn에서 GetComponent로 캐싱.")]
        [SerializeField] private WeaponSystem _weaponSystem;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            // WeaponSystem은 같은 PlayerObject에 부착되어 있어야 한다 (ADR-007).
            if (_weaponSystem == null)
                _weaponSystem = GetComponent<WeaponSystem>();

            // 로컬 소유자만 NetworkVariable 변경을 UI로 연결한다 (ReadPermission.Owner).
            if (IsOwner)
            {
                _equippedCostumeId.OnValueChanged += OnCostumeIdChanged;
                _equippedWeaponId.OnValueChanged += OnWeaponIdChanged;
                _remainingTraitPoints.OnValueChanged += OnTraitPointsChanged;
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsOwner)
            {
                _equippedCostumeId.OnValueChanged -= OnCostumeIdChanged;
                _equippedWeaponId.OnValueChanged -= OnWeaponIdChanged;
                _remainingTraitPoints.OnValueChanged -= OnTraitPointsChanged;
            }

            if (IsServer)
                _lastRpcTime.Clear();
        }

        // ── 공개 API (ui-programmer가 호출 — 로컬 클라이언트 전용) ────────────────

        /// <summary>복장 장착을 요청한다. 로컬 소유 클라이언트에서만 호출해야 한다.</summary>
        /// <param name="costumeInstanceId">서버 인벤토리 레코드 기준 복장 인스턴스 ID.</param>
        public void RequestEquipCostume(string costumeInstanceId)
        {
            if (string.IsNullOrEmpty(costumeInstanceId)) return;
            EquipCostumeServerRpc(new FixedString64Bytes(costumeInstanceId));
        }

        /// <summary>무기 장착을 요청한다. grade는 절대 전송하지 않는다 (서버 조회 — ADR-007).</summary>
        /// <param name="weaponInstanceId">서버 인벤토리 레코드 기준 무기 인스턴스 ID.</param>
        public void RequestEquipWeapon(string weaponInstanceId)
        {
            if (string.IsNullOrEmpty(weaponInstanceId)) return;
            EquipWeaponServerRpc(new FixedString64Bytes(weaponInstanceId));
        }

        /// <summary>현재 복장을 해제한다.</summary>
        public void RequestUnequipCostume() => UnequipCostumeServerRpc();

        /// <summary>현재 무기를 해제한다.</summary>
        public void RequestUnequipWeapon() => UnequipWeaponServerRpc();

        /// <summary>특성 노드에 포인트를 투자한다. traitDirection은 서버가 nodeId에서 도출한다.</summary>
        /// <param name="nodeId">특성 트리 노드 ID. 클라이언트는 방향을 결정하지 않는다 (보안 §5).</param>
        public void RequestInvestTraitNode(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return;
            InvestTraitNodeServerRpc(new FixedString64Bytes(nodeId));
        }

        // ── ServerRpc: 복장 ─────────────────────────────────────────────────────

        [ServerRpc(RequireOwnership = true)]
        private void EquipCostumeServerRpc(FixedString64Bytes costumeInstanceId, ServerRpcParams p = default)
        {
            ulong senderId = p.Receive.SenderClientId;

            // 보안 §7: 레이트 리밋 — 100ms 미만 간격 요청은 침묵 드롭(응답 없음). 클라 타임아웃이 처리.
            if (!PassRateLimit(senderId)) return;

            // 보안 §3: 서버 독립 InCombat 재검증 (GAME_RULE §6-B).
            if (ResolveInCombat(senderId))
            {
                ReplyTo(senderId, EquipSlot.Costume, EquipResult.RejectedInCombat);
                return;
            }

            // 보안 §4: 서버 권위 인벤토리 소유 검증.
            if (!OwnsCostume(senderId, costumeInstanceId))
            {
                ReplyTo(senderId, EquipSlot.Costume, EquipResult.RejectedNotOwned);
                return;
            }

            // 상태 반영 — 외형 교체는 DisguiseSystem 책임. 여기서는 instanceId 미러만 갱신.
            _equippedCostumeId.Value = costumeInstanceId;

            // TODO(DisguiseSystem 연동): costumeInstanceId → jobId 해석 후 외형 적용 트리거.
            //   DisguiseSystem은 PlayerManager.OnPlayerSpawnedWithJob 경로로 동작하므로
            //   런타임 복장 변경 훅이 추가되면 이 지점에서 서버 호출.

            ReplyTo(senderId, EquipSlot.Costume, EquipResult.Success);
        }

        [ServerRpc(RequireOwnership = true)]
        private void UnequipCostumeServerRpc(ServerRpcParams p = default)
        {
            ulong senderId = p.Receive.SenderClientId;
            if (!PassRateLimit(senderId)) return;

            if (ResolveInCombat(senderId))
            {
                ReplyTo(senderId, EquipSlot.Costume, EquipResult.RejectedInCombat);
                return;
            }

            _equippedCostumeId.Value = Unequipped;
            ReplyTo(senderId, EquipSlot.Costume, EquipResult.Success);
        }

        // ── ServerRpc: 무기 ─────────────────────────────────────────────────────

        [ServerRpc(RequireOwnership = true)]
        private void EquipWeaponServerRpc(FixedString64Bytes weaponInstanceId, ServerRpcParams p = default)
        {
            ulong senderId = p.Receive.SenderClientId;
            if (!PassRateLimit(senderId)) return;

            // 보안 §3: 서버 독립 InCombat 재검증 (GAME_RULE §6-B — 전투중 무기 교체 차단).
            if (ResolveInCombat(senderId))
            {
                ReplyTo(senderId, EquipSlot.Weapon, EquipResult.RejectedInCombat);
                return;
            }

            // 보안 §4: 서버 권위 소유 검증. 통과 시 서버가 무기 데이터 인덱스를 도출한다.
            if (!TryResolveWeapon(senderId, weaponInstanceId, out int weaponDataId, out WeaponGrade grade))
            {
                ReplyTo(senderId, EquipSlot.Weapon, EquipResult.RejectedNotOwned);
                return;
            }

            // 보안 §2: grade는 서버가 WeaponData에서 조회한 값. 클라이언트는 grade를 보내지 않았다.
            //   (grade는 현재 검증 로깅/향후 등급 제한 정책 훅을 위한 서버 보유값.)
            _ = grade;

            // ADR-007: 실제 장착 상태(NetworkVariable<int>)는 WeaponSystem이 권위 보유한다.
            //   이 컴포넌트는 검증을 통과시킨 뒤 서버 측에서 WeaponSystem에 위임한다.
            if (_weaponSystem != null)
                _weaponSystem.EquipWeaponServerRpc(weaponDataId);
            // NOTE: WeaponSystem.EquipWeaponServerRpc는 [ServerRpc]이며 서버에서 직접 호출 시
            //   서버 로컬 실행된다(NGO). 별도 ClientRpc 불필요.

            _equippedWeaponId.Value = weaponInstanceId;
            ReplyTo(senderId, EquipSlot.Weapon, EquipResult.Success);
        }

        [ServerRpc(RequireOwnership = true)]
        private void UnequipWeaponServerRpc(ServerRpcParams p = default)
        {
            ulong senderId = p.Receive.SenderClientId;
            if (!PassRateLimit(senderId)) return;

            if (ResolveInCombat(senderId))
            {
                ReplyTo(senderId, EquipSlot.Weapon, EquipResult.RejectedInCombat);
                return;
            }

            // TODO(WeaponSystem 연동): WeaponSystem에 명시적 해제 API(EquipWeaponServerRpc(-1) 등)
            //   추가 시 위임. 현재 WeaponSystem은 -1 인덱스를 미장착으로 처리하나 음수 인덱스를
            //   거부하므로(범위 검증), 해제 전용 API가 필요하다. 여기서는 미러만 갱신.
            _equippedWeaponId.Value = Unequipped;
            ReplyTo(senderId, EquipSlot.Weapon, EquipResult.Success);
        }

        // ── ServerRpc: 특성 투자 ─────────────────────────────────────────────────

        [ServerRpc(RequireOwnership = true)]
        private void InvestTraitNodeServerRpc(FixedString64Bytes nodeId, ServerRpcParams p = default)
        {
            ulong senderId = p.Receive.SenderClientId;
            if (!PassRateLimit(senderId)) return;

            // 보안 §5: 단일 트랜잭션 검증 — 잔여량·선행·중복.
            //   traitDirection은 서버가 nodeId에서 도출한다 (클라이언트 결정 금지).
            EquipResult validation = ValidateTraitInvestment(senderId, nodeId, out TraitDirection direction);
            if (validation != EquipResult.Success)
            {
                ReplyTo(senderId, EquipSlot.Trait, validation);
                return;
            }

            // ADR-015: 포인트 상태는 TraitTreeSystem이 서버 권위 보유한다.
            //   TraitTreeSystem.InvestPointServerRpc는 [ServerRpc(RequireOwnership=false)]이며
            //   서버에서 직접 호출 시 서버 로컬 실행되고, 내부에서 SenderClientId를 사용한다.
            //   서버 직접 호출 시 SenderClientId는 서버(0)가 되므로 ADR-015 API로는 senderId를
            //   전달할 수 없다 → 이 통합은 TraitTreeSystem에 clientId 인자 API가 필요하다.
            // TODO(TraitTreeSystem 연동): TraitTreeSystem에 서버 직접 호출용
            //   InvestPoint(ulong clientId, TraitDirection) 공개 API 추가 필요.
            //   현재 ADR-015 시그니처(InvestPointServerRpc(TraitDirection))로는 임의 clientId 투자 불가.
            //   API 추가 전까지 본 컴포넌트는 자체 _remainingTraitPoints 미러만 차감한다.

            if (_remainingTraitPoints.Value > 0)
                _remainingTraitPoints.Value -= 1;

            _ = direction; // 도출된 방향 — TraitTreeSystem 연동 API 추가 시 전달.

            ReplyTo(senderId, EquipSlot.Trait, EquipResult.Success);

            // TODO(스탯 스키마 확정): 투자 후 변경된 스탯 배열을 산출해 StatsUpdatedClientRpc로 전송.
            //   현재 TraitTreeSystem은 방향별 패시브만 적용하고 수치 스탯 배열을 노출하지 않는다.
            StatsUpdatedClientRpc(nodeId, Array.Empty<float>(), TargetOnly(senderId));
        }

        // ── ClientRpc: 결과 통지 (송신자 1인 한정) ────────────────────────────────

        /// <summary>
        /// 장착·해제·투자 판정 결과를 요청한 클라이언트에게만 전달한다.
        /// 신원 비공개: TargetClientIds로 송신자 1인에게만 송신 (DisguiseSystem 패턴).
        /// </summary>
        [ClientRpc]
        private void EquipResultClientRpc(EquipSlot slot, EquipResult result, ClientRpcParams p = default)
        {
            // slot은 UI가 어느 슬롯을 롤백/확정할지 식별하는 데 사용. 본 이벤트로 함께 전달.
            OnEquipResultReceived?.Invoke(slot, result);
        }

        /// <summary>
        /// 특성 투자 후 변경 스탯을 요청한 클라이언트에게만 전달한다.
        /// </summary>
        [ClientRpc]
        private void StatsUpdatedClientRpc(FixedString64Bytes nodeId, float[] stats, ClientRpcParams p = default)
        {
            OnStatsUpdated?.Invoke(nodeId.ToString(), stats ?? Array.Empty<float>());
        }

        // ── NetworkVariable → UI 브리지 (로컬 소유자) ────────────────────────────

        private void OnCostumeIdChanged(FixedString64Bytes _, FixedString64Bytes __) { /* UI 미러 — 폴링/표시는 UI 책임 */ }
        private void OnWeaponIdChanged(FixedString64Bytes _, FixedString64Bytes __) { /* UI 미러 */ }
        private void OnTraitPointsChanged(int _, int __) { /* UI 미러 */ }

        // ── 서버 검증 헬퍼 ─────────────────────────────────────────────────────

        /// <summary>
        /// 레이트 리밋 게이트. story-006 §7: 플레이어당 ≥100ms. 통과 시 마지막 시각을 갱신한다.
        /// </summary>
        /// <returns>요청을 처리해도 되면 true. 너무 빠르면 false(드롭).</returns>
        private bool PassRateLimit(ulong clientId)
        {
            double now = NetworkManager.ServerTime.Time;
            if (_lastRpcTime.TryGetValue(clientId, out double last)
                && now - last < RpcMinIntervalSeconds)
            {
                return false;
            }
            _lastRpcTime[clientId] = now;
            return true;
        }

        /// <summary>
        /// 서버 권위 InCombat 상태를 조회한다 (GAME_RULE §6-B).
        /// </summary>
        /// <returns>전투중이면 true.</returns>
        private bool ResolveInCombat(ulong clientId)
        {
            // TODO(CombatSystem 연동): CombatSystem이 IsInCombat(clientId) 서버 조회 API를
            //   제공하면 여기서 호출한다. 현재 CombatSystem 미구현(전 코드베이스에서 TODO 주석).
            //   안전 기본값: false (평화중) — 단, CombatSystem 구현 즉시 연결 필수.
            //   이 스텁이 살아있는 동안 전투중 장착 차단은 클라이언트 UX 차단에만 의존한다(위험).
            return false;
        }

        /// <summary>
        /// 복장 인스턴스를 서버 권위 인벤토리 레코드에서 소유하는지 검증한다.
        /// </summary>
        private bool OwnsCostume(ulong clientId, FixedString64Bytes costumeInstanceId)
        {
            // TODO(Inventory 연동): 서버 측 플레이어 인벤토리 레코드가 구현되면 소유 조회로 교체.
            //   현재 코드베이스에 서버 인벤토리 레코드 시스템 부재(LootManager는 드롭/스폰만 담당,
            //   인벤토리는 ItemData[]로 호출자가 전달). 보수적 기본값: 검증 실패(소유 안 함).
            //   주의: 이 스텁이 살아있는 동안 모든 복장 장착은 RejectedNotOwned로 거부된다.
            return false;
        }

        /// <summary>
        /// 무기 인스턴스 소유를 검증하고, 통과 시 서버가 WeaponData 인덱스와 등급을 도출한다.
        /// grade는 서버 데이터에서 조회한다 — 클라이언트가 보낸 값이 아니다 (ADR-007).
        /// </summary>
        /// <returns>소유하고 데이터 해석에 성공하면 true.</returns>
        private bool TryResolveWeapon(ulong clientId, FixedString64Bytes weaponInstanceId,
            out int weaponDataId, out WeaponGrade grade)
        {
            weaponDataId = -1;
            grade = WeaponGrade.Common;

            // TODO(Inventory 연동): 서버 인벤토리 레코드에서 weaponInstanceId → (소유 여부, ItemId)
            //   조회 후, ItemId → WeaponSystem._weaponDatabase 인덱스 → WeaponData.grade 도출.
            //   현재 서버 인벤토리 레코드 부재 + WeaponSystem._weaponDatabase는 private이라
            //   인덱스 역조회 API도 없다. 보수적 기본값: 해석 실패(소유 안 함).
            //   API 필요: WeaponSystem.TryGetWeaponIndexByItemId(int itemId, out int idx, out WeaponGrade grade)
            return false;
        }

        /// <summary>
        /// 특성 투자를 단일 트랜잭션으로 검증한다 (잔여량·선행·중복).
        /// traitDirection은 nodeId에서 서버가 도출한다 (보안 §5 — 클라이언트 결정 금지).
        /// </summary>
        /// <param name="direction">도출된 투자 방향. 검증 실패 시 기본값.</param>
        /// <returns><see cref="EquipResult.Success"/> 또는 거부 사유.</returns>
        private EquipResult ValidateTraitInvestment(ulong clientId, FixedString64Bytes nodeId,
            out TraitDirection direction)
        {
            direction = TraitDirection.Attack;

            // 잔여량 검증 — 서버 권위 잔여 포인트 기준 (자체 미러; ADR-015 연동 시 TraitTreeSystem 기준).
            // TODO(TraitTreeSystem 연동): TraitTreeSystem.GetAvailablePoints(clientId)로 교체.
            if (_remainingTraitPoints.Value <= 0)
                return EquipResult.RejectedInsufficientPoints;

            // TODO(TraitTree 그래프 확정 — story-006 OQ-04): nodeId → TraitDirection 매핑,
            //   선행 노드 해금 검증(RejectedPrerequisiteNotMet), 중복 투자 검증(RejectedDuplicate).
            //   현재 TraitTreeSystem(ADR-015)은 nodeId/prerequisite 그래프가 없고 3방향 단순 투자만
            //   지원한다. 그래프 스키마 확정 전까지 선행/중복 검증은 통과시킨다.

            return EquipResult.Success;
        }

        // ── ClientRpc 송신 헬퍼 ──────────────────────────────────────────────────

        /// <summary>송신자 1인에게만 보내는 ClientRpcParams를 만든다 (신원 비공개).</summary>
        private static ClientRpcParams TargetOnly(ulong clientId) => new()
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
        };

        /// <summary>판정 결과를 요청한 클라이언트에게만 통지한다.</summary>
        private void ReplyTo(ulong clientId, EquipSlot slot, EquipResult result)
            => EquipResultClientRpc(slot, result, TargetOnly(clientId));
    }
}
