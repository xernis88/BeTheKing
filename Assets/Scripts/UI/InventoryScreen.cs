// Implements: production/epics/epic-ui-presentation/story-006-inventory-screen.md
// UX Spec: design/ux/inventory.md
//
// 설계 결정:
//   MonoBehaviour (NetworkBehaviour 아님): 렌더링 전용 오버레이. 서버 로직 없음.
//   OnEnable/OnDisable 구독 패턴: HUDManager 패턴 준수.
//   사망 이벤트: PlayerManager.OnPlayerDeathAnnounced → 즉시 닫힘 (GameOverUI 패턴).
//   InventoryEquipComponent 참조: Awake에서 로컬 플레이어 PlayerObject GetComponent.
//     로컬 플레이어 스폰 전에 컴포넌트가 없을 수 있으므로 OnPlayerSpawned 콜백으로 재시도.
//   오버레이 슬라이드 인/아웃: 코루틴 기반 0.15초/0.12초 (UX Spec §Transitions).
//   탭 전환: [캐릭터] ↔ [특성] 크로스페이드 0.1초.
//   Tab/I 키 입력: InputSystem이 아닌 Input.GetKeyDown으로 MVP 처리.
//     TODO: New Input System 전환 시 InputAction으로 교체.

using System.Collections;
using BeTheKing.CoreServices;
using BeTheKing.Networking;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

namespace BeTheKing.UI
{
    /// <summary>
    /// 인벤토리 오버레이의 메인 컨트롤러.
    /// <para>
    ///   Tab / I 키로 열기/닫기. ESC도 닫기.<br/>
    ///   [캐릭터] / [특성] 탭 전환.<br/>
    ///   사망 이벤트 수신 시 즉시 닫힘 (트랜지션 없음).
    /// </para>
    /// </summary>
    public class InventoryScreen : MonoBehaviour
    {
        // ── 상수 ───────────────────────────────────────────────────────────────

        private const float OpenDuration  = 0.15f; // UX Spec §Transitions
        private const float CloseDuration = 0.12f;
        private const float TabFadeDuration = 0.1f;

        // ── Inspector — 패널 루트 ─────────────────────────────────────────────

        [Header("패널")]
        [Tooltip("인벤토리 전체 루트. 기본 비활성.")]
        [SerializeField] private GameObject _rootPanel;

        [Tooltip("슬라이드 인/아웃 대상 RectTransform (루트 패널과 동일하거나 자식 패널).")]
        [SerializeField] private RectTransform _slideTarget;

        [Tooltip("화면 높이 대비 슬라이드 오프셋 비율 (하단 진입).")]
        [SerializeField] private float _slideOffsetRatio = 1.0f;

        [Header("탭 패널")]
        [Tooltip("[캐릭터] 탭 패널 GameObject.")]
        [SerializeField] private GameObject _characterPanel;

        [Tooltip("[특성] 탭 패널 GameObject.")]
        [SerializeField] private GameObject _traitPanel;

        [Header("탭 버튼")]
        [SerializeField] private Button _characterTabButton;
        [SerializeField] private Button _traitTabButton;
        [SerializeField] private Button _closeButton;

        [Header("탭 CanvasGroup (크로스페이드용)")]
        [SerializeField] private CanvasGroup _characterPanelGroup;
        [SerializeField] private CanvasGroup _traitPanelGroup;

        [Header("서브 컨트롤러")]
        [Tooltip("CharacterViewerController 참조. OnInventoryOpen/Close 연동.")]
        [SerializeField] private CharacterViewerController _characterViewerController;

        [Tooltip("InventoryCharacterPanel 참조.")]
        [SerializeField] private InventoryCharacterPanel _characterPanelController;

        [Tooltip("InventoryTraitPanel 참조.")]
        [SerializeField] private InventoryTraitPanel _traitPanelController;

        // ── 내부 상태 ──────────────────────────────────────────────────────────

        private bool _isOpen;
        private bool _isTransitioning;
        private InventoryEquipComponent _equipComponent;

        // 탭 전환 코루틴 참조 — StopCoroutine에 IEnumerator 방식으로 시작된 코루틴을
        // 문자열 StopCoroutine(nameof(...))으로 중지하면 무효이므로 직접 캐싱한다.
        private Coroutine _tabFadeCoroutine;

        // 슬라이드 시작 위치 (화면 하단 아래).
        private Vector2 _closedPosition;
        private Vector2 _openPosition;
        private bool _positionInitialized;

        // ── Unity 생명주기 ─────────────────────────────────────────────────────

        private void Awake()
        {
            ValidateInspectorFields();

            // 초기 상태: 닫힘.
            _rootPanel?.SetActive(false);
            _isOpen = false;

            // 버튼 이벤트 연결.
            if (_characterTabButton != null)
                _characterTabButton.onClick.AddListener(() => SwitchTab(isCharacterTab: true));
            if (_traitTabButton != null)
                _traitTabButton.onClick.AddListener(() => SwitchTab(isCharacterTab: false));
            if (_closeButton != null)
                _closeButton.onClick.AddListener(CloseInventory);
        }

        private void Start()
        {
            // 이미 로컬 플레이어가 스폰된 경우 즉시 EquipComponent 바인딩 시도.
            TryBindEquipComponent();
        }

        private void OnEnable()
        {
            PlayerManager.OnPlayerDeathAnnounced += HandlePlayerDeath;

            if (PlayerManager.Instance != null)
                PlayerManager.Instance.OnPlayerSpawned += HandlePlayerSpawned;
        }

        private void OnDisable()
        {
            PlayerManager.OnPlayerDeathAnnounced -= HandlePlayerDeath;

            if (PlayerManager.Instance != null)
                PlayerManager.Instance.OnPlayerSpawned -= HandlePlayerSpawned;

            StopAllCoroutines();
        }

        private void Update()
        {
            // Tab 또는 I 키: 토글
            if (Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.I))
            {
                if (_isOpen)
                    CloseInventory();
                else
                    OpenInventory();
            }

            // ESC: 닫기만
            if (_isOpen && Input.GetKeyDown(KeyCode.Escape))
                CloseInventory();
        }

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 인벤토리를 연다. 이미 열려있거나 트랜지션 중이면 무시한다.
        /// </summary>
        public void OpenInventory()
        {
            if (_isOpen || _isTransitioning) return;
            StartCoroutine(OpenRoutine());
        }

        /// <summary>
        /// 인벤토리를 닫는다.
        /// </summary>
        public void CloseInventory()
        {
            if (!_isOpen && !_isTransitioning) return;
            // 플래그를 즉시 설정해 Update()에서 재진입을 막는다.
            // OpenRoutine이 진행 중이어도 StopAllCoroutines()로 중단 후 CloseRoutine 시작.
            _isOpen = false;
            _isTransitioning = true;
            StopAllCoroutines();
            StartCoroutine(CloseRoutine());
        }

        // ── 이벤트 핸들러 ─────────────────────────────────────────────────────

        private void HandlePlayerDeath(ulong clientId)
        {
            if (!IsLocalClient(clientId)) return;
            ForceClose();
        }

        private void HandlePlayerSpawned(ulong clientId)
        {
            if (!IsLocalClient(clientId)) return;
            TryBindEquipComponent();
        }

        // ── 오버레이 트랜지션 코루틴 ───────────────────────────────────────────

        private IEnumerator OpenRoutine()
        {
            _isTransitioning = true;
            _isOpen = true;

            EnsurePositionInitialized();

            _rootPanel?.SetActive(true);

            // 캐릭터 패널 기본값으로 열기.
            SwitchTab(isCharacterTab: true, instant: true);

            _characterViewerController?.OnInventoryOpen();
            _characterPanelController?.RefreshAll(_equipComponent);

            // 슬라이드 인 (하단 → 원래 위치).
            if (_slideTarget != null)
            {
                float elapsed = 0f;
                while (elapsed < OpenDuration)
                {
                    elapsed += Time.unscaledDeltaTime;
                    float t = Mathf.Clamp01(elapsed / OpenDuration);
                    float eased = 1f - (1f - t) * (1f - t); // ease-out quad
                    _slideTarget.anchoredPosition = Vector2.Lerp(_closedPosition, _openPosition, eased);
                    yield return null;
                }
                _slideTarget.anchoredPosition = _openPosition;
            }

            _isTransitioning = false;
        }

        private IEnumerator CloseRoutine()
        {
            // _isOpen, _isTransitioning은 CloseInventory()에서 이미 설정됨.
            EnsurePositionInitialized();

            // 슬라이드 아웃 (원래 위치 → 하단).
            if (_slideTarget != null)
            {
                float elapsed = 0f;
                while (elapsed < CloseDuration)
                {
                    elapsed += Time.unscaledDeltaTime;
                    float t = Mathf.Clamp01(elapsed / CloseDuration);
                    float eased = t * t; // ease-in quad
                    _slideTarget.anchoredPosition = Vector2.Lerp(_openPosition, _closedPosition, eased);
                    yield return null;
                }
                _slideTarget.anchoredPosition = _closedPosition;
            }

            _characterViewerController?.OnInventoryClose();
            _rootPanel?.SetActive(false);

            _isTransitioning = false;
        }

        /// <summary>
        /// 사망 이벤트 등 즉시 닫힘. 트랜지션 없음 (UX Spec §States).
        /// </summary>
        private void ForceClose()
        {
            StopAllCoroutines();
            _characterViewerController?.OnInventoryClose();
            _rootPanel?.SetActive(false);
            _isOpen = false;
            _isTransitioning = false;
        }

        // ── 탭 전환 ───────────────────────────────────────────────────────────

        private void SwitchTab(bool isCharacterTab, bool instant = false)
        {
            if (_tabFadeCoroutine != null) StopCoroutine(_tabFadeCoroutine);
            _tabFadeCoroutine = StartCoroutine(TabFadeRoutine(isCharacterTab, instant));
        }

        private IEnumerator TabFadeRoutine(bool isCharacterTab, bool instant)
        {
            CanvasGroup showGroup = isCharacterTab ? _characterPanelGroup : _traitPanelGroup;
            CanvasGroup hideGroup = isCharacterTab ? _traitPanelGroup : _characterPanelGroup;
            GameObject  showPanel = isCharacterTab ? _characterPanel : _traitPanel;
            GameObject  hidePanel = isCharacterTab ? _traitPanel : _characterPanel;

            showPanel?.SetActive(true);

            if (instant)
            {
                if (showGroup != null) { showGroup.alpha = 1f; showGroup.interactable = true; showGroup.blocksRaycasts = true; }
                if (hideGroup != null) { hideGroup.alpha = 0f; hideGroup.interactable = false; hideGroup.blocksRaycasts = false; }
                hidePanel?.SetActive(false);
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < TabFadeDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / TabFadeDuration);
                if (showGroup != null) showGroup.alpha = t;
                if (hideGroup != null) hideGroup.alpha = 1f - t;
                yield return null;
            }

            if (showGroup != null) { showGroup.alpha = 1f; showGroup.interactable = true; showGroup.blocksRaycasts = true; }
            if (hideGroup != null) { hideGroup.alpha = 0f; hideGroup.interactable = false; hideGroup.blocksRaycasts = false; }
            hidePanel?.SetActive(false);
        }

        // ── EquipComponent 바인딩 ──────────────────────────────────────────────

        private void TryBindEquipComponent()
        {
            if (_equipComponent != null) return;
            if (NetworkManager.Singleton == null) return;

            var playerObj = NetworkManager.Singleton.LocalClient?.PlayerObject;
            if (playerObj == null) return;

            _equipComponent = playerObj.GetComponent<InventoryEquipComponent>();
            if (_equipComponent == null)
                Debug.LogWarning("[InventoryScreen] 로컬 플레이어 PlayerObject에서 InventoryEquipComponent를 찾지 못했습니다.", this);
        }

        // ── 슬라이드 위치 초기화 ────────────────────────────────────────────────

        private void EnsurePositionInitialized()
        {
            if (_positionInitialized || _slideTarget == null) return;

            _openPosition   = _slideTarget.anchoredPosition;
            float screenH   = Screen.height;
            _closedPosition = _openPosition + Vector2.down * screenH * _slideOffsetRatio;
            _positionInitialized = true;
        }

        // ── 헬퍼 ──────────────────────────────────────────────────────────────

        private static bool IsLocalClient(ulong clientId)
            => NetworkManager.Singleton != null
            && clientId == NetworkManager.Singleton.LocalClientId;

        // ── Inspector 검증 ─────────────────────────────────────────────────────

        private void ValidateInspectorFields()
        {
            if (_rootPanel == null)
                Debug.LogError("[InventoryScreen] _rootPanel이 Inspector에 연결되지 않았습니다.", this);
            if (_characterPanel == null)
                Debug.LogError("[InventoryScreen] _characterPanel이 Inspector에 연결되지 않았습니다.", this);
            if (_traitPanel == null)
                Debug.LogError("[InventoryScreen] _traitPanel이 Inspector에 연결되지 않았습니다.", this);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_rootPanel == null)
                Debug.LogWarning("[InventoryScreen] _rootPanel 연결 필요.", this);
        }
#endif
    }
}
