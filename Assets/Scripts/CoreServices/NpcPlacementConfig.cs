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
        [Tooltip("구역당 일반 NPC 수. 4구역 × 이 값 = 총 일반 NPC 수. GDD 기준: 17~18 → 총 ~70명")]
        [Range(10, 25)]
        public int CivilianPerZone = 17;

        [Tooltip("프리워밍 풀 크기. CivilianPerZone × 4 이상 권장.")]
        [Range(40, 100)]
        public int CivilianPrewarm = 70;

        [Header("Assassin NPC")]
        [Tooltip("구역당 자객 NPC 수. GDD 기준: 2~3 → 총 8~10마리")]
        [Range(1, 5)]
        public int AssassinPerZone = 2;

        [Tooltip("프리워밍 풀 크기. AssassinPerZone × 4 이상 권장.")]
        [Range(4, 20)]
        public int AssassinPrewarm = 10;

        [Header("Job Copy")]
        [Tooltip("일반 NPC에 플레이어 직업 복장을 카피할 인원 수. GDD 기준: 3~4명")]
        [Range(3, 4)]
        public int JobCopyCount = 3;
    }
}
