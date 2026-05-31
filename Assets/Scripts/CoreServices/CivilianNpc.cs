// Extracted from NPCManager.cs — Unity requires separate file per MonoBehaviour for reliable GUID mapping.
// POLISH-002: NetworkBehaviour로 업그레이드 — NetworkVariable<int>로 JobId 클라이언트 동기화.

using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.CoreServices
{
    /// <summary>
    /// 일반 NPC 컴포넌트 — 직업 ID 보유.
    /// DisguiseSystem이 NPCManager.OnCivilianNpcSpawned 이벤트를 통해 머티리얼을 적용한다.
    /// NetworkVariable로 JobId를 모든 클라이언트에 동기화한다.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class CivilianNpc : NetworkBehaviour
    {
        private readonly NetworkVariable<int> _jobId = new(
            -1,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        /// <summary>
        /// 배정된 직업 ID. 서버에서 NPCManager.PlaceNPCs()가 설정.
        /// 클라이언트에서도 읽기 가능(Everyone read).
        /// </summary>
        public int NpcJobId
        {
            get => _jobId.Value;
            set
            {
                if (!IsServer) return;
                _jobId.Value = value;
            }
        }
    }
}
