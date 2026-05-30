// Implements: design/gdd/01-core-services.md — NPCManager Tuning Knobs
// Story: production/epics/epic-core-services/story-003-npc-pool-placement.md
// Data-driven: 모든 배치 수치는 이 ScriptableObject에서만 변경한다.

using UnityEngine;

namespace BeTheKing.CoreServices
{
    /// <summary>
    /// NPCManager 배치 수치 설정.
    /// Inspector 또는 assets/data/NpcPlacementConfig.asset에서 튜닝한다.
    /// 디자이너가 코드 수정 없이 NPC 수량·풀 크기를 조정할 수 있다.
    /// </summary>
    [CreateAssetMenu(fileName = "NpcPlacementConfig", menuName = "BeTheKing/Config/NpcPlacementConfig")]
    public sealed class NpcPlacementConfig : ScriptableObject
    {
        [Header("Civilian NPC")]
        [Tooltip("구역당 일반 NPC 수. MVP=1(총 4), 풀비전=17(총 ~70). ADR-003: NPC≥15 시 NpcUpdateScheduler 필수.")]
        [Range(1, 25)]
        public int CivilianPerZone = 1;

        [Tooltip("프리워밍 풀 크기. CivilianPerZone × 4 이상 권장.")]
        [Range(1, 100)]
        public int CivilianPrewarm = 4;

        [Header("Assassin NPC")]
        [Tooltip("구역당 자객 NPC 수. MVP=1(총 4), 풀비전=2~3(총 8~10).")]
        [Range(1, 5)]
        public int AssassinPerZone = 1;

        [Tooltip("프리워밍 풀 크기. AssassinPerZone × 4 이상 권장.")]
        [Range(1, 20)]
        public int AssassinPrewarm = 4;

        [Header("Job Copy")]
        [Tooltip("일반 NPC에 플레이어 직업 복장을 카피할 인원 수. GDD 기준: 3~4명")]
        [Range(3, 4)]
        public int JobCopyCount = 3;
    }
}
