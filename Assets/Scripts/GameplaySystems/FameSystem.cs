// Implements: design/gdd/03-progression-economy.md — FameSystem
// Story: production/epics/epic-progression-economy/story-005-fame-system.md
// ADR: docs/architecture/ADR-012-fame-system-server-dictionary.md
//
// 설계 결정:
//   명성치는 서버 전용 Dictionary<ulong, int>로 관리. NetworkVariable 미사용.
//   이유: CurrencySystem 동일 패턴 (ADR-006 → ADR-012 확장).
//   명성치는 최솟값 0 보장 (Math.Max). 상한 없음.
//   CheckFame은 소비 없음 — 자격 검사 전용.

using Unity.Netcode;
using System.Collections.Generic;
using System;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 플레이어 명성치(Fame)를 서버 권위적으로 관리한다.
    /// <para>
    ///   - <see cref="GainFame"/>: 명성치를 증가시킨다. 퀘스트 완료, 공공시설 건설 등에서 호출.
    ///   - <see cref="LoseFame"/>: 명성치를 감소시킨다. 최솟값 0 보장.
    ///   - <see cref="CheckFame"/>: 명성치가 요구치 이상인지 확인한다. 소비 없음.
    ///   - <see cref="GetFame"/>: 현재 명성치를 반환한다.
    /// </para>
    /// </summary>
    public class FameSystem : NetworkBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────

        /// <summary>씬 내 단일 인스턴스. OnNetworkSpawn에서 서버 전용으로 등록된다.</summary>
        public static FameSystem Instance { get; private set; }

        // ── 서버 전용 상태 ─────────────────────────────────────────────────────

        // ADR-012: private readonly — 외부 직접 접근 불가. 서버 메모리에만 존재.
        private readonly Dictionary<ulong, int> _playerFame = new();

        // ── Lifecycle ──────────────────────────────────────────────────────────

        public override void OnNetworkSpawn()
        {
            if (IsServer) Instance = this;
        }

        public override void OnNetworkDespawn()
        {
            if (IsServer && Instance == this) Instance = null;
        }

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 지정 클라이언트의 명성치를 증가시킨다. 서버에서만 유효.
        /// </summary>
        /// <param name="clientId">대상 클라이언트 ID.</param>
        /// <param name="amount">증가량. 양수여야 한다.</param>
        public void GainFame(ulong clientId, int amount)
        {
            // ADR-012: IsServer 가드 필수.
            if (!IsServer) return;

            _playerFame[clientId] = GetFame(clientId) + amount;
        }

        /// <summary>
        /// 지정 클라이언트의 명성치를 감소시킨다. 서버에서만 유효.
        /// 결과가 0 미만이 되면 0으로 고정된다.
        /// </summary>
        /// <param name="clientId">대상 클라이언트 ID.</param>
        /// <param name="amount">감소량.</param>
        public void LoseFame(ulong clientId, int amount)
        {
            // ADR-012: IsServer 가드 필수.
            if (!IsServer) return;

            _playerFame[clientId] = Math.Max(0, GetFame(clientId) - amount);
        }

        /// <summary>
        /// 지정 클라이언트의 명성치가 요구치 이상인지 확인한다. 서버에서만 유효.
        /// 명성치를 소비하지 않는다 — 자격 검사 전용.
        /// </summary>
        /// <param name="clientId">확인할 클라이언트 ID.</param>
        /// <param name="required">필요 명성치.</param>
        /// <returns>명성치 충족 시 true. 클라이언트 호출 시 false.</returns>
        public bool CheckFame(ulong clientId, int required)
        {
            // ADR-012: IsServer 가드 필수.
            if (!IsServer) return false;

            return GetFame(clientId) >= required;
        }

        /// <summary>
        /// 지정 클라이언트의 현재 명성치를 반환한다. 서버에서만 유효.
        /// 미등록 클라이언트는 0을 반환한다.
        /// </summary>
        /// <param name="clientId">조회할 클라이언트 ID.</param>
        /// <returns>현재 명성치. 클라이언트 호출 시 0.</returns>
        public int GetFame(ulong clientId)
        {
            // ADR-012: IsServer 가드 필수.
            if (!IsServer) return 0;

            return _playerFame.GetValueOrDefault(clientId, 0);
        }
    }
}
