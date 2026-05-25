// Implements: design/gdd/01-core-services.md — LootManager §7 Tuning Knobs
// Story: production/epics/epic-core-services/story-004-loot-drop.md

using UnityEngine;

namespace BeTheKing.CoreServices
{
    /// <summary>
    /// LootManager 동작을 제어하는 모든 수치를 보관하는 ScriptableObject.
    /// 디자이너가 코드 수정 없이 조정할 수 있다.
    /// </summary>
    [CreateAssetMenu(fileName = "LootConfig", menuName = "BeTheKing/Config/LootConfig")]
    public class LootConfig : ScriptableObject
    {
        [Header("드롭 오프셋")]
        [Tooltip("겹침 방지를 위한 scatter 반경 (유닛). AC-2 요구사항.")]
        [Range(0.5f, 5f)]
        public float ScatterRadius = 1.2f;

        [Tooltip("scatter 위치 탐색 최대 시도 횟수. 초과 시 마지막 후보 위치를 사용한다.")]
        [Range(5, 30)]
        public int ScatterMaxAttempts = 10;

        [Header("경계 보정")]
        [Tooltip("구역 경계에서 안쪽으로 보정할 최소 여백 (유닛). AC-3 요구사항.")]
        [Range(0.1f, 2f)]
        public float ZoneBoundaryInset = 0.5f;

        [Header("월드 상자")]
        [Tooltip("상자에 배치할 기본 아이템 풀. LootManager가 랜덤 선택한다.")]
        public ItemData[] ChestItemPool;
    }
}
