// Implements: design/gdd/04-victory-endgame.md — ThroneZoneManager (stub)
// Story: production/epics/epic-victory-endgame/story-001-coronation-trigger.md
//
// NOTE: VE-002에서 실제 구현으로 교체된다.
//       현재는 CoronationTrigger.HandleCoronationStarted() → Activate() 계약만 정의.

using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 왕좌 영역 감지 시스템. 이 클래스는 stub이다.
    /// VE-002에서 RoyalGaugeSystem과 함께 실제 구현으로 교체된다.
    /// </summary>
    public class ThroneZoneManager : NetworkBehaviour
    {
        /// <summary>
        /// 왕좌 영역 감지를 활성화한다.
        /// 대관식 시작 시 CoronationTrigger에서 Activate() 호출.
        /// VE-002 구현 전까지 서버 로그만 출력한다.
        /// </summary>
        public virtual void Activate()
        {
            if (!IsServer) return;
            Debug.Log("[ThroneZoneManager] Activate() — VE-002 구현 전 stub");
        }
    }
}
