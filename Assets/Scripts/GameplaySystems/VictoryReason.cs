// Implements: design/gdd/04-victory-endgame.md — 승리 조건 열거형
// Story: production/epics/epic-victory-endgame/story-002-throne-gauge.md
// Requirement: TR-VICT-003, TR-VICT-004, TR-VICT-006

namespace BeTheKing.GameplaySystems
{
    /// <summary>
    /// 게임 승리 조건을 나타내는 열거형.
    /// VictoryManager(VE-003)와 RoyalGaugeSystem(VE-002)에서 사용된다.
    /// </summary>
    public enum VictoryReason
    {
        /// <summary>왕좌 게이지 120 도달로 인한 승리.</summary>
        GaugeFull,

        /// <summary>제한 시간 종료 시 리더보드 기반 승리.</summary>
        TimeUp,

        /// <summary>마지막 생존자로 인한 승리.</summary>
        LastSurvivor,
    }
}
