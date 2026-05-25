using System;
using Unity.Netcode;

namespace BeTheKing.Foundation
{
    /// <summary>
    /// 호스트 마이그레이션 시 전달되는 세션 상태 스냅샷.
    /// NetworkSerializable을 구현하여 NGO RPC로 직접 전송 가능하다.
    ///
    /// <example>
    /// // 스냅샷 생성 (현재 서버 상태에서)
    /// var snap = MigrationSnapshot.Capture();
    ///
    /// // RPC 전송
    /// RestoreSessionClientRpc(snap);
    ///
    /// // 신규 호스트에서 복원
    /// snap.ApplyToManagers();
    /// </example>
    /// </summary>
    [Serializable]
    public struct MigrationSnapshot : INetworkSerializable
    {
        /// <summary>마이그레이션 시점의 누적 경과 시간(초)</summary>
        public float Elapsed;

        /// <summary>마이그레이션 시점의 현재 일차(1~3)</summary>
        public int CurrentDay;

        /// <summary>마이그레이션 시점의 낮/밤 페이즈</summary>
        public DayPhase Phase;

        /// <summary>대관식 진행 중 여부</summary>
        public bool IsCoronation;

        /// <summary>마이그레이션 시점의 게임 상태</summary>
        public GameState GameState;

        /// <summary>스냅샷 생성 시각 (UTC Unix timestamp, 검증용)</summary>
        public long CapturedAtUtc;

        // ── Factory ────────────────────────────────────────────

        /// <summary>
        /// 현재 실행 중인 매니저들의 상태로 스냅샷을 생성한다.
        /// 서버에서만 호출해야 한다.
        /// </summary>
        /// <returns>현재 세션 상태를 담은 스냅샷</returns>
        public static MigrationSnapshot Capture()
        {
            var time  = SessionTimeManager.Instance;
            var state = GameStateManager.Instance;

            return new MigrationSnapshot
            {
                Elapsed       = time  != null ? time.Elapsed      : 0f,
                CurrentDay    = time  != null ? time.CurrentDay   : 1,
                Phase         = time  != null ? time.Phase        : DayPhase.Day,
                IsCoronation  = time  != null ? time.IsCoronation : false,
                GameState     = state != null ? state.Current     : BeTheKing.Foundation.GameState.InGame,
                CapturedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
        }

        // ── INetworkSerializable ───────────────────────────────

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Elapsed);
            serializer.SerializeValue(ref CurrentDay);
            serializer.SerializeValue(ref Phase);
            serializer.SerializeValue(ref IsCoronation);
            serializer.SerializeValue(ref GameState);
            serializer.SerializeValue(ref CapturedAtUtc);
        }
    }
}
