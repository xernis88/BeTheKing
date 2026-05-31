// Implements: design/gdd/05-ui-presentation.md — DayNightUI
// Story: production/epics/epic-ui-presentation/story-004-day-night-ui.md
// Requirement: TR-UI-006, TR-UI-007

using System.Collections;
using BeTheKing.Foundation;
using BeTheKing.GameplaySystems;
using TMPro;
using UnityEngine;

namespace BeTheKing.UI
{
    /// <summary>
    /// 낮/밤 전환 오버레이, 시간 카운트다운, 대관식 배너를 담당한다.
    /// <para>
    ///   데이터 소스:
    ///   <list type="bullet">
    ///     <item><see cref="SessionTimeManager.OnDayStarted"/> / <see cref="SessionTimeManager.OnNightStarted"/> — 페이드 트리거</item>
    ///     <item><see cref="CoronationTrigger.OnCoronationAnnounced"/> — 대관식 배너 트리거</item>
    ///     <item><see cref="SessionTimeManager.PhaseRemainingTime"/> — Update 폴링으로 카운트다운 표시</item>
    ///   </list>
    /// </para>
    /// </summary>
    public class DayNightUI : MonoBehaviour
    {
        [Header("Overlay")]
        [Tooltip("밤 오버레이. alpha 0 = 낮, alpha = _nightAlpha = 밤.")]
        [SerializeField] private CanvasGroup _nightOverlay;
        [SerializeField] private float _fadeDuration = 3f;
        [Tooltip("밤 오버레이 최대 alpha. 0 = 낮, 1 = 완전 어둠. 밸런스 조정용.")]
        [SerializeField] private float _nightAlpha = 0.6f;

        [Header("HUD")]
        [SerializeField] private TMP_Text _timeText;

        [Header("Coronation")]
        [SerializeField] private GameObject _coronationBanner;

        private Coroutine _activeFade;
        // SessionTimeManager는 NetworkBehaviour이므로 OnEnable 시점에 Instance가 없을 수 있다.
        // Update에서 지연 구독을 보장하기 위해 플래그로 추적한다.
        private bool _subscribed;
        // GC 최소화: 초 단위가 바뀔 때만 문자열 재빌드 (매 프레임 할당 방지).
        private int _lastDisplaySec = -1;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void OnEnable()
        {
            CoronationTrigger.OnCoronationAnnounced += HandleCoronationAnnounced;
            TrySubscribeToTimeManager();
        }

        private void OnDisable()
        {
            CoronationTrigger.OnCoronationAnnounced -= HandleCoronationAnnounced;

            if (_subscribed && SessionTimeManager.Instance != null)
            {
                SessionTimeManager.Instance.OnDayStarted   -= HandleDayStarted;
                SessionTimeManager.Instance.OnNightStarted -= HandleNightStarted;
            }
            _subscribed = false;
            _lastDisplaySec = -1;

            if (_activeFade != null)
            {
                StopCoroutine(_activeFade);
                _activeFade = null;
            }
        }

        private void Update()
        {
            var tm = SessionTimeManager.Instance;
            if (tm == null) return;

            // SessionTimeManager가 늦게 스폰되는 경우 지연 구독
            if (!_subscribed) TrySubscribeToTimeManager();

            if (_timeText == null) return;

            float remaining = tm.PhaseRemainingTime;
            int   totalSec  = Mathf.FloorToInt(remaining);
            if (totalSec == _lastDisplaySec) return;
            _lastDisplaySec = totalSec;

            string phase = tm.Phase == DayPhase.Day ? "Day" : "Night"; // TODO: 한국어 폰트 임포트 후 교체
            int    min   = totalSec / 60;
            int    sec   = totalSec % 60;
            _timeText.text = $"Day {tm.CurrentDay} — {phase} ({min}:{sec:D2})";
        }

        // ── Event Handlers ─────────────────────────────────────────────────────

        private void HandleDayStarted(int day)   => TriggerFade(0f);
        private void HandleNightStarted(int day) => TriggerFade(_nightAlpha);

        private void HandleCoronationAnnounced()
        {
            if (_coronationBanner == null) return;
            _coronationBanner.SetActive(true);
            StartCoroutine(HideBannerAfter(5f));
        }

        // ── Fade ───────────────────────────────────────────────────────────────

        private void TriggerFade(float target)
        {
            if (_activeFade != null) StopCoroutine(_activeFade);
            _activeFade = StartCoroutine(FadeTo(target));
        }

        private IEnumerator FadeTo(float target)
        {
            if (_nightOverlay == null) yield break;
            float start   = _nightOverlay.alpha;
            float elapsed = 0f;
            while (elapsed < _fadeDuration)
            {
                elapsed             += Time.deltaTime;
                _nightOverlay.alpha  = Mathf.Lerp(start, target, elapsed / _fadeDuration);
                yield return null;
            }
            _nightOverlay.alpha = target;
            _activeFade         = null;
        }

        private IEnumerator HideBannerAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            if (_coronationBanner != null)
                _coronationBanner.SetActive(false);
        }

        // ── Helper ─────────────────────────────────────────────────────────────

        private void TrySubscribeToTimeManager()
        {
            var tm = SessionTimeManager.Instance;
            if (tm == null || _subscribed) return;

            tm.OnDayStarted   += HandleDayStarted;
            tm.OnNightStarted += HandleNightStarted;
            _subscribed        = true;

            // 구독 누락 구간에 이미 페이즈가 전환됐을 경우 현재 상태로 즉시 동기화
            TriggerFade(tm.Phase == DayPhase.Day ? 0f : 0.6f);
        }
    }
}
