// Implements: design/gdd/02-gameplay-systems.md — CivilianNPCAI
// Story: production/epics/epic-gameplay-systems/story-007-assassin-npc-ai.md
// Referenced by: AssassinNPCAI.cs (IDLE 상태 위장 행동 동기화)

using BeTheKing.CoreServices;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 일반 NPC(Civilian)의 AI를 구현한다. 서버 권위적.
    /// <para>
    ///   - NavMeshAgent를 사용해 미리 설정된 웨이포인트 목록을 순환 배회한다.
    ///   - 웨이포인트가 없으면 스폰 위치 주변 반경(<see cref="_wanderRadius"/>)을
    ///     랜덤하게 배회하는 Fallback 동작을 실행한다.
    ///   - 모든 FSM 로직은 IsServer 가드 안에서만 실행된다.
    /// </para>
    /// <para>
    ///   REQUIRES: Prefab에 NavMeshAgent 및 CivilianNpc 컴포넌트 필요.
    /// </para>
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent))]
    [RequireComponent(typeof(CivilianNpc))]
    public class CivilianNPCAI : NetworkBehaviour
    {
        // ── Inspector ────────────────────────────────────────────────────────────

        [Header("Waypoints — Inspector 또는 NpcPlacementConfig에서 설정")]
        [Tooltip("순환 배회할 웨이포인트 목록. 비어 있으면 랜덤 배회(Fallback) 사용.")]
        [SerializeField] private Transform[] _waypoints;

        [Header("Balance — 밸런스 시 확정")]
        [Tooltip("웨이포인트 도착 판정 거리(m). 기본 0.5m.")]
        [SerializeField] private float _waypointReachThreshold = 0.5f;

        [Tooltip("랜덤 배회 반경(m). 웨이포인트 미설정 시 사용. 기본 10m.")]
        [SerializeField] private float _wanderRadius = 10f;

        [Tooltip("배회 대기 시간(초). 웨이포인트 도착 후 다음 이동 전 대기. 기본 2s.")]
        [SerializeField] private float _waitDuration = 2f;

        [Tooltip("이동 속도(m/s). 기본 1.5m/s (일반인 보행 속도).")]
        [SerializeField] private float _moveSpeed = 1.5f;

        // ── 서버 전용 상태 ───────────────────────────────────────────────────────

        private NavMeshAgent _agent;
        private int _currentWaypointIndex;
        private float _waitTimer;
        private bool _isWaiting;
        private Vector3 _spawnPos;

        // ── Lifecycle ────────────────────────────────────────────────────────────

        private void Awake()
        {
            _agent = GetComponent<NavMeshAgent>();
        }

        /// <summary>스폰 위치 저장 및 NavMeshAgent 초기화. 서버 전용.</summary>
        public override void OnNetworkSpawn()
        {
            if (!IsServer) return;

            _spawnPos = transform.position;
            _agent.speed = _moveSpeed;

            // 첫 목적지 설정
            SetNextDestination();
        }

        // ── Unity Update ──────────────────────────────────────────────────────────

        private void Update() => Tick(Time.deltaTime);

        /// <summary>
        /// 배회 FSM 틱. 서버에서만 실행. 테스트에서 직접 주입 가능.
        /// </summary>
        /// <param name="deltaTime">경과 시간(초).</param>
        internal void Tick(float deltaTime)
        {
            if (!IsServer) return;

            if (_isWaiting)
            {
                _waitTimer -= deltaTime;
                if (_waitTimer <= 0f)
                {
                    _isWaiting = false;
                    SetNextDestination();
                }
                return;
            }

            // 목적지 도달 판정
            if (!_agent.pathPending && _agent.remainingDistance <= _waypointReachThreshold)
            {
                _isWaiting = true;
                _waitTimer = _waitDuration;
            }
        }

        // ── 목적지 설정 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 웨이포인트 순환 또는 랜덤 배회 목적지를 설정한다.
        /// </summary>
        private void SetNextDestination()
        {
            if (_waypoints != null && _waypoints.Length > 0)
            {
                // 웨이포인트 순환
                Vector3 dest = _waypoints[_currentWaypointIndex].position;
                _agent.SetDestination(dest);
                _currentWaypointIndex = (_currentWaypointIndex + 1) % _waypoints.Length;
            }
            else
            {
                // Fallback: 스폰 위치 주변 랜덤 배회
                Vector3 randomPoint = _spawnPos + Random.insideUnitSphere * _wanderRadius;
                randomPoint.y = _spawnPos.y;

                if (UnityEngine.AI.NavMesh.SamplePosition(randomPoint, out var hit, _wanderRadius, UnityEngine.AI.NavMesh.AllAreas))
                    _agent.SetDestination(hit.position);
                else
                    _agent.SetDestination(_spawnPos); // NavMesh 샘플 실패 시 스폰 위치로
            }
        }

        // ── 공개 API (AssassinNPCAI IDLE 위장 동기화용) ──────────────────────────

        /// <summary>
        /// AssassinNPCAI가 IDLE 상태에서 일반 NPC처럼 행동할 때 호출.
        /// 웨이포인트를 동일하게 공유하여 외형·행동 구별 불가를 보장한다.
        /// </summary>
        /// <param name="waypoints">공유할 웨이포인트 배열.</param>
        public void SetWaypoints(Transform[] waypoints)
        {
            if (!IsServer) return;
            _waypoints = waypoints;
            _currentWaypointIndex = 0;
            _isWaiting = false;
            SetNextDestination();
        }

#if UNITY_EDITOR
        // ── 에디터 디버그 시각화 ──────────────────────────────────────────────────

        private void OnDrawGizmosSelected()
        {
            // 배회 반경 — 파란색
            Gizmos.color = Color.blue;
            Vector3 center = Application.isPlaying ? _spawnPos : transform.position;
            Gizmos.DrawWireSphere(center, _wanderRadius);

            // 웨이포인트 연결선 — 초록색
            if (_waypoints == null || _waypoints.Length == 0) return;
            Gizmos.color = Color.green;
            for (int i = 0; i < _waypoints.Length; i++)
            {
                if (_waypoints[i] == null) continue;
                Gizmos.DrawSphere(_waypoints[i].position, 0.3f);
                if (i + 1 < _waypoints.Length && _waypoints[i + 1] != null)
                    Gizmos.DrawLine(_waypoints[i].position, _waypoints[i + 1].position);
            }
        }
#endif
    }
}
