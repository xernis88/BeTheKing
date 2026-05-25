using System;
using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.Foundation
{
    public enum DayPhase : byte { Day, Night }

    /// <summary>
    /// 세션 시간과 낮/밤 사이클을 관리한다.
    /// 시간은 서버에서만 흐르며 NetworkVariable로 클라이언트에 동기화된다.
    /// </summary>
    public class SessionTimeManager : NetworkBehaviour
    {
        public static SessionTimeManager Instance { get; private set; }

        // ── Tuning Knobs ───────────────────────────────────────
        [Header("Tuning")]
        [SerializeField] private float dayDuration   = 180f; // 3분
        [SerializeField] private float nightDuration = 60f;  // 1분
        [SerializeField] private int   totalDays     = 3;

        // ── Network State ──────────────────────────────────────
        private readonly NetworkVariable<float>    _elapsed     = new(0f,           NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<int>      _currentDay  = new(1,            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<DayPhase> _phase       = new(DayPhase.Day, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
        private readonly NetworkVariable<bool>     _coronation  = new(false,        NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // ── Public Accessors ───────────────────────────────────
        public float    Elapsed      => _elapsed.Value;
        public int      CurrentDay   => _currentDay.Value;
        public DayPhase Phase        => _phase.Value;
        public bool     IsCoronation => _coronation.Value;

        public float CycleDuration => dayDuration + nightDuration;
        public float TotalDuration => CycleDuration * totalDays;

        /// <summary>현재 페이즈의 남은 시간(초)</summary>
        public float PhaseRemainingTime
        {
            get
            {
                float cycleTime = _elapsed.Value % CycleDuration;
                return _phase.Value == DayPhase.Day
                    ? dayDuration - cycleTime
                    : CycleDuration - cycleTime;
            }
        }

        // ── Events (서버/클라이언트 모두에서 발행) ───────────────
        /// <summary>새로운 낮이 시작될 때 — 일차(1~3)를 전달</summary>
        public event Action<int> OnDayStarted;

        /// <summary>밤이 시작될 때 — 일차(1~3)를 전달</summary>
        public event Action<int> OnNightStarted;

        /// <summary>3일차 낮 시작 (대관식). CoronationTrigger가 구독한다.</summary>
        public event Action OnCoronationStarted;

        /// <summary>세션 12분 종료. VictoryManager가 구독한다.</summary>
        public event Action OnSessionEnded;

        // ── 서버 전용 내부 상태 ────────────────────────────────
        private bool _sessionEnded;

        void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public override void OnNetworkSpawn()
        {
            _phase.OnValueChanged += (prev, next) =>
            {
                if (next == DayPhase.Day)  OnDayStarted?.Invoke(_currentDay.Value);
                else                       OnNightStarted?.Invoke(_currentDay.Value);
            };

            _coronation.OnValueChanged += (_, active) =>
            {
                if (active) OnCoronationStarted?.Invoke();
            };
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            if (!IsServer || _sessionEnded) return;
            if (GameStateManager.Instance == null) return;
            if (GameStateManager.Instance.Current != GameState.InGame) return;

            _elapsed.Value += Time.deltaTime;

            if (_elapsed.Value >= TotalDuration)
            {
                _sessionEnded = true;
                OnSessionEnded?.Invoke();
                return;
            }

            SyncCycle();
        }

        // ── 마이그레이션 복원 API ──────────────────────────────

        /// <summary>
        /// 호스트 마이그레이션 후 신규 호스트에서 경과 시간을 복원한다.
        /// 서버에서만 호출 가능. 복원 직후 SyncCycle()로 Day/Phase를 즉시 반영한다.
        ///
        /// <example>
        /// // HostMigrationController가 스냅샷 복원 시 호출
        /// SessionTimeManager.Instance.RestoreElapsed(snapshot.Elapsed);
        /// </example>
        /// </summary>
        /// <param name="elapsed">복원할 누적 경과 시간(초). 음수는 0으로 클램프된다.</param>
        public void RestoreElapsed(float elapsed)
        {
            if (!IsServer)
            {
                Debug.LogWarning("[SessionTime] RestoreElapsed는 서버에서만 호출 가능하다.");
                return;
            }

            _elapsed.Value = Mathf.Max(0f, elapsed);
            _sessionEnded  = false;
            SyncCycle();

            Debug.Log($"[SessionTime] 경과 시간 복원 완료 — Elapsed={_elapsed.Value:F1}s");
        }

        private void SyncCycle()
        {
            float cycleTime = _elapsed.Value % CycleDuration;
            int   day       = Mathf.FloorToInt(_elapsed.Value / CycleDuration) + 1;
            var   phase     = cycleTime < dayDuration ? DayPhase.Day : DayPhase.Night;
            bool  corona    = day == totalDays && phase == DayPhase.Day;

            // NetworkVariable 쓰기는 값이 실제로 바뀔 때만 (OnValueChanged 트리거 조건)
            if (_currentDay.Value != day)   _currentDay.Value  = day;
            if (_phase.Value      != phase) _phase.Value       = phase;
            if (_coronation.Value != corona) _coronation.Value = corona;
        }
    }
}
