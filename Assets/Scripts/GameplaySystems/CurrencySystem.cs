// Implements: design/gdd/03-progression-economy.md — CurrencySystem
// Story: production/epics/epic-progression-economy/story-001-currency-system.md
// ADR: docs/architecture/ADR-006-currency-system-server-dictionary.md
//
// 설계 결정:
//   금화량은 서버 전용 Dictionary<ulong, int>로 관리. NetworkVariable 미사용.
//   이유: 이벤트 기반 변경 + 타인의 잔액 노출 방지 (ADR-006).
//   잔액 변경 시 ClientRpc로 해당 클라이언트에만 푸시.
//   클라이언트 UI는 OnBalanceUpdated 이벤트를 구독하여 갱신한다.

using Unity.Netcode;
using System.Collections.Generic;
using System;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 플레이어 금화 잔액을 서버 권위적으로 관리한다.
    /// <para>
    ///   - <see cref="Award"/>: 서버에서 금화를 지급한다. JobInteractionSystem 성공 콜백에서 호출.
    ///   - <see cref="TrySpend"/>: 서버에서 금화를 소비한다. 잔액 부족 시 false 반환.
    ///   - 잔액 변경 시 해당 클라이언트에게만 <see cref="OnBalanceUpdated"/> 이벤트를 발행한다.
    /// </para>
    /// </summary>
    public class CurrencySystem : NetworkBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────

        /// <summary>씬 내 단일 인스턴스. OnNetworkSpawn/OnNetworkDespawn에서 관리된다.</summary>
        public static CurrencySystem Instance { get; private set; }

        // ── 서버 전용 상태 ─────────────────────────────────────────────────────

        // ADR-006: private readonly — 외부 직접 접근 불가. 서버 메모리에만 존재.
        private readonly Dictionary<ulong, int> _balances = new();

        // ── 이벤트 (클라이언트 UI 구독용) ─────────────────────────────────────

        /// <summary>
        /// 이 클라이언트의 금화 잔액이 변경될 때 발생. 클라이언트 UI HUD가 구독한다.
        /// 매개변수: 갱신된 잔액.
        /// </summary>
        public event Action<int> OnBalanceUpdated;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            Instance = this;
        }

        public override void OnNetworkDespawn()
        {
            if (Instance == this)
                Instance = null;
        }

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 지정 클라이언트에게 금화를 지급한다. 서버에서만 유효.
        /// JobInteractionSystem의 미니게임 성공 콜백에서 호출된다.
        /// </summary>
        /// <param name="clientId">지급 대상 클라이언트 ID.</param>
        /// <param name="amount">지급할 금화량. 양수여야 한다.</param>
        public void Award(ulong clientId, int amount)
        {
            // ADR-006: IsServer 가드 필수.
            if (!IsServer) return;

            _balances.TryAdd(clientId, 0);
            _balances[clientId] += amount;
            SyncBalanceClientRpc(clientId, _balances[clientId]);
        }

        /// <summary>
        /// 지정 클라이언트의 금화를 소비한다. 서버에서만 유효.
        /// LevelSystem, 스킬북 구매 등에서 호출된다.
        /// </summary>
        /// <param name="clientId">소비 대상 클라이언트 ID.</param>
        /// <param name="amount">소비할 금화량.</param>
        /// <returns>소비 성공 시 true. 잔액 부족 또는 클라이언트 호출 시 false.</returns>
        public bool TrySpend(ulong clientId, int amount)
        {
            // ADR-006: IsServer 가드 필수.
            if (!IsServer) return false;
            if (!_balances.TryGetValue(clientId, out var bal) || bal < amount) return false;

            _balances[clientId] -= amount;
            SyncBalanceClientRpc(clientId, _balances[clientId]);
            return true;
        }

        // ── ClientRpc ─────────────────────────────────────────────────────────

        /// <summary>
        /// 잔액 변경을 해당 클라이언트에게 푸시한다.
        /// ADR-006: 모든 클라이언트가 수신하지만, LocalClientId가 일치하는 경우에만 이벤트를 발행한다.
        /// 이를 통해 타인의 잔액이 UI에 노출되지 않는다.
        /// </summary>
        [ClientRpc]
        private void SyncBalanceClientRpc(ulong clientId, int newBalance)
        {
            // ADR-006: LocalClientId 체크 후 UI 이벤트 발행.
            if (NetworkManager.Singleton.LocalClientId == clientId)
                OnBalanceUpdated?.Invoke(newBalance);
        }
    }
}
