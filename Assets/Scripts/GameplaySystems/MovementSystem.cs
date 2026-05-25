// Implements: design/gdd/02-gameplay-systems.md — MovementSystem
// Story: production/epics/epic-gameplay-systems/story-002-movement-footprint.md
// ADR: docs/architecture/ADR-008-footprint-networkobject-spawn.md
//
// 설계 결정:
//   클라이언트(IsOwner)가 입력을 감지해 ServerRpc를 호출하고,
//   서버에서 스태미나 소모와 발자국 스폰을 처리한다 (모든 플레이어 지원).
//   IsOwner && IsServer 패턴은 비Host 플레이어의 달리기를 막으므로 사용 금지.

using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 플레이어 달리기/걷기 상태를 관리한다. 서버 권위적.
    /// <para>
    ///   - <see cref="RequestSprint"/>: 스태미나 잔량이 있을 때 달리기 시작 (클라이언트 로컬).
    ///   - <see cref="StopSprint"/>: 달리기 강제 중단.
    ///   - <see cref="IsSprinting"/>: 현재 달리기 상태 여부.
    ///   - Update: IsOwner일 때 ServerRpc로 스태미나 소모·발자국 스폰 요청.
    /// </para>
    /// </summary>
    public class MovementSystem : NetworkBehaviour
    {
        // ── Inspector 참조 ──────────────────────────────────────────────────

        [SerializeField] private StaminaSystem _stamina;
        [SerializeField] private FootprintSystem _footprint;

        // ── Balance ────────────────────────────────────────────────────────

        [Header("Balance — 밸런스 시 확정")]
        [Tooltip("달리기 중 초당 스태미나 소모량. GDD 기본값 20/s.")]
        [SerializeField] private float _sprintCostPerSec = 20f;

        [Tooltip("발자국 생성 간격(초). ADR-008: 0.5초.")]
        [SerializeField] private float _spawnInterval = 0.5f;

        // ── 런타임 상태 ────────────────────────────────────────────────────

        private bool _isSprinting;
        private float _nextSpawnTime;  // 서버 전용: 다음 발자국 스폰 시각

        // ── 프로퍼티 ──────────────────────────────────────────────────────

        /// <summary>현재 달리기 상태 여부. 클라이언트 읽기 가능.</summary>
        public bool IsSprinting => _isSprinting;

        /// <summary>
        /// 달리기를 요청한다. 스태미나 잔량이 있을 때만 활성화된다.
        /// 로컬 플래그만 설정 — 실제 소모는 Update에서 ServerRpc를 통해 서버에서 처리.
        /// </summary>
        public void RequestSprint()
        {
            if (_stamina.CanAct)
                _isSprinting = true;
        }

        /// <summary>달리기를 중단하고 걷기 상태로 전환한다.</summary>
        public void StopSprint()
        {
            _isSprinting = false;
        }

        // ── Unity Lifecycle ────────────────────────────────────────────────

        private void Update()
        {
            // 소유 클라이언트에서만 입력 처리.
            // 비Host 플레이어도 IsOwner=true이므로 모든 플레이어 지원.
            if (!IsOwner) return;
            if (!_isSprinting) return;

            SprintTickServerRpc(Time.deltaTime, transform.position);
        }

        // ── ServerRpc ─────────────────────────────────────────────────────

        /// <summary>
        /// 서버에서 달리기 스태미나 소모 및 발자국 스폰을 처리한다.
        /// 스태미나 소진 시 StopSprintClientRpc로 클라이언트에 통보.
        /// </summary>
        [ServerRpc]
        private void SprintTickServerRpc(float deltaTime, Vector3 pos)
        {
            if (!_stamina.TryConsume(_sprintCostPerSec * deltaTime))
            {
                StopSprintClientRpc();
                return;
            }

            if (Time.time >= _nextSpawnTime)
            {
                _nextSpawnTime = Time.time + _spawnInterval;
                _footprint.SpawnFootprintServerRpc(pos);
            }
        }

        // ── ClientRpc ─────────────────────────────────────────────────────

        /// <summary>서버 판정으로 스태미나 소진 시 소유 클라이언트의 달리기를 중단한다.</summary>
        [ClientRpc]
        private void StopSprintClientRpc()
        {
            _isSprinting = false;
        }
    }
}
