// Extracted from NPCManager.cs — Unity requires separate file per MonoBehaviour for reliable GUID mapping.

using UnityEngine;

namespace BeTheKing.CoreServices
{
    /// <summary>
    /// 일반 NPC 컴포넌트 — 직업 ID 보유.
    /// DisguiseSystem이 이 값을 읽어 머티리얼을 교체한다.
    /// </summary>
    public class CivilianNpc : MonoBehaviour
    {
        /// <summary>배정된 직업 ID. NPCManager.PlaceNPCs()에서 설정.</summary>
        public int NpcJobId { get; set; }
    }
}
