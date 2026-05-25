// Implements: design/gdd/04-victory-endgame.md — ThroneZoneManager
// Story: production/epics/epic-victory-endgame/story-002-throne-gauge.md
// Requirement: TR-VICT-003, TR-VICT-004
//
// 설계 결정:
//   - OnTriggerEnter/Exit는 서버에서만 실행 (IsServer 가드).
//   - NetworkObject를 통해 OwnerClientId를 추출하여 RoyalGaugeSystem에 전달.
//   - Activate()는 CoronationTrigger에서 호출되며, 콜라이더를 활성화하여
//     트리거 감지를 시작한다 (VE-001 계약 유지).

using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 왕좌 영역 진입/이탈 트리거 감지 시스템. 서버 권위적.
    /// <para>
    ///   대관식 시작 시 <see cref="CoronationTrigger"/>에서 <see cref="Activate"/>를 호출하여
    ///   트리거 콜라이더를 활성화한다.
    ///   이후 플레이어 진입/이탈을 감지하여 <see cref="RoyalGaugeSystem"/>에 전달한다.
    /// </para>
    /// </summary>
    public class ThroneZoneManager : NetworkBehaviour
    {
        // ── Inspector ──────────────────────────────────────────────────────────

        [Tooltip("왕좌 영역을 나타내는 트리거 콜라이더. Activate() 호출 시 활성화된다.")]
        [SerializeField] private Collider _zoneTrigger;

        [Tooltip("게이지 관리 시스템. 진입/이탈 이벤트를 수신한다.")]
        [SerializeField] private RoyalGaugeSystem _gaugeSystem;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void Awake()
        {
            // 트리거는 대관식 시작(Activate) 전까지 비활성화 상태 유지
            if (_zoneTrigger != null)
                _zoneTrigger.enabled = false;

            if (_gaugeSystem == null)
                Debug.LogError("[ThroneZoneManager] _gaugeSystem이 할당되지 않았습니다.", this);
        }

        // ── 공개 API ───────────────────────────────────────────────────────────

        /// <summary>
        /// 왕좌 영역 감지를 활성화한다.
        /// <para>
        ///   대관식 시작 시 <see cref="CoronationTrigger.HandleCoronationStarted"/>에서 호출된다.
        ///   트리거 콜라이더를 활성화하여 이후 플레이어 진입/이탈 감지를 시작한다.
        /// </para>
        /// </summary>
        public virtual void Activate()
        {
            if (!IsServer) return;

            if (_zoneTrigger != null)
                _zoneTrigger.enabled = true;

            Debug.Log("[ThroneZoneManager] 왕좌 영역 트리거 활성화 완료.");
        }

        // ── 트리거 감지 (서버 전용) ────────────────────────────────────────────

        private void OnTriggerEnter(Collider other)
        {
            if (!IsServer) return;

            if (other.TryGetComponent<NetworkObject>(out var netObj))
                _gaugeSystem?.OnPlayerEnter(netObj.OwnerClientId);
        }

        private void OnTriggerExit(Collider other)
        {
            if (!IsServer) return;

            if (other.TryGetComponent<NetworkObject>(out var netObj))
                _gaugeSystem?.OnPlayerExit(netObj.OwnerClientId);
        }
    }
}
