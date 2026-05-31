// Implements: production/epics/epic-ui-presentation/story-006-inventory-screen.md
// UX Spec: design/ux/inventory.md §인벤토리 패널
//
// 설계 결정:
//   ScrollRect + GridLayoutGroup (5열×4행=20슬롯, 초과 시 스크롤).
//   클릭 primary 인터랙션: ItemGridSlot.OnSlotSelected → 클릭 장착 플로우.
//   현재 장착 아이템 강조 테두리: ItemGridSlot.SetEquipped(true).
//   키보드 방향키 탐색: _focusedIndex + 5열 그리드 좌표 계산.
//     그리드 좌측 끝에서 방향키 좌 → InventoryCharacterPanel 포커스 이동 요청.
//   그리드 로딩 스피너 + 2초 타임아웃 + 에러 상태.
//   툴팁: ItemGridSlot.OnSlotHoverEnter/Exit → 인라인 툴팁 패널 표시.
//   드롭 수신: ItemGridSlot.OnSlotDropReceived → 슬롯 간 이동 처리.

using System.Collections;
using System.Collections.Generic;
using BeTheKing.CoreServices;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace BeTheKing.UI
{
    /// <summary>
    /// 인벤토리 그리드 패널.
    /// <para>
    ///   5열 × 가변 행 ScrollRect 그리드에 아이템을 표시하고,<br/>
    ///   클릭 선택 → 슬롯 장착 플로우, 방향키 탐색, 드래그 앤 드롭을 처리한다.
    /// </para>
    /// </summary>
    public class InventoryGridPanel : MonoBehaviour
    {
        // ── 상수 ───────────────────────────────────────────────────────────────

        private const int  Columns           = 5;
        private const int  DefaultSlotCount  = 20; // 5열×4행
        private const float LoadTimeoutSec   = 2f;

        // ── Inspector ──────────────────────────────────────────────────────────

        [Header("그리드")]
        [Tooltip("아이템 슬롯 프리팹 (ItemGridSlot 컴포넌트 포함).")]
        [SerializeField] private ItemGridSlot _slotPrefab;

        [Tooltip("GridLayoutGroup이 부착된 Content RectTransform.")]
        [SerializeField] private RectTransform _gridContent;

        [Tooltip("ScrollRect 컴포넌트.")]
        [SerializeField] private ScrollRect _scrollRect;

        [Header("로딩 / 에러")]
        [Tooltip("로딩 스피너 패널.")]
        [SerializeField] private GameObject _loadingPanel;

        [Tooltip("에러 패널 (2초 타임아웃 후 표시).")]
        [SerializeField] private GameObject _errorPanel;

        [Tooltip("에러 메시지 텍스트.")]
        [SerializeField] private TMP_Text _errorText;

        [Header("툴팁")]
        [Tooltip("아이템 툴팁 패널 루트.")]
        [SerializeField] private GameObject _tooltipPanel;

        [Tooltip("툴팁 아이템명 텍스트.")]
        [SerializeField] private TMP_Text _tooltipNameText;

        [Tooltip("툴팁 등급 텍스트.")]
        [SerializeField] private TMP_Text _tooltipGradeText;

        [Tooltip("툴팁 스탯/설명 텍스트.")]
        [SerializeField] private TMP_Text _tooltipDescText;

        // ── 이벤트 ────────────────────────────────────────────────────────────

        /// <summary>
        /// 아이템을 복장 슬롯에 장착 요청 시 발행.
        /// InventoryScreen → InventoryCharacterPanel.OnCostumeEquipRequested 로 전달.
        /// </summary>
        public event System.Action<ItemGridSlot, Sprite> OnCostumeEquipRequested;

        /// <summary>
        /// 아이템을 무기 슬롯에 장착 요청 시 발행.
        /// </summary>
        public event System.Action<ItemGridSlot, Sprite> OnWeaponEquipRequested;

        /// <summary>
        /// 그리드 좌측 경계에서 방향키 좌 입력 시 발행 (패널 포커스 이동 요청).
        /// </summary>
        public event System.Action OnFocusMoveLeft;

        // ── 내부 상태 ──────────────────────────────────────────────────────────

        private readonly List<ItemGridSlot> _slots = new();
        private readonly List<ItemData>     _items = new();
        private readonly List<Sprite>       _icons = new();

        private int  _focusedIndex = 0;
        private bool _isLoading;
        private bool _hasError;
        private bool _hasFocus; // 키보드 탐색 활성 여부

        // 클릭 장착 플로우에서 선택된 슬롯.
        private ItemGridSlot _selectedSlot;

        // 현재 장착된 아이템 인스턴스 ID (강조 테두리용).
        private string _equippedCostumeId;
        private string _equippedWeaponId;

        private Coroutine _loadingTimeoutCoroutine;

        // ── Unity 생명주기 ─────────────────────────────────────────────────────

        private void Awake()
        {
            _tooltipPanel?.SetActive(false);
            _loadingPanel?.SetActive(false);
            _errorPanel?.SetActive(false);
        }

        private void OnEnable()
        {
            // 키보드 탐색은 Update에서 처리 (InputSystem 미전환 — MVP).
        }

        private void OnDisable()
        {
            StopAllCoroutines();
            HideTooltip();
        }

        private void Update()
        {
            if (!_hasFocus || _isLoading || _hasError) return;
            HandleKeyboardNavigation();
        }

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 인벤토리 오픈 시 호출. 아이템 목록을 로드하여 그리드를 채운다.
        /// </summary>
        public void LoadInventory(List<ItemData> items, List<Sprite> icons,
            string equippedCostumeId, string equippedWeaponId)
        {
            _equippedCostumeId = equippedCostumeId;
            _equippedWeaponId  = equippedWeaponId;

            _items.Clear();
            _items.AddRange(items);
            _icons.Clear();
            _icons.AddRange(icons);

            if (_loadingTimeoutCoroutine != null) StopCoroutine(_loadingTimeoutCoroutine);
            StartCoroutine(LoadWithTimeout());
        }

        /// <summary>
        /// 즉시 그리드를 채운다 (캐시 히트 등 로딩 불필요 시).
        /// </summary>
        public void PopulateGrid(List<ItemData> items, List<Sprite> icons,
            string equippedCostumeId, string equippedWeaponId)
        {
            _equippedCostumeId = equippedCostumeId;
            _equippedWeaponId  = equippedWeaponId;
            _items.Clear(); _items.AddRange(items);
            _icons.Clear(); _icons.AddRange(icons);
            BuildGrid();
        }

        /// <summary>
        /// 키보드 탐색 포커스를 활성화/비활성화한다.
        /// </summary>
        public void SetFocus(bool hasFocus)
        {
            _hasFocus = hasFocus;
            if (hasFocus && _slots.Count > 0)
                SetFocusedSlot(0);
        }

        // ── 그리드 구축 ────────────────────────────────────────────────────────

        private void BuildGrid()
        {
            // 기존 슬롯 정리.
            foreach (var slot in _slots)
            {
                slot.OnSlotSelected    -= HandleSlotSelected;
                slot.OnSlotHoverEnter  -= HandleSlotHoverEnter;
                slot.OnSlotHoverExit   -= HandleSlotHoverExit;
                slot.OnSlotDropReceived -= HandleSlotDropReceived;
            }
            _slots.Clear();

            // 기존 자식 제거.
            foreach (Transform child in _gridContent)
                Destroy(child.gameObject);

            // 슬롯 수: 최소 DefaultSlotCount, items 수 초과 시 items 수만큼.
            int slotCount = Mathf.Max(DefaultSlotCount, _items.Count);

            for (int i = 0; i < slotCount; i++)
            {
                var slot = Instantiate(_slotPrefab, _gridContent);
                slot.name = $"ItemSlot_{i}";

                if (i < _items.Count && _items[i].IsValid)
                {
                    Sprite icon = i < _icons.Count ? _icons[i] : null;
                    slot.SetItem(_items[i], icon);

                    // 장착 강조.
                    bool isEquipped = _items[i].PrefabKey == _equippedCostumeId
                                   || _items[i].PrefabKey == _equippedWeaponId;
                    slot.SetEquipped(isEquipped);
                }
                else
                {
                    slot.Clear();
                }

                slot.OnSlotSelected     += HandleSlotSelected;
                slot.OnSlotHoverEnter   += HandleSlotHoverEnter;
                slot.OnSlotHoverExit    += HandleSlotHoverExit;
                slot.OnSlotDropReceived += HandleSlotDropReceived;

                _slots.Add(slot);
            }

            _focusedIndex = 0;
        }

        // ── 로딩 타임아웃 ─────────────────────────────────────────────────────

        private IEnumerator LoadWithTimeout()
        {
            _isLoading = true;
            _loadingPanel?.SetActive(true);
            _errorPanel?.SetActive(false);

            // TODO: 실제 LootManager 비동기 로드 API 구현 후 교체.
            // 현재: 아이템이 이미 있으면 즉시 빌드, 없으면 타임아웃.
            if (_items.Count > 0)
            {
                _loadingPanel?.SetActive(false);
                _isLoading = false;
                BuildGrid();
                yield break;
            }

            float elapsed = 0f;
            while (elapsed < LoadTimeoutSec)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;

                // TODO: 데이터 로드 완료 신호 수신 시 break.
                if (_items.Count > 0)
                {
                    _loadingPanel?.SetActive(false);
                    _isLoading = false;
                    BuildGrid();
                    yield break;
                }
            }

            // 2초 타임아웃 → 에러 상태.
            _loadingPanel?.SetActive(false);
            _isLoading = false;
            _hasError  = true;
            _errorPanel?.SetActive(true);
            if (_errorText != null)
                _errorText.text = "인벤토리를 불러올 수 없습니다."; // TODO: 로컬라이제이션 키 교체
        }

        // ── 슬롯 이벤트 핸들러 ────────────────────────────────────────────────

        /// <summary>
        /// 슬롯 클릭 → 클릭 장착 플로우.
        /// 1차 클릭: 선택. 2차 클릭(같은 슬롯): 장착 요청.
        /// </summary>
        private void HandleSlotSelected(ItemGridSlot slot)
        {
            if (_selectedSlot != null && _selectedSlot == slot)
            {
                // 동일 슬롯 재클릭 → 장착 요청.
                RequestEquip(slot);
                return;
            }

            // 이전 선택 해제.
            if (_selectedSlot != null)
                _selectedSlot.SetSelected(false);

            _selectedSlot = slot;
            slot.SetSelected(true);
        }

        private void HandleSlotHoverEnter(ItemGridSlot slot)
        {
            ShowTooltip(slot);
        }

        private void HandleSlotHoverExit(ItemGridSlot slot)
        {
            HideTooltip();
        }

        private void HandleSlotDropReceived(ItemGridSlot source, ItemGridSlot target)
        {
            // 드래그 앤 드롭 secondary 인터랙션: 슬롯 간 아이템 교환.
            int srcIdx = _slots.IndexOf(source);
            int tgtIdx = _slots.IndexOf(target);
            if (srcIdx < 0 || tgtIdx < 0) return;

            // 데이터 교환.
            (_items[srcIdx], _items[tgtIdx]) = (_items[tgtIdx], _items[srcIdx]);
            (_icons[srcIdx], _icons[tgtIdx]) = (_icons[tgtIdx], _icons[srcIdx]);

            // 비주얼 교환.
            Sprite srcSprite = source.ItemData.IsValid ? (_icons.Count > srcIdx ? _icons[srcIdx] : null) : null;
            Sprite tgtSprite = target.ItemData.IsValid ? (_icons.Count > tgtIdx ? _icons[tgtIdx] : null) : null;

            if (_items[srcIdx].IsValid)
                source.SetItem(_items[srcIdx], srcSprite);
            else
                source.Clear();

            if (_items[tgtIdx].IsValid)
                target.SetItem(_items[tgtIdx], tgtSprite);
            else
                target.Clear();
        }

        // ── 장착 요청 ─────────────────────────────────────────────────────────

        private void RequestEquip(ItemGridSlot slot)
        {
            if (slot.IsEmpty) return;

            // TODO: 아이템 타입(복장/무기) 판별 로직 — ItemData에 타입 필드 추가 후 분기.
            // 현재 임시: grade가 0이면 복장, 1+이면 무기로 분류 (placeholder).
            Sprite icon = slot.ItemData.IsValid ? null : null; // TODO: 실제 아이콘 조회

            if (slot.ItemData.Grade == 0)
                OnCostumeEquipRequested?.Invoke(slot, icon);
            else
                OnWeaponEquipRequested?.Invoke(slot, icon);

            // 선택 해제.
            slot.SetSelected(false);
            _selectedSlot = null;
        }

        // ── 툴팁 ──────────────────────────────────────────────────────────────

        private void ShowTooltip(ItemGridSlot slot)
        {
            if (_tooltipPanel == null || slot.IsEmpty) return;

            _tooltipPanel.SetActive(true);

            if (_tooltipNameText != null)
                _tooltipNameText.text = slot.ItemData.PrefabKey; // TODO: 아이템 이름 조회

            if (_tooltipGradeText != null)
                _tooltipGradeText.text = GetGradeLabel(slot.ItemData.Grade);

            if (_tooltipDescText != null)
                _tooltipDescText.text = string.Empty; // TODO: 아이템 스탯/설명 조회
        }

        private void HideTooltip()
        {
            _tooltipPanel?.SetActive(false);
        }

        // ── 키보드 탐색 ────────────────────────────────────────────────────────

        private void HandleKeyboardNavigation()
        {
            if (_slots.Count == 0) return;

            int row = _focusedIndex / Columns;
            int col = _focusedIndex % Columns;

            if (Input.GetKeyDown(KeyCode.RightArrow))
            {
                int newCol = col + 1;
                if (newCol < Columns && _focusedIndex + 1 < _slots.Count)
                    SetFocusedSlot(_focusedIndex + 1);
                // 수평 wrap: 마지막 열 오른쪽 → 다음 행 첫 열 (UX Spec §Gamepad 포커스 순서)
                else if (newCol >= Columns)
                {
                    int nextRowFirst = (row + 1) * Columns;
                    if (nextRowFirst < _slots.Count)
                        SetFocusedSlot(nextRowFirst);
                }
            }
            else if (Input.GetKeyDown(KeyCode.LeftArrow))
            {
                if (col > 0)
                    SetFocusedSlot(_focusedIndex - 1);
                else
                {
                    // 좌측 경계 → 캐릭터 패널로 포커스 이동 요청.
                    _hasFocus = false;
                    OnFocusMoveLeft?.Invoke();
                }
            }
            else if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                int newIdx = _focusedIndex - Columns;
                if (newIdx >= 0) SetFocusedSlot(newIdx);
            }
            else if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                int newIdx = _focusedIndex + Columns;
                if (newIdx < _slots.Count) SetFocusedSlot(newIdx);
                // 최하단 이동 없음 (UX Spec: 수직 끝 행 아래 → 이동 없음).
            }
            else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                if (_focusedIndex < _slots.Count)
                    HandleSlotSelected(_slots[_focusedIndex]);
            }
        }

        private void SetFocusedSlot(int index)
        {
            if (index < 0 || index >= _slots.Count) return;

            // 이전 포커스 해제.
            if (_focusedIndex < _slots.Count)
                _slots[_focusedIndex].SetSelected(false);

            _focusedIndex = index;
            _slots[_focusedIndex].SetSelected(true);

            // 스크롤 뷰 안으로 이동.
            if (_scrollRect != null)
                ScrollToSlot(_slots[_focusedIndex]);
        }

        private void ScrollToSlot(ItemGridSlot slot)
        {
            if (_scrollRect == null || _gridContent == null) return;
            Canvas.ForceUpdateCanvases();
            var slotRect = slot.GetComponent<RectTransform>();
            if (slotRect == null) return;

            Vector2 viewportLocal;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _scrollRect.viewport,
                RectTransformUtility.WorldToScreenPoint(null, slotRect.position),
                null,
                out viewportLocal);

            float normalizedY = 1f - (viewportLocal.y + _scrollRect.viewport.rect.height / 2f)
                              / _scrollRect.content.rect.height;
            _scrollRect.verticalNormalizedPosition = Mathf.Clamp01(1f - normalizedY);
        }

        // ── 유틸리티 ──────────────────────────────────────────────────────────

        private static string GetGradeLabel(int grade)
        {
            return grade switch
            {
                0 => "일반",
                1 => "희귀",
                2 => "영웅",
                _ => string.Empty,
            };
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (_slotPrefab == null)
                Debug.LogWarning("[InventoryGridPanel] _slotPrefab 연결 필요.", this);
            if (_gridContent == null)
                Debug.LogWarning("[InventoryGridPanel] _gridContent 연결 필요.", this);
        }
#endif
    }
}
