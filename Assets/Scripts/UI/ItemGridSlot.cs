// Implements: production/epics/epic-ui-presentation/story-006-inventory-screen.md
// UX Spec: design/ux/inventory.md §Component Inventory
//
// 설계 결정:
//   IDragHandler/IBeginDragHandler/IEndDragHandler/IDropHandler: 드래그 secondary 인터랙션 지원.
//   클릭 primary 인터랙션: IPointerClickHandler로 선택 상태 토글.
//   등급 색상 + 등급명 텍스트 병기: 색맹 접근성 필수 조건 (design/ux/inventory.md §Accessibility).
//   등급 팔레트: Common=#D4A853(황토), Rare=#B8A9D4(라벤더), Heroic=#F4C842(골드) (story-006 비주얼 방향).

using BeTheKing.CoreServices;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

namespace BeTheKing.UI
{
    /// <summary>
    /// 인벤토리 그리드의 개별 슬롯 컴포넌트.
    /// <para>
    ///   클릭 primary: 슬롯을 선택 상태로 변경하고 <see cref="OnSlotSelected"/> 이벤트를 발행한다.<br/>
    ///   드래그 secondary: BeginDrag → 드래그 중 고스트 표시 → Drop으로 슬롯 간 이동.
    /// </para>
    /// </summary>
    public class ItemGridSlot : MonoBehaviour,
        IPointerClickHandler,
        IPointerEnterHandler,
        IPointerExitHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler,
        IDropHandler
    {
        // ── 등급 색상 팔레트 (story-006 비주얼 방향성) ───────────────────────────

        private static readonly Color ColorCommon = new Color(0.831f, 0.659f, 0.325f, 1f); // #D4A853
        private static readonly Color ColorRare   = new Color(0.722f, 0.663f, 0.831f, 1f); // #B8A9D4
        private static readonly Color ColorHeroic = new Color(0.957f, 0.784f, 0.255f, 1f); // #F4C842
        private static readonly Color ColorEmpty  = new Color(0.5f, 0.5f, 0.5f, 0.4f);

        // ── Inspector ──────────────────────────────────────────────────────────

        [Header("슬롯 UI 요소")]
        [Tooltip("아이템 아이콘 Image.")]
        [SerializeField] private Image _itemIcon;

        [Tooltip("등급 프레임/테두리 Image.")]
        [SerializeField] private Image _gradeFrame;

        [Tooltip("등급명 텍스트 (접근성: 색상+텍스트 병기).")]
        [SerializeField] private TMP_Text _gradeLabel;

        [Tooltip("선택 하이라이트 오버레이 Image.")]
        [SerializeField] private Image _selectionHighlight;

        [Tooltip("현재 장착 강조 테두리 Image.")]
        [SerializeField] private Image _equippedBorder;

        [Tooltip("드래그 중 고스트 이미지 프리팹.")]
        [SerializeField] private GameObject _dragGhostPrefab;

        // ── 이벤트 ────────────────────────────────────────────────────────────

        /// <summary>슬롯 클릭 선택 시 발행. 파라미터: 선택된 ItemData.</summary>
        public event System.Action<ItemGridSlot> OnSlotSelected;

        /// <summary>슬롯 호버 진입 시 발행 (툴팁 표시용).</summary>
        public event System.Action<ItemGridSlot> OnSlotHoverEnter;

        /// <summary>슬롯 호버 이탈 시 발행 (툴팁 숨김용).</summary>
        public event System.Action<ItemGridSlot> OnSlotHoverExit;

        /// <summary>드롭 대상으로 선택되었을 때 발행. (소스 슬롯, 대상 슬롯).</summary>
        public event System.Action<ItemGridSlot, ItemGridSlot> OnSlotDropReceived;

        // ── 내부 상태 ──────────────────────────────────────────────────────────

        private ItemData _itemData;
        private bool _isEmpty = true;
        private bool _isSelected;
        private bool _isEquipped;
        private bool _isDragging;

        private GameObject _dragGhostInstance;
        private Canvas _rootCanvas; // 드래그 고스트 좌표계 기준

        // ── 프로퍼티 ──────────────────────────────────────────────────────────

        /// <summary>슬롯에 배치된 아이템 데이터. 빈 슬롯이면 default.</summary>
        public ItemData ItemData => _itemData;

        /// <summary>슬롯이 비어 있으면 true.</summary>
        public bool IsEmpty => _isEmpty;

        /// <summary>현재 장착 아이템이면 true.</summary>
        public bool IsEquipped => _isEquipped;

        // ── Unity 생명주기 ─────────────────────────────────────────────────────

        private void Awake()
        {
            // 드래그 고스트 좌표계: 루트 Canvas 기준.
            _rootCanvas = GetComponentInParent<Canvas>();
            RefreshVisual();
        }

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 슬롯에 아이템 데이터를 설정하고 비주얼을 갱신한다.
        /// </summary>
        public void SetItem(ItemData itemData, Sprite icon)
        {
            _itemData = itemData;
            _isEmpty  = !itemData.IsValid;
            if (_itemIcon != null)
                _itemIcon.sprite = icon;
            RefreshVisual();
        }

        /// <summary>
        /// 슬롯을 빈 상태로 초기화한다.
        /// </summary>
        public void Clear()
        {
            _itemData = default;
            _isEmpty  = true;
            if (_itemIcon != null)
                _itemIcon.sprite = null;
            RefreshVisual();
        }

        /// <summary>
        /// 선택 상태를 외부에서 설정한다 (그리드 매니저가 단일 선택 유지용으로 호출).
        /// </summary>
        public void SetSelected(bool selected)
        {
            _isSelected = selected;
            if (_selectionHighlight != null)
                _selectionHighlight.gameObject.SetActive(selected);
        }

        /// <summary>
        /// 장착 강조 테두리를 표시/숨김 처리한다.
        /// </summary>
        public void SetEquipped(bool equipped)
        {
            _isEquipped = equipped;
            if (_equippedBorder != null)
                _equippedBorder.gameObject.SetActive(equipped);
        }

        // ── IPointerClickHandler ───────────────────────────────────────────────

        public void OnPointerClick(PointerEventData eventData)
        {
            if (_isEmpty) return;
            OnSlotSelected?.Invoke(this);
        }

        // ── IPointerEnterHandler / IPointerExitHandler (툴팁) ─────────────────

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (!_isEmpty)
                OnSlotHoverEnter?.Invoke(this);
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            OnSlotHoverExit?.Invoke(this);
        }

        // ── IBeginDragHandler ─────────────────────────────────────────────────

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (_isEmpty) return;
            _isDragging = true;
            SpawnDragGhost(eventData);
        }

        // ── IDragHandler ──────────────────────────────────────────────────────

        public void OnDrag(PointerEventData eventData)
        {
            if (!_isDragging || _dragGhostInstance == null) return;
            MoveDragGhost(eventData);
        }

        // ── IEndDragHandler ───────────────────────────────────────────────────

        public void OnEndDrag(PointerEventData eventData)
        {
            _isDragging = false;
            DestroyDragGhost();
        }

        // ── IDropHandler ──────────────────────────────────────────────────────

        public void OnDrop(PointerEventData eventData)
        {
            // 드롭 소스가 ItemGridSlot인지 확인.
            var source = eventData.pointerDrag?.GetComponent<ItemGridSlot>();
            if (source == null || source == this) return;

            OnSlotDropReceived?.Invoke(source, this);
        }

        // ── 비주얼 갱신 ────────────────────────────────────────────────────────

        private void RefreshVisual()
        {
            if (_isEmpty)
            {
                SetGradeVisual(-1);
                if (_itemIcon != null) _itemIcon.color = new Color(1f, 1f, 1f, 0f);
            }
            else
            {
                if (_itemIcon != null) _itemIcon.color = Color.white;
                SetGradeVisual(_itemData.Grade);
            }

            // 선택/장착 상태는 외부에서 직접 제어하므로 여기서는 유지.
        }

        /// <summary>
        /// 등급에 따른 프레임 색상과 레이블 텍스트를 설정한다.
        /// 접근성: 색상 단독 사용 금지 — 텍스트 병기 필수.
        /// </summary>
        /// <param name="grade">0=Common, 1=Rare, 2=Heroic. -1=Empty.</param>
        private void SetGradeVisual(int grade)
        {
            Color frameColor;
            string labelText;

            switch (grade)
            {
                case 0:
                    frameColor = ColorCommon;
                    labelText  = "일반"; // TODO: 로컬라이제이션 시스템 도입 시 교체
                    break;
                case 1:
                    frameColor = ColorRare;
                    labelText  = "희귀";
                    break;
                case 2:
                    frameColor = ColorHeroic;
                    labelText  = "영웅";
                    break;
                default:
                    frameColor = ColorEmpty;
                    labelText  = string.Empty;
                    break;
            }

            if (_gradeFrame != null)
                _gradeFrame.color = frameColor;

            if (_gradeLabel != null)
                _gradeLabel.text = labelText;
        }

        // ── 드래그 고스트 ──────────────────────────────────────────────────────

        private void SpawnDragGhost(PointerEventData eventData)
        {
            if (_dragGhostPrefab == null || _rootCanvas == null) return;

            _dragGhostInstance = Instantiate(_dragGhostPrefab, _rootCanvas.transform);
            MoveDragGhost(eventData);

            // 고스트는 레이캐스트 무시 (드롭 판정 방해 방지).
            var canvasGroup = _dragGhostInstance.GetComponent<CanvasGroup>();
            if (canvasGroup != null) canvasGroup.blocksRaycasts = false;
        }

        private void MoveDragGhost(PointerEventData eventData)
        {
            if (_dragGhostInstance == null) return;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _rootCanvas.transform as RectTransform,
                eventData.position,
                eventData.pressEventCamera,
                out Vector2 localPos);
            (_dragGhostInstance.transform as RectTransform).anchoredPosition = localPos;
        }

        private void DestroyDragGhost()
        {
            if (_dragGhostInstance != null)
            {
                Destroy(_dragGhostInstance);
                _dragGhostInstance = null;
            }
        }
    }
}
