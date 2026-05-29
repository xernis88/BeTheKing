// Implements: design/gdd/04-victory-endgame.md — RoyalGaugeVisibility
// Story: production/epics/epic-victory-endgame/story-004-royal-gauge-visibility.md
// Requirement: TR-VICT-008
//
// 설계 결정:
//   - MonoBehaviour (NetworkBehaviour 아님): 렌더링 전용, 서버 로직 없음.
//   - static event 구독: OnEnable/OnDisable에서 +=/-=.
//     Start/OnDestroy 대비 씬 전환·오브젝트 비활성화·Domain Reload 시 누수 방지.
//   - Camera 캐싱: Awake에서 Camera.main 1회 캐싱. LateUpdate에서 null 시 재취득.
//   - Billboard: LateUpdate에서 transform.forward = _camera.transform.forward (screen-aligned).
//   - 플레이어 식별: Start에서 NetworkObject 참조 캐싱. HandleGaugeSync에서 live-read.
//     OwnerClientId 1회 스냅샷 금지 — Host Migration 후 owner 재배정 시 오작동 방지.
//   - Slider 정규화: currentGauge / gaugeSystem.MaxGauge (Inspector 튜닝값 live-read).
//   - 가시성: current > 0f 이면 _gaugeRoot 활성, 0이면 비활성.
//   - 색상 임계값: < 60 초록, < 96 노랑, >= 96 빨강.

using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 왕좌 게이지를 플레이어 머리 위 World Space UI로 표시한다.
    /// <para>
    ///   <see cref="RoyalGaugeSystem.OnGaugeSyncedToClient"/> 이벤트를 구독하여
    ///   해당 플레이어의 게이지 슬라이더와 색상을 갱신한다.
    /// </para>
    /// <para>
    ///   이 컴포넌트는 플레이어 프리팹 하위의 World Space Canvas에 부착한다.
    ///   NetworkBehaviour가 아니므로 서버 로직을 포함하지 않는다.
    /// </para>
    /// </summary>
    public class RoyalGaugeUI : MonoBehaviour
    {
        // ── Inspector ──────────────────────────────────────────────────────────

        [Tooltip("게이지 0 시 비활성화할 루트 오브젝트 (World Space Canvas 루트 등).")]
        [SerializeField] private GameObject _gaugeRoot;

        [Tooltip("게이지 값을 표시하는 World Space Canvas Slider.")]
        [SerializeField] private Slider _gaugeSlider;

        [Tooltip("색상 변경 대상 Slider Fill Image.")]
        [SerializeField] private Image _fillImage;

        // ── 상수 ───────────────────────────────────────────────────────────────

        /// <summary>색상 임계값: 초록 상한. gauge < 60 → 초록.</summary>
        private const float ThresholdYellow = 60f;

        /// <summary>색상 임계값: 노랑 상한. gauge < 96 → 노랑, >= 96 → 빨강.</summary>
        private const float ThresholdRed = 96f;

        // ── 색상 정의 ──────────────────────────────────────────────────────────

        /// <summary>#4CAF50 — 게이지 < 60 (< 50%)</summary>
        private static readonly Color ColorGreen  = new Color(0.298f, 0.686f, 0.314f);

        /// <summary>#FFC107 — 게이지 60 ~ 95 (50% ~ 79%)</summary>
        private static readonly Color ColorYellow = new Color(1.000f, 0.757f, 0.027f);

        /// <summary>#F44336 — 게이지 >= 96 (>= 80%)</summary>
        private static readonly Color ColorRed    = new Color(0.957f, 0.263f, 0.212f);

        // ── 캐시 ───────────────────────────────────────────────────────────────

        private Camera            _camera;
        private NetworkObject     _netObj;
        private RoyalGaugeSystem  _gaugeSystem;

        // ── 생명주기 ───────────────────────────────────────────────────────────

        private void Awake()
        {
            // Camera.main은 Update/LateUpdate에서 직접 참조하면 매 프레임 FindGameObject 비용 발생.
            _camera = Camera.main;
        }

        private void Start()
        {
            // OwnerClientId는 NGO Spawn 완료 후 유효하므로 Start에서 NetworkObject 참조만 캐싱.
            // OwnerClientId 자체는 HandleGaugeSync에서 live-read: Host Migration 후 재배정 대응.
            _netObj = GetComponentInParent<NetworkObject>();

            // RoyalGaugeSystem.MaxGauge를 live-read하여 Inspector 튜닝값 동기화.
            _gaugeSystem = FindObjectOfType<RoyalGaugeSystem>();

            if (_gaugeRoot != null)
                _gaugeRoot.SetActive(false);
        }

        private void OnEnable()
        {
            RoyalGaugeSystem.OnGaugeSyncedToClient += HandleGaugeSync;
        }

        private void OnDisable()
        {
            // OnEnable/OnDisable 쌍: 비활성화·씬 전환·Domain Reload 시 static event 누수 방지.
            RoyalGaugeSystem.OnGaugeSyncedToClient -= HandleGaugeSync;
        }

        /// <summary>
        /// Billboard 회전 처리. LateUpdate에서 수행하여 캐릭터 애니메이션 이후 적용.
        /// transform.forward = camera.transform.forward (screen-aligned billboard).
        /// Host Migration 후 카메라 재생성 시 null-refetch로 자동 복구.
        /// </summary>
        private void LateUpdate()
        {
            if (_camera == null)
                _camera = Camera.main;

            if (_camera != null)
                transform.forward = _camera.transform.forward;
        }

        // ── 이벤트 핸들러 ──────────────────────────────────────────────────────

        /// <summary>
        /// <see cref="RoyalGaugeSystem.OnGaugeSyncedToClient"/> 수신 핸들러.
        /// 이 컴포넌트를 소유한 플레이어(OwnerClientId live-read)의 이벤트만 처리한다.
        /// </summary>
        /// <param name="clientId">게이지가 갱신된 플레이어의 NGO OwnerClientId.</param>
        /// <param name="current">현재 게이지 값 (0 ~ MaxGauge).</param>
        /// <param name="cumulative">누적 포인트 합계 (UI 미사용, 향후 리더보드용).</param>
        private void HandleGaugeSync(ulong clientId, float current, float cumulative)
        {
            // OwnerClientId live-read: Host Migration 후 owner 재배정 시에도 올바른 플레이어 필터링.
            if (_netObj == null || clientId != _netObj.OwnerClientId) return;

            bool hasProgress = current > 0f;

            if (_gaugeRoot != null)
                _gaugeRoot.SetActive(hasProgress);

            if (!hasProgress) return;

            float maxGauge = _gaugeSystem != null ? _gaugeSystem.MaxGauge : 120f;

            if (_gaugeSlider != null)
                _gaugeSlider.value = current / maxGauge;

            if (_fillImage != null)
                _fillImage.color = GetGaugeColor(current);
        }

        // ── 내부 유틸리티 ──────────────────────────────────────────────────────

        /// <summary>
        /// 게이지 값에 따른 Fill 색상을 반환한다.
        /// <list type="bullet">
        ///   <item>gauge &lt; 60 (50%) → 초록 #4CAF50</item>
        ///   <item>gauge &lt; 96 (80%) → 노랑 #FFC107</item>
        ///   <item>gauge >= 96        → 빨강 #F44336</item>
        /// </list>
        /// </summary>
        private static Color GetGaugeColor(float gauge)
        {
            if (gauge < ThresholdYellow) return ColorGreen;
            if (gauge < ThresholdRed)   return ColorYellow;
            return ColorRed;
        }
    }
}
