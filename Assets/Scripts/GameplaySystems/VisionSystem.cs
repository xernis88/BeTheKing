// Implements: design/gdd/02-gameplay-systems.md — VisionSystem
// Story: production/epics/epic-gameplay-systems/story-003-vision-lantern.md
// ADR: docs/architecture/ADR-014-vision-lantern-fow-render-feature.md
//
// 설계 결정:
//   MonoBehaviour 싱글턴. FOW 반경 갱신 책임만 담당.
//   SessionTimeManager.OnDayStarted → SetFullVision (float.MaxValue).
//   SessionTimeManager.OnNightStarted → UpdateNightVision (등불 여부에 따라 반경 분기).
//   LanternSystem.HandleIsOnChanged → UpdateNightVision 직접 호출.

using BeTheKing.Foundation;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// FOW(Fog of War) 반경을 낮/밤 전환 및 등불 상태에 따라 갱신한다.
    /// <para>
    ///   - 낮: <see cref="SetFullVision"/>으로 FOW 비활성화(float.MaxValue).
    ///   - 밤: <see cref="UpdateNightVision"/>으로 등불 여부에 따라 반경 설정.
    ///   - <see cref="FogOfWarRenderFeature"/>에 반경을 전달해 실제 렌더링 파라미터를 갱신한다.
    /// </para>
    /// </summary>
    public class VisionSystem : MonoBehaviour
    {
        // ── Inspector (밸런스 튜닝 가능) ────────────────────────────────────────

        [Header("Balance — 밸런스 시 확정")]
        [Tooltip("밤 기본 가시 반경(월드 단위). 등불 없을 때 적용.")]
        [SerializeField] private float nightVisionRadius = 8f;

        [Tooltip("등불 켜짐 시 가시 반경(월드 단위).")]
        [SerializeField] private float lanternVisionRadius = 15f;

        [Header("References")]
        [Tooltip("FOW 반경 파라미터를 수신하는 ScriptableRendererFeature. Inspector에서 연결.")]
        [SerializeField] private FogOfWarRenderFeature _fowFeature;

        // ── 싱글턴 ────────────────────────────────────────────────────────────

        /// <summary>씬의 단일 VisionSystem 인스턴스. LanternSystem이 참조한다.</summary>
        public static VisionSystem Instance { get; private set; }

        // ── 내부 참조 ─────────────────────────────────────────────────────────

        private LanternSystem _lantern;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void Start()
        {
            // ?.로 이벤트 구독 불가(C# 제약) — 로컬 변수에 저장 후 null 체크.
            var stm = SessionTimeManager.Instance;
            if (stm != null)
            {
                stm.OnDayStarted  += HandleDayStarted;
                stm.OnNightStarted += HandleNightStarted;
            }

            _lantern = LanternSystem.Instance;
        }

        private void OnDestroy()
        {
            var stm = SessionTimeManager.Instance;
            if (stm != null)
            {
                stm.OnDayStarted  -= HandleDayStarted;
                stm.OnNightStarted -= HandleNightStarted;
            }

            if (Instance == this)
                Instance = null;
        }

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// FOW를 비활성화한다(낮 상태). FOW 반경을 float.MaxValue로 설정해 전체 맵을 가시화한다.
        /// </summary>
        public void SetFullVision()
        {
            _fowFeature?.SetVisionRadius(float.MaxValue);
        }

        /// <summary>
        /// 현재 등불 상태에 따라 밤 FOW 반경을 설정한다.
        /// 등불 ON → <see cref="lanternVisionRadius"/>, 등불 OFF → <see cref="nightVisionRadius"/>.
        /// </summary>
        public void UpdateNightVision()
        {
            // LanternSystem이 늦게 Spawn되는 경우를 대비해 재참조한다.
            if (_lantern == null)
                _lantern = LanternSystem.Instance;

            float radius = (_lantern != null && _lantern.IsOn)
                ? lanternVisionRadius
                : nightVisionRadius;

            _fowFeature?.SetVisionRadius(radius);
        }

        // ── 내부 이벤트 핸들러 ─────────────────────────────────────────────────

        // System.Action<int> — day 파라미터는 현재 미사용(TO-VERIFY: 향후 일차별 분기 필요 시 활용).
        private void HandleDayStarted(int day)   => SetFullVision();
        private void HandleNightStarted(int day) => UpdateNightVision();
    }
}
