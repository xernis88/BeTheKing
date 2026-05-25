using System;
using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.Foundation
{
    public enum GameState : byte
    {
        Lobby,
        Loading,
        InGame,
        GameOver
    }

    /// <summary>
    /// 세션 전체 상태 기계(FSM). 서버가 권위를 가지며 모든 클라이언트에 동기화된다.
    /// </summary>
    public class GameStateManager : NetworkBehaviour
    {
        public static GameStateManager Instance { get; private set; }

        private readonly NetworkVariable<GameState> _state = new(
            GameState.Lobby,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        public GameState Current => _state.Value;

        /// <summary>(이전 상태, 새 상태) — 서버/클라이언트 모두에서 발행</summary>
        public event Action<GameState, GameState> OnStateChanged;

        void Awake()
        {
            if (Instance != null)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        public override void OnNetworkSpawn()
        {
            _state.OnValueChanged += (prev, next) => OnStateChanged?.Invoke(prev, next);
        }

        public override void OnDestroy()
        {
            base.OnDestroy();
            if (Instance == this) Instance = null;
        }

        // ── 서버 전용 ──────────────────────────────────────────

        /// <summary>서버에서만 호출. 유효하지 않은 전환은 무시된다.</summary>
        public void TransitionTo(GameState next)
        {
            if (!IsServer) return;
            if (_state.Value == next) return;
            if (!IsValidTransition(_state.Value, next))
            {
                Debug.LogWarning($"[GameState] 유효하지 않은 전환: {_state.Value} → {next}");
                return;
            }
            _state.Value = next;
        }

        private static bool IsValidTransition(GameState from, GameState to) => (from, to) switch
        {
            (GameState.Lobby,    GameState.Loading)  => true,
            (GameState.Loading,  GameState.InGame)   => true,
            (GameState.InGame,   GameState.GameOver) => true,
            (GameState.GameOver, GameState.Lobby)    => true,
            _ => false
        };
    }
}
