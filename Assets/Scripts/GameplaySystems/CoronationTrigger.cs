// Implements: design/gdd/04-victory-endgame.md — CoronationTrigger
// Story: production/epics/epic-victory-endgame/story-001-coronation-trigger.md
// Requirement: TR-VICT-001, TR-VICT-002
//
// 설계 결정:
//   OnCoronationStarted 이벤트의 단일 조율자. 서버에서만 게임 상태 전환을 실행.
//   1) MapManager.OpenGates() — 성문 개방 (TR-VICT-001)
//   2) PrinceNPCAI.Activate() — 왕자 NPC 활성화 (TR-VICT-002)
//   3) ThroneZoneManager.SetActive(true) — 왕좌 영역 감지 시작
//   4) NotifyAllClientsClientRpc() — HUD 알림용 전체 브로드캐스트
//   MapManager와 PrinceNPCAI는 이 클래스에서만 OnCoronationStarted를 수신한다.

using System;
using BeTheKing.CoreServices;
using BeTheKing.Foundation;
using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 대관식 시작 이벤트의 중앙 조율자. 서버 권위적.
    /// <para>
    ///   <see cref="SessionTimeManager.OnCoronationStarted"/> 수신 시:
    ///   성문 개방 → 왕자 NPC 활성화 → 왕좌 영역 감지 시작 → 전체 클라이언트 알림.
    /// </para>
    /// </summary>
    public class CoronationTrigger : NetworkBehaviour
    {
        [Header("References")]
        [Tooltip("성문 관리자. OnCoronationStarted 시 OpenGates() 호출.")]
        [SerializeField] private MapManager _mapManager;

        [Tooltip("왕자 NPC. OnCoronationStarted 시 Activate() 호출.")]
        [SerializeField] private PrinceNPCAI _prince;

        [Tooltip("왕좌 영역 감지 관리자. VE-002 구현 전 stub.")]
        [SerializeField] private ThroneZoneManager _throneZone;

        /// <summary>
        /// 모든 클라이언트에서 대관식 시작 알림을 받을 때 발행된다.
        /// HUD 및 UI 시스템에서 구독하여 알림을 표시한다.
        /// </summary>
        public static event Action OnCoronationAnnounced;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void Awake()
        {
            if (_mapManager == null)
                Debug.LogError("[CoronationTrigger] _mapManager is not assigned.", this);
            if (_prince == null)
                Debug.LogError("[CoronationTrigger] _prince is not assigned.", this);
            if (_throneZone == null)
                Debug.LogError("[CoronationTrigger] _throneZone is not assigned.", this);
        }

        public override void OnNetworkSpawn()
        {
            // 서버/클라이언트 모두 구독 — SessionTimeManager는 양쪽에서 이벤트를 발행.
            // HandleCoronationStarted 내부 IsServer 가드가 서버 전용 로직을 보호.
            if (SessionTimeManager.Instance != null)
                SessionTimeManager.Instance.OnCoronationStarted += HandleCoronationStarted;
            else
                Debug.LogError("[CoronationTrigger] SessionTimeManager.Instance is null — OnCoronationStarted 구독 불가.", this);
        }

        public override void OnNetworkDespawn()
        {
            if (SessionTimeManager.Instance != null)
                SessionTimeManager.Instance.OnCoronationStarted -= HandleCoronationStarted;
        }

        // ── 이벤트 핸들러 ──────────────────────────────────────────────────────

        private void HandleCoronationStarted()
        {
            if (!IsServer) return;

            _mapManager?.OpenGates();                          // TR-VICT-001: 성문 개방
            NPCManager.Instance?.ActivatePrince();             // TR-VICT-002: 왕자 NetworkObject Spawn
            _prince?.Activate();                               // TR-VICT-002: 왕자 NPC AI 활성화 (무적 해제)
            _throneZone?.Activate();                           // 왕좌 영역 감지 시작
            NotifyAllClientsClientRpc();                       // 전체 클라이언트 알림
        }

        // ── RPC ────────────────────────────────────────────────────────────────

        /// <summary>대관식 시작을 전체 클라이언트에 브로드캐스트한다. HUD 이벤트 트리거.</summary>
        [ClientRpc]
        private void NotifyAllClientsClientRpc()
        {
            OnCoronationAnnounced?.Invoke();
        }
    }
}
