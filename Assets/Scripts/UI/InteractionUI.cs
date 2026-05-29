// Implements: design/gdd/05-ui-presentation.md — InteractionUI
// Story: production/epics/epic-ui-presentation/story-002-interaction-ui.md
// Requirement: TR-UI-002, TR-UI-003
//
// 설계 결정:
//   스킬체크 이벤트: JobInteractionSystem.OnSkillCheckBegin/End (static) → OnEnable/OnDisable 직접 구독.
//   홀드 이벤트: GeneralInteractionSystem (per-player NetworkBehaviour, 싱글톤 없음)
//               → 로컬 플레이어 PlayerObject GetComponent 바인딩 (HUDManager 패턴).
//   게이지 침 회전: Update에서 _skillCheckActive 조건 매 프레임 Rotate.
//   SubmitInputServerRpc 입력 처리: PlayerController 책임 (이 클래스 스코프 밖).

using System.Collections;
using BeTheKing.CoreServices;
using BeTheKing.GameplaySystems;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace BeTheKing.UI
{
    /// <summary>
    /// 스킬체크 원형 게이지와 일반 상호작용 홀드 게이지를 담당한다. Screen Space Canvas에 부착.
    /// <para>
    ///   스킬체크: <see cref="JobInteractionSystem.OnSkillCheckBegin"/> / <see cref="JobInteractionSystem.OnSkillCheckEnd"/> 구독.<br/>
    ///   홀드 게이지: 로컬 플레이어 PlayerObject의 <see cref="GeneralInteractionSystem"/> 에 바인딩.
    /// </para>
    /// </summary>
    public class InteractionUI : MonoBehaviour
    {
        [Header("Skill Check")]
        [Tooltip("스킬체크 패널 루트. 기본 비활성.")]
        [SerializeField] private GameObject _skillCheckPanel;
        [Tooltip("회전 침 Transform.")]
        [SerializeField] private Transform _needle;
        [Tooltip("성공 구간 하이라이트 이미지.")]
        [SerializeField] private Image _successZone;
        [Tooltip("성공/실패 피드백 Flash 이미지.")]
        [SerializeField] private Image _feedbackImage;
        [Tooltip("침 회전 속도(도/초). 밸런스 조정용.")]
        [SerializeField] private float _needleSpeed = 90f;
        [Tooltip("성공 피드백 Flash 색상.")]
        [SerializeField] private Color _successColor = new Color(0.298f, 0.686f, 0.314f, 0.8f);
        [Tooltip("실패 피드백 Flash 색상.")]
        [SerializeField] private Color _failColor    = new Color(0.957f, 0.263f, 0.212f, 0.8f);
        [Tooltip("피드백 Flash 지속 시간(초). 밸런스 조정용.")]
        [SerializeField] private float _flashDuration = 0.3f;

        [Header("Hold Gauge")]
        [Tooltip("홀드 게이지 패널 루트. 기본 비활성.")]
        [SerializeField] private GameObject _holdPanel;
        [SerializeField] private Slider _holdSlider;

        private bool _skillCheckActive;
        private bool _holdBound;
        private GeneralInteractionSystem _localHoldSystem;

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private void Start()
        {
            _skillCheckPanel?.SetActive(false);
            _holdPanel?.SetActive(false);
            if (_holdSlider != null) _holdSlider.value = 0f;
        }

        private void OnEnable()
        {
            JobInteractionSystem.OnSkillCheckBegin += HandleSkillCheckBegin;
            JobInteractionSystem.OnSkillCheckEnd   += HandleSkillCheckEnd;
            TryBindHoldSystem();
        }

        private void OnDisable()
        {
            JobInteractionSystem.OnSkillCheckBegin -= HandleSkillCheckBegin;
            JobInteractionSystem.OnSkillCheckEnd   -= HandleSkillCheckEnd;
            UnbindHoldSystem();

            // 코루틴 강제 중단 — 중단 시 피드백 이미지가 중간 색상으로 고착되는 것을 방지.
            StopAllCoroutines();
            if (_feedbackImage != null) _feedbackImage.color = Color.clear;
            _skillCheckActive = false;
            _skillCheckPanel?.SetActive(false);
            _holdPanel?.SetActive(false);
        }

        private void Update()
        {
            if (_skillCheckActive && _needle != null)
                _needle.Rotate(0f, 0f, -_needleSpeed * Time.deltaTime);

            if (!_holdBound)
                TryBindHoldSystem();
        }

        // ── Skill Check ────────────────────────────────────────────────────────

        private void HandleSkillCheckBegin(float zoneStart, float zoneEnd)
        {
            _skillCheckPanel?.SetActive(true);
            _skillCheckActive = true;

            if (_successZone != null)
                _successZone.transform.rotation = Quaternion.Euler(0f, 0f, -zoneStart);
            // zoneEnd: reserved — 성공 구간 폭 시각화에 사용 예정 (현재 미구현)
        }

        private void HandleSkillCheckEnd(bool success)
        {
            _skillCheckActive = false;
            _skillCheckPanel?.SetActive(false);
            StartCoroutine(success ? FlashGreen() : FlashRed());
        }

        private IEnumerator FlashGreen()
        {
            if (_feedbackImage == null) yield break;
            _feedbackImage.color = _successColor;
            yield return new WaitForSeconds(_flashDuration);
            _feedbackImage.color = Color.clear;
        }

        private IEnumerator FlashRed()
        {
            if (_feedbackImage == null) yield break;
            _feedbackImage.color = _failColor;
            yield return new WaitForSeconds(_flashDuration);
            _feedbackImage.color = Color.clear;
        }

        // ── Hold Gauge ─────────────────────────────────────────────────────────

        private void ShowHoldGauge()
        {
            _holdPanel?.SetActive(true);
            if (_holdSlider != null) _holdSlider.value = 0f;
        }

        private void HideHoldGauge()
        {
            _holdPanel?.SetActive(false);
            if (_holdSlider != null) _holdSlider.value = 0f;
        }

        private void UpdateHoldProgress(float progress)
        {
            if (_holdSlider != null) _holdSlider.value = progress;
        }

        // ── Hold System Binding ────────────────────────────────────────────────

        private void TryBindHoldSystem()
        {
            if (_holdBound) return;
            if (NetworkManager.Singleton == null) return;

            var localClient = NetworkManager.Singleton.LocalClient;
            if (localClient?.PlayerObject == null) return;

            var sys = localClient.PlayerObject.GetComponent<GeneralInteractionSystem>();
            if (sys == null) return;

            _localHoldSystem             = sys;
            _localHoldSystem.OnHoldStarted   += ShowHoldGauge;
            _localHoldSystem.OnHoldProgress  += UpdateHoldProgress;
            _localHoldSystem.OnHoldCancelled += HideHoldGauge;
            _holdBound = true;
        }

        private void UnbindHoldSystem()
        {
            if (!_holdBound || _localHoldSystem == null) return;
            _localHoldSystem.OnHoldStarted   -= ShowHoldGauge;
            _localHoldSystem.OnHoldProgress  -= UpdateHoldProgress;
            _localHoldSystem.OnHoldCancelled -= HideHoldGauge;
            _localHoldSystem = null;
            _holdBound       = false;
        }
    }
}
