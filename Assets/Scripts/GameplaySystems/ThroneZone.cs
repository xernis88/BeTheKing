// Implements: design/gdd/02-gameplay-systems.md — ThroneZone (stub)
// Story: production/epics/epic-victory-endgame/VE-002 (미구현 — stub 처리)
//
// NOTE: 이 파일은 PrinceNPCAI가 의존하는 ThroneZone의 stub이다.
//       실제 구현은 epic-victory-endgame VE-002에서 교체된다.
//       PrinceNPCAI.OnDeath() → _throneZone.OpenFully() 계약만 정의.

using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 왕좌 영역을 나타낸다. 이 클래스는 stub이다.
    /// <para>
    ///   VE-002에서 실제 구현으로 교체된다.
    ///   현재는 <see cref="OpenFully"/> 호출 시 로그를 남기는 것만 수행한다.
    /// </para>
    /// </summary>
    public class ThroneZone : NetworkBehaviour
    {
        /// <summary>
        /// 왕좌 영역을 완전 개방한다. 왕자 NPC 처치 시 PrinceNPCAI에서 호출된다.
        /// VE-002 구현 전까지 서버 로그만 출력한다.
        /// </summary>
        public virtual void OpenFully()
        {
            if (!IsServer) return;
            Debug.Log("[ThroneZone] OpenFully 호출됨 — VE-002 구현 전 stub");
        }
    }
}
