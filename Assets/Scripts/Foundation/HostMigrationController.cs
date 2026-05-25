using System.Collections;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.Foundation
{
    /// <summary>
    /// 호스트 이탈 감지 → 새 호스트 선출 → 세션 상태 복원을 담당한다.
    ///
    /// MonoBehaviour Singleton 채택 이유:
    ///   NetworkBehaviour는 NetworkManager.Shutdown() 시 despawn/destroy되어
    ///   마이그레이션 실행 중 컨트롤러가 소멸한다. MonoBehaviour는 Shutdown 후에도 생존.
    ///
    /// 동작 흐름:
    ///   [호스트] 2초 간격으로 {스냅샷, 후보Id}를 CustomMessagingManager로 브로드캐스트
    ///   [클라이언트] 수신 패키지를 _cachedPackage에 캐시
    ///   [호스트 이탈] GameNetworkManager.OnHostLost 수신
    ///     → 캐시된 후보Id와 자신 비교
    ///     → 후보: Shutdown → StartHost → RestoreElapsed
    ///     → 비후보: TODO(STEAM_BUILD) 새 호스트에 재연결
    /// </summary>
    public class HostMigrationController : MonoBehaviour
    {
        public static HostMigrationController Instance { get; private set; }

        // ── Tuning ────────────────────────────────────────────
        [Header("Tuning")]
        [Tooltip("호스트가 클라이언트에 상태를 브로드캐스트하는 간격(초)")]
        [SerializeField] private float broadcastInterval = 2f;

        [Tooltip("마이그레이션 제한 시간(초). GDD TR-FND-004: 60초 이내 완료")]
        [SerializeField] private float migrationTimeout = 60f;

        // ── Events ────────────────────────────────────────────
        /// <summary>마이그레이션 완료 시 발행. true=성공, false=실패</summary>
        public event System.Action<bool> OnMigrationCompleted;

        // ── 내부 상태 ─────────────────────────────────────────
        private const string BroadcastMsg = "BTK_MigPkg";

        private bool _migrating;
        private bool _hasPackage;
        private ulong _cachedCandidateId;
        private MigrationSnapshot _cachedSnapshot;

        private Coroutine _broadcastCoroutine;

        // ── Lifecycle ─────────────────────────────────────────

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void OnEnable()
        {
            var gnm = GameNetworkManager.Instance;
            if (gnm == null) return;
            gnm.OnHostStarted         += HandleHostStarted;
            gnm.OnLocalClientConnected += HandleLocalClientConnected;
            gnm.OnHostLost            += HandleHostLost;
        }

        void OnDisable()
        {
            var gnm = GameNetworkManager.Instance;
            if (gnm == null) return;
            gnm.OnHostStarted         -= HandleHostStarted;
            gnm.OnLocalClientConnected -= HandleLocalClientConnected;
            gnm.OnHostLost            -= HandleHostLost;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ── GameNetworkManager 이벤트 핸들러 ──────────────────

        private void HandleHostStarted()
        {
            if (_broadcastCoroutine != null) StopCoroutine(_broadcastCoroutine);
            _broadcastCoroutine = StartCoroutine(BroadcastLoop());
        }

        private void HandleLocalClientConnected()
        {
            var messaging = NetworkManager.Singleton?.CustomMessagingManager;
            if (messaging == null) return;
            messaging.RegisterNamedMessageHandler(BroadcastMsg, ReceivePackage);
        }

        private void HandleHostLost()
        {
            if (_migrating) return;

            if (!_hasPackage)
            {
                Debug.LogError("[Migration] 캐시된 패키지 없음 — 마이그레이션 불가");
                FinishMigration(false);
                return;
            }

            _migrating = true;
            bool isCandidate = NetworkManager.Singleton.LocalClientId == _cachedCandidateId;
            StartCoroutine(RunMigration(isCandidate, _cachedSnapshot));
        }

        // ── 호스트: 주기 브로드캐스트 ────────────────────────

        private IEnumerator BroadcastLoop()
        {
            var wait = new WaitForSeconds(broadcastInterval);
            while (true)
            {
                BroadcastPackage();
                yield return wait;
            }
        }

        private void BroadcastPackage()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            ulong candidate = ElectCandidate();
            var snapshot    = MigrationSnapshot.Capture();

            // 필드를 직접 직렬화 (INetworkSerializable은 BufferSerializer 필요,
            // 여기서는 FastBufferWriter 직접 사용으로 단순화)
            using var writer = new FastBufferWriter(64, Allocator.Temp);
            writer.WriteValueSafe(candidate);
            writer.WriteValueSafe(snapshot.Elapsed);
            writer.WriteValueSafe(snapshot.CurrentDay);
            writer.WriteValueSafe((byte)snapshot.Phase);
            writer.WriteValueSafe(snapshot.IsCoronation);
            writer.WriteValueSafe((byte)snapshot.GameState);
            writer.WriteValueSafe(snapshot.CapturedAtUtc);

            nm.CustomMessagingManager.SendNamedMessageToAll(BroadcastMsg, writer);
        }

        // ── 클라이언트: 패키지 수신 캐시 ────────────────────

        private void ReceivePackage(ulong _, FastBufferReader reader)
        {
            reader.ReadValueSafe(out ulong candidateId);
            reader.ReadValueSafe(out float elapsed);
            reader.ReadValueSafe(out int currentDay);
            reader.ReadValueSafe(out byte phaseByte);
            reader.ReadValueSafe(out bool isCoronation);
            reader.ReadValueSafe(out byte gameStateByte);
            reader.ReadValueSafe(out long capturedAt);

            _cachedCandidateId = candidateId;
            _cachedSnapshot = new MigrationSnapshot
            {
                Elapsed       = elapsed,
                CurrentDay    = currentDay,
                Phase         = (DayPhase)phaseByte,
                IsCoronation  = isCoronation,
                GameState     = (GameState)gameStateByte,
                CapturedAtUtc = capturedAt,
            };
            _hasPackage = true;
        }

        // ── 마이그레이션 흐름 ────────────────────────────────

        private IEnumerator RunMigration(bool isCandidate, MigrationSnapshot snapshot)
        {
            float elapsed = 0f;

            if (isCandidate)
            {
                yield return StartCoroutine(BecomeNewHost(snapshot));
            }
            else
            {
                // TODO(STEAM_BUILD): Steam Lobby 오너 변경 이벤트로 새 호스트 SteamId 수신 후 재연결.
                // Steam SDR 미연동 시 재연결 불가 — known limitation (ADR-004 참조).
                while (elapsed < migrationTimeout)
                {
                    elapsed += Time.deltaTime;
                    yield return null;
                }
                FinishMigration(false);
            }
        }

        private IEnumerator BecomeNewHost(MigrationSnapshot snapshot)
        {
            Debug.Log("[Migration] 새 호스트 역할 인수 시작");

            // 1. 기존 세션 종료
            NetworkManager.Singleton.Shutdown();

            // 2. 1프레임 대기 — unity-specialist 검증: 같은 프레임 재시작 시 transport 핸들 누수
            yield return null;

            // 3. 새 호스트로 시작
            NetworkManager.Singleton.StartHost();

            // 4. IsServer 확인 (최대 5초 대기)
            float waited = 0f;
            while (!NetworkManager.Singleton.IsServer && waited < 5f)
            {
                waited += Time.deltaTime;
                yield return null;
            }

            if (!NetworkManager.Singleton.IsServer)
            {
                Debug.LogError("[Migration] StartHost() 실패 — IsServer 미달성");
                FinishMigration(false);
                yield break;
            }

            // 5. 서버 컨텍스트 확보 후 세션 복원 (IsServer == true 보장 이후)
            SessionTimeManager.Instance?.RestoreElapsed(snapshot.Elapsed);

            Debug.Log($"[Migration] 완료 — Elapsed={snapshot.Elapsed:F1}s");
            FinishMigration(true);
        }

        private void FinishMigration(bool success)
        {
            _migrating = false;
            OnMigrationCompleted?.Invoke(success);
        }

        // ── 후보 선출 (서버 전용) ────────────────────────────

        /// <summary>
        /// 연결된 클라이언트 중 가장 낮은 ClientId를 새 호스트 후보로 선출한다.
        /// 서버 자신은 제외. 결정론적 알고리즘 — 추가 합의 프로토콜 불필요.
        /// 후보 없으면 ulong.MaxValue 반환.
        /// </summary>
        internal ulong ElectCandidate()
        {
            var clients = GameNetworkManager.Instance?.ConnectedClients;
            if (clients == null || clients.Count == 0) return ulong.MaxValue;

            ulong hostId = NetworkManager.Singleton.LocalClientId;
            ulong best   = ulong.MaxValue;

            foreach (ulong id in clients)
            {
                if (id == hostId) continue;
                if (id < best) best = id;
            }
            return best;
        }
    }
}
