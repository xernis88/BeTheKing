// Implements: design/gdd/02-gameplay-systems.md — JobInteractionSystem
// Story: production/epics/epic-gameplay-systems/story-005-job-interaction.md
// ADR: docs/architecture/ADR-013-job-interaction-skill-check.md
//
// 설계 결정:
//   스킬체크 세션 상태를 서버 전용 Dictionary<ulong, SkillCheckState>로 관리.
//   Random.Range로 매 세션마다 목표 구간을 랜덤 결정. 성공 판정은 서버에서만 수행.
//   성공 시 CurrencySystem.Award 호출, 실패 시 SuspicionSystem.Report 호출.
//   UI 게이지 시작은 BeginSkillCheckClientRpc stub — epic-ui-presentation에서 구현.
//   PlayerManager는 MVP stub — null-safe, 항상 직업 일치로 처리.
//
// 스펙 편차: 스토리 파일 스펙의 CurrencySystem.AddGold는 실제 API에 존재하지 않음.
//   실제 CurrencySystem 구현(ADR-006)의 Award(ulong, int)를 사용한다.

using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.GameplaySystems
{
    /// <summary>직업 유형. MVP: Merchant만 구현.</summary>
    public enum JobType { Merchant, Guard, Farmer }

    /// <summary>
    /// 직업 상호작용 대상 오브젝트에 부착되는 컴포넌트.
    /// 해당 오브젝트와 상호작용하기 위해 플레이어에게 요구되는 직업 유형을 정의한다.
    /// </summary>
    public class JobInteractable : MonoBehaviour
    {
        /// <summary>이 오브젝트 상호작용에 요구되는 직업 유형.</summary>
        [SerializeField] public JobType RequiredJob;
    }

    /// <summary>스킬체크 세션의 서버 전용 상태.</summary>
    internal class SkillCheckState
    {
        /// <summary>목표 구간 시작 각도(도). Random.Range(0, 360 - SuccessZoneSize)로 결정.</summary>
        public float TargetZoneStart;

        /// <summary>목표 구간 크기(도). Inspector _successZoneSize에서 복사.</summary>
        public float SuccessZoneSize;

        /// <summary>세션 활성 여부. false이면 SubmitInput 무시.</summary>
        public bool IsActive;
    }

    /// <summary>
    /// 직업 상호작용 미니게임(스킬체크)을 서버 권위적으로 관리한다.
    /// <para>
    ///   - <see cref="BeginSkillCheck"/>: 서버에서 스킬체크 세션을 시작한다.
    ///   - <see cref="SubmitInputServerRpc"/>: 클라이언트가 입력한 각도를 서버로 전송하여 성공/실패를 판정한다.
    ///   - <see cref="CancelSkillCheck"/>: 서버에서 진행 중인 세션을 취소한다.
    /// </para>
    /// <para>
    ///   ADR-013: 성공 판정은 서버에서만 수행. 목표 구간은 매 세션 랜덤 결정.
    ///   성공 시 CurrencySystem.Award, 실패 시 SuspicionSystem.Report 호출.
    ///   UI 게이지(BeginSkillCheckClientRpc)는 epic-ui-presentation에서 구현 예정.
    /// </para>
    /// </summary>
    public class JobInteractionSystem : NetworkBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────

        /// <summary>씬 내 단일 인스턴스. OnNetworkSpawn/OnNetworkDespawn에서 관리된다.</summary>
        public static JobInteractionSystem Instance { get; private set; }

        // ── Inspector (밸런스 튜닝 가능) ────────────────────────────────────────

        [Header("Skill Check")]
        [Tooltip("게이지 회전 속도(도/초). 클라이언트 UI에 전달된다. 기본값 90.")]
        [SerializeField] private float _gaugeRotationSpeed = 90f;

        [Tooltip("성공 구간 크기(도). 범위 0~360. 기본값 40.")]
        [SerializeField] private float _successZoneSize = 40f;

        [Tooltip("스킬체크 성공 시 지급할 금화량. 기본값 10.")]
        [SerializeField] private int _rewardAmount = 10;

        // ── Events (클라이언트에서 구독 가능) ───────────────────────────────────

        /// <summary>
        /// 로컬 클라이언트의 스킬체크가 시작될 때 발행된다. (zoneStart, zoneEnd 각도)
        /// InteractionUI에서 구독하여 원형 게이지 팝업을 표시한다.
        /// </summary>
        public static event Action<float, float> OnSkillCheckBegin;

        /// <summary>
        /// 로컬 클라이언트의 스킬체크 결과가 도달할 때 발행된다. (success)
        /// InteractionUI에서 구독하여 성공/실패 피드백을 표시한다.
        /// </summary>
        public static event Action<bool> OnSkillCheckEnd;

        /// <summary>OnSkillCheckBegin 이벤트를 발행한다. unit test에서 직접 호출 가능.</summary>
        internal static void RaiseOnSkillCheckBegin(float zoneStart, float zoneEnd)
            => OnSkillCheckBegin?.Invoke(zoneStart, zoneEnd);

        /// <summary>OnSkillCheckEnd 이벤트를 발행한다. unit test에서 직접 호출 가능.</summary>
        internal static void RaiseOnSkillCheckEnd(bool success)
            => OnSkillCheckEnd?.Invoke(success);

        // ── 서버 전용 상태 ─────────────────────────────────────────────────────

        // ADR-013: 서버 전용 세션 딕셔너리 — 클라이언트에서 접근 불가.
        private readonly Dictionary<ulong, SkillCheckState> _activeSessions = new();

        // ── Lifecycle ──────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public override void OnNetworkSpawn()
        {
            Instance = this;
        }

        /// <inheritdoc/>
        public override void OnNetworkDespawn()
        {
            if (Instance == this)
                Instance = null;
        }

        // ── 공개 API ──────────────────────────────────────────────────────────

        /// <summary>
        /// 지정 클라이언트에 대해 스킬체크 세션을 시작한다. 서버에서만 유효.
        /// <para>
        ///   ADR-013: 목표 구간은 Random.Range(0, 360 - _successZoneSize)로 랜덤 결정.
        ///   PlayerManager stub — null이거나 불일치 시 return (MVP: 항상 일치 처리).
        /// </para>
        /// </summary>
        /// <param name="clientId">스킬체크를 수행할 플레이어의 ClientId.</param>
        /// <param name="target">상호작용 대상 오브젝트. 직업 일치 확인에 사용.</param>
        public void BeginSkillCheck(ulong clientId, JobInteractable target)
        {
            if (!IsServer) return;

            // MVP stub: PlayerManager가 없으면 항상 직업 일치로 간주.
            // TODO: PlayerManager.Instance?.GetJob(clientId) 직업 일치 확인 (PlayerManager 구현 후)

            float targetZoneStart = UnityEngine.Random.Range(0f, 360f - _successZoneSize);
            _activeSessions[clientId] = new SkillCheckState
            {
                TargetZoneStart = targetZoneStart,
                SuccessZoneSize = _successZoneSize,
                IsActive = true
            };

            BeginSkillCheckClientRpc(clientId, targetZoneStart, _successZoneSize, _gaugeRotationSpeed);
        }

        /// <summary>
        /// 클라이언트가 입력한 각도를 서버로 전송하여 성공/실패를 판정한다.
        /// <para>
        ///   성공: currentAngle이 [TargetZoneStart, TargetZoneStart + SuccessZoneSize] 구간 안에 있을 때.
        ///   성공 시 CurrencySystem.Award 호출. 실패 시 SuspicionSystem.Report 호출.
        ///   판정 후 세션은 비활성화된다.
        /// </para>
        /// <para>
        ///   보안: clientId는 rpcParams.Receive.SenderClientId에서 추출한다.
        ///   클라이언트가 파라미터로 위조한 clientId를 신뢰하지 않는다.
        /// </para>
        /// </summary>
        /// <param name="currentAngle">클라이언트가 제출하는 현재 게이지 각도(도).</param>
        /// <param name="rpcParams">NGO가 주입하는 RPC 메타데이터. SenderClientId 추출에 사용.</param>
        [ServerRpc(RequireOwnership = false)]
        public void SubmitInputServerRpc(float currentAngle, ServerRpcParams rpcParams = default)
        {
            ulong clientId = rpcParams.Receive.SenderClientId;
            if (!_activeSessions.TryGetValue(clientId, out var state) || !state.IsActive)
                return;

            bool isSuccess = currentAngle >= state.TargetZoneStart &&
                             currentAngle <= state.TargetZoneStart + state.SuccessZoneSize;

            if (isSuccess)
            {
                // 스펙 편차: 스토리의 AddGold 대신 CurrencySystem.Award 사용 (실제 API).
                CurrencySystem.Instance?.Award(clientId, _rewardAmount);
            }
            else
            {
                // 실패 시 수상행동 보고. 위치는 Out of Scope이므로 Vector3.zero 사용.
                SuspicionSystem.Instance?.Report(clientId, Vector3.zero);
            }

            state.IsActive = false;
            NotifySkillCheckResultClientRpc(clientId, isSuccess);
        }

        /// <summary>
        /// 진행 중인 스킬체크 세션을 취소한다. 서버에서만 유효.
        /// 취소 시 금화 지급 및 수상행동 보고 없이 세션을 종료한다.
        /// </summary>
        /// <param name="clientId">취소할 세션의 클라이언트 ClientId.</param>
        public void CancelSkillCheck(ulong clientId)
        {
            if (!IsServer) return;

            if (_activeSessions.TryGetValue(clientId, out var state))
                state.IsActive = false;
        }

        // ── ClientRpc ─────────────────────────────────────────────────────────

        /// <summary>
        /// 스킬체크 세션 시작을 클라이언트에 알린다.
        /// UI 게이지 시작 구현은 epic-ui-presentation에서 담당한다.
        /// </summary>
        /// <param name="clientId">세션 대상 클라이언트 ClientId.</param>
        /// <param name="zoneStart">목표 구간 시작 각도(도).</param>
        /// <param name="zoneSize">목표 구간 크기(도).</param>
        /// <param name="speed">게이지 회전 속도(도/초).</param>
        [ClientRpc]
        private void BeginSkillCheckClientRpc(ulong clientId, float zoneStart, float zoneSize, float speed)
        {
            // 로컬 클라이언트 대상 이벤트만 발행 — 타 클라이언트 스킬체크는 표시하지 않는다.
            if (NetworkManager.Singleton == null ||
                NetworkManager.Singleton.LocalClientId != clientId) return;

            RaiseOnSkillCheckBegin(zoneStart, zoneStart + zoneSize);
        }

        /// <summary>스킬체크 결과를 해당 클라이언트에만 알린다.</summary>
        [ClientRpc]
        private void NotifySkillCheckResultClientRpc(ulong clientId, bool success)
        {
            if (NetworkManager.Singleton == null ||
                NetworkManager.Singleton.LocalClientId != clientId) return;

            RaiseOnSkillCheckEnd(success);
        }
    }
}
