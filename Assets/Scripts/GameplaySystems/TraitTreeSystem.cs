// Implements: design/gdd/03-progression-economy.md — TraitTreeSystem
// Story: production/epics/epic-progression-economy/story-003-trait-tree-system.md
// ADR: docs/architecture/ADR-015-trait-tree-system-server-dictionary.md
//
// 설계 결정:
//   포인트 상태는 서버 전용 Dictionary<ulong, TraitPoints>로 관리.
//   이유: 타 플레이어에게 특성 노출 방지 + 클라이언트 위변조 불가 (ADR-015).
//   스킬북 획득: OnSkillBookAcquired(서버 직접 호출) → Available++.
//   투자: InvestPointServerRpc → 서버 검증 후 패시브 효과 ClientRpc.
//   공격형 패시브: ApplyAttackTraitClientRpc → IsOwner 가드 후 FootprintSystem 호출.
//   생존/암살형 패시브: StaminaSystem/MovementSystem 연동은 해당 시스템 구현 후 추가 예정.

using Unity.Netcode;
using System.Collections.Generic;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 3방향 특성(공격/생존/암살) 투자 방향.
    /// </summary>
    public enum TraitDirection
    {
        Attack   = 0,
        Survival = 1,
        Assassin = 2,
    }

    /// <summary>
    /// 클라이언트별 특성 포인트 상태. 서버 전용 — 외부 직접 접근 불가.
    /// </summary>
    internal class TraitPoints
    {
        /// <summary>투자 가능한 잔여 포인트 수.</summary>
        public int Available;

        /// <summary>방향별 투자량. 인덱스는 <see cref="TraitDirection"/> 정수값과 대응.</summary>
        public readonly int[] Invested = new int[3];
    }

    /// <summary>
    /// 플레이어 특성 포인트의 획득·투자를 서버 권위적으로 관리한다.
    /// <para>
    ///   - <see cref="OnSkillBookAcquired"/>: 스킬북 획득 시 서버에서 직접 호출. Available++.
    ///   - <see cref="InvestPointServerRpc"/>: 클라이언트가 투자 방향을 서버에 제출.
    ///   - 공격형 투자 시 <see cref="ApplyAttackTraitClientRpc"/>로 FootprintSystem 가시화.
    /// </para>
    /// </summary>
    public class TraitTreeSystem : NetworkBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────

        /// <summary>씬 내 단일 인스턴스. Awake/OnDestroy에서 관리된다.</summary>
        public static TraitTreeSystem Instance { get; private set; }

        // ── 서버 전용 상태 ─────────────────────────────────────────────────────

        // ADR-015: private readonly — 외부 직접 접근 불가. 서버 메모리에만 존재.
        private readonly Dictionary<ulong, TraitPoints> _traitPoints = new();

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 스킬북 획득 이벤트. 서버에서 직접 호출된다 (LootManager 미구현 — 직접 호출 방식).
        /// 해당 클라이언트의 Available을 1 증가시키고 UI 동기화 ClientRpc를 발행한다.
        /// </summary>
        /// <param name="clientId">스킬북을 획득한 클라이언트 ID.</param>
        public void OnSkillBookAcquired(ulong clientId)
        {
            if (!IsServer) return;

            _traitPoints.TryAdd(clientId, new TraitPoints());
            _traitPoints[clientId].Available++;

            SyncPointsClientRpc(clientId, _traitPoints[clientId].Available);
        }

        /// <summary>
        /// 클라이언트가 특성 포인트 투자 방향을 서버에 제출한다.
        /// 서버가 잔여 포인트를 검증하고, 성공 시 패시브 효과 ClientRpc를 발행한다.
        /// <para>
        ///   보안: clientId는 rpcParams.Receive.SenderClientId에서 추출한다.
        ///   클라이언트가 파라미터로 위조한 clientId를 신뢰하지 않는다.
        /// </para>
        /// </summary>
        /// <param name="direction">투자할 특성 방향.</param>
        /// <param name="rpcParams">NGO가 주입하는 RPC 메타데이터. SenderClientId 추출에 사용.</param>
        [ServerRpc(RequireOwnership = false)]
        public void InvestPointServerRpc(TraitDirection direction, ServerRpcParams rpcParams = default)
        {
            ulong clientId = rpcParams.Receive.SenderClientId;
            if (!_traitPoints.TryGetValue(clientId, out var pts) || pts.Available <= 0)
                return;

            pts.Available--;
            pts.Invested[(int)direction]++;

            switch (direction)
            {
                case TraitDirection.Attack:
                    ApplyAttackTraitClientRpc(clientId);
                    break;

                case TraitDirection.Survival:
                    // TODO: StaminaSystem 연동 — StaminaSystem 구현 후 추가.
                    break;

                case TraitDirection.Assassin:
                    // TODO: MovementSystem 연동 — MovementSystem 구현 후 추가.
                    break;
            }
        }

        /// <summary>
        /// 현재 보유 포인트를 조회한다. 서버에서만 유효한 값을 반환한다.
        /// </summary>
        /// <param name="clientId">조회 대상 클라이언트 ID.</param>
        /// <returns>보유 포인트 수. 서버가 아니거나 등록되지 않은 clientId면 0 반환.</returns>
        public int GetAvailablePoints(ulong clientId)
        {
            if (!IsServer) return 0;
            return _traitPoints.TryGetValue(clientId, out var pts) ? pts.Available : 0;
        }

        /// <summary>
        /// 방향별 투자량을 조회한다. 서버에서만 유효한 값을 반환한다.
        /// </summary>
        /// <param name="clientId">조회 대상 클라이언트 ID.</param>
        /// <param name="direction">조회할 특성 방향.</param>
        /// <returns>해당 방향의 투자량. 서버가 아니거나 등록되지 않은 clientId면 0 반환.</returns>
        public int GetInvestedPoints(ulong clientId, TraitDirection direction)
        {
            if (!IsServer) return 0;
            return _traitPoints.TryGetValue(clientId, out var pts) ? pts.Invested[(int)direction] : 0;
        }

        // ── ClientRpc ─────────────────────────────────────────────────────────

        /// <summary>
        /// 공격형 특성 투자 효과를 소유 클라이언트에 적용한다.
        /// ADR-015: IsOwner 가드로 로컬 플레이어 발자국만 처리. null-safe.
        /// </summary>
        [ClientRpc]
        private void ApplyAttackTraitClientRpc(ulong clientId)
        {
            if (NetworkManager.Singleton.LocalClientId != clientId) return;
            FootprintSystem.Instance?.SetAttackTraitOwner(true);
        }

        /// <summary>
        /// 포인트 잔량을 해당 클라이언트에 동기화한다.
        /// UI Out of Scope — 현재 stub. UI 구현 시 LocalClientId 체크 후 이벤트 발행.
        /// </summary>
        [ClientRpc]
        private void SyncPointsClientRpc(ulong clientId, int available)
        {
            // TODO: UI 동기화 — UISystem 구현 후 LocalClientId == clientId 체크 후 HUD 갱신.
        }
    }
}
