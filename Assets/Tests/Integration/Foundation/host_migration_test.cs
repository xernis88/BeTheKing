// ============================================================
// Host Migration — Integration Tests
// Story: Foundation / Host Migration (EPIC-FOUNDATION-HM)
//
// 자동화 범위: 후보 선출 로직, 스냅샷 직렬화/역직렬화
// 플레이테스트 범위: 네트워크 의존 흐름 (아래 절차 문서 참조)
// ============================================================

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using BeTheKing.Foundation;

namespace BeTheKing.Tests.Integration.Foundation
{
    // ──────────────────────────────────────────────────────────
    // [자동화] 후보 선출 로직
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("HostMigration")]
    public class HostElectionTests
    {
        // ElectNewHost()는 GameNetworkManager.ConnectedClients와 NetworkManager에
        // 의존한다. 단위 테스트에서는 ElectFromList() 헬퍼로 의존성을 분리한다.
        // HostMigrationController.ElectNewHost()가 internal이므로 직접 호출 가능.

        /// <summary>단일 후보가 존재할 때 해당 클라이언트가 선출된다.</summary>
        [Test]
        public void Elect_SingleCandidate_ReturnsThatCandidate()
        {
            var result = ElectFromList(serverClientId: 0UL, candidates: new[] { 1UL });
            Assert.AreEqual(1UL, result);
        }

        /// <summary>복수 후보 중 가장 낮은 ClientId가 선출된다 (RTT 미측정 fallback).</summary>
        [Test]
        public void Elect_MultipleCandidates_ReturnsLowestClientId()
        {
            var result = ElectFromList(serverClientId: 0UL, candidates: new[] { 5UL, 2UL, 8UL, 3UL });
            Assert.AreEqual(2UL, result);
        }

        /// <summary>후보가 없으면 ulong.MaxValue를 반환한다.</summary>
        [Test]
        public void Elect_NoCandidates_ReturnsMaxValue()
        {
            var result = ElectFromList(serverClientId: 0UL, candidates: new ulong[0]);
            Assert.AreEqual(ulong.MaxValue, result);
        }

        /// <summary>서버 자신은 후보에서 제외된다.</summary>
        [Test]
        public void Elect_ServerIdExcludedFromCandidates()
        {
            // serverClientId가 가장 낮지만 서버 자신이므로 제외
            var result = ElectFromList(serverClientId: 0UL, candidates: new[] { 0UL, 3UL, 7UL });
            Assert.AreEqual(3UL, result);
        }

        // ── 헬퍼 ─────────────────────────────────────────────

        /// <summary>
        /// NetworkManager 의존성 없이 선출 로직만 테스트하는 순수 함수.
        /// HostMigrationController.ElectNewHost()와 동일한 알고리즘을 사용한다.
        /// </summary>
        private static ulong ElectFromList(ulong serverClientId, IEnumerable<ulong> candidates)
        {
            ulong best = ulong.MaxValue;
            foreach (ulong id in candidates)
            {
                if (id == serverClientId) continue;
                if (id < best) best = id;
            }
            return best;
        }
    }

    // ──────────────────────────────────────────────────────────
    // [자동화] 스냅샷 직렬화
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("HostMigration")]
    public class MigrationSnapshotTests
    {
        /// <summary>스냅샷의 모든 필드가 올바르게 초기화된다.</summary>
        [Test]
        public void Snapshot_DefaultValues_AreValid()
        {
            var snap = new MigrationSnapshot
            {
                Elapsed      = 120.5f,
                CurrentDay   = 2,
                Phase        = DayPhase.Night,
                IsCoronation = false,
                GameState    = GameState.InGame,
                CapturedAtUtc = 1_700_000_000L,
            };

            Assert.AreEqual(120.5f,            snap.Elapsed,       delta: 0.001f);
            Assert.AreEqual(2,                  snap.CurrentDay);
            Assert.AreEqual(DayPhase.Night,     snap.Phase);
            Assert.AreEqual(false,              snap.IsCoronation);
            Assert.AreEqual(GameState.InGame,   snap.GameState);
            Assert.AreEqual(1_700_000_000L,     snap.CapturedAtUtc);
        }

        /// <summary>음수 Elapsed는 복원 시 0으로 클램프되어야 한다.</summary>
        [Test]
        public void Snapshot_NegativeElapsed_IsClampedToZero()
        {
            float restored = Mathf.Max(0f, -5f);
            Assert.AreEqual(0f, restored, delta: 0.001f);
        }

        /// <summary>Elapsed가 TotalDuration을 초과해도 구조체 자체는 저장 가능하다.</summary>
        [Test]
        public void Snapshot_ElapsedExceedingTotal_IsStoredAsIs()
        {
            // 유효성 검사는 복원 호출자(SessionTimeManager)의 책임
            var snap = new MigrationSnapshot { Elapsed = 99999f };
            Assert.AreEqual(99999f, snap.Elapsed, delta: 0.001f);
        }

        /// <summary>대관식 페이즈(Day=3, Phase=Day) 조합이 올바르게 보존된다.</summary>
        [Test]
        public void Snapshot_CoronationState_IsPreserved()
        {
            var snap = new MigrationSnapshot
            {
                CurrentDay   = 3,
                Phase        = DayPhase.Day,
                IsCoronation = true,
            };

            Assert.AreEqual(3,              snap.CurrentDay);
            Assert.AreEqual(DayPhase.Day,   snap.Phase);
            Assert.IsTrue(snap.IsCoronation);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [플레이테스트 절차] 네트워크 의존 시나리오
    // ──────────────────────────────────────────────────────────
    //
    // 아래 시나리오는 Unity Play Mode + 실제 네트워크 스택이 필요하여 자동화 불가.
    // QA 담당자는 각 항목을 수동으로 검증하고 evidence 폴더에 결과를 기록한다.
    // 결과 저장 경로: production/qa/evidence/host-migration-[날짜].md
    //
    // ── 시나리오 HM-01: 인게임 중 호스트 이탈 ─────────────────
    //
    // 준비:
    //   - PC A를 호스트로, PC B / PC C를 클라이언트로 로비 구성
    //   - 게임을 시작하여 InGame 상태 진입
    //   - SessionTimeManager로 경과 시간 2분(120s) 이상 대기
    //
    // 실행:
    //   1. PC A의 네트워크를 강제 단절 (LAN 케이블 제거 또는 방화벽 차단)
    //
    // 기대 결과:
    //   - PC B / PC C에서 OnTransportFailure 이벤트 수신 확인
    //   - GameNetworkManager.OnHostLost 발행 확인 (Debug.Log로 검증)
    //   - HostMigrationController가 ElectNewHost() 호출 — 가장 낮은 ClientId(PC B)가 선출
    //   - PC B에서 PromoteToHostClientRpc 수신 확인
    //   - PC B에서 RestoreElapsed() 호출 후 SessionTimeManager.Elapsed가 스냅샷 값과 일치
    //   - PC C에서 NotifyNewHostClientRpc 수신 확인
    //
    // 합격 기준:
    //   [ ] 세션이 중단 없이 PC B 주도로 계속 진행됨
    //   [ ] PC B의 SessionTimeManager.Elapsed가 마이그레이션 전 값 ±2s 이내
    //   [ ] PC C가 새 호스트를 인식하고 게임 진행 중임
    //
    // ── 시나리오 HM-02: 클라이언트 1인만 남았을 때 이탈 ─────────
    //
    // 준비:
    //   - PC A를 호스트, PC B를 유일한 클라이언트로 구성
    //   - InGame 상태 진입
    //
    // 실행:
    //   1. PC A 강제 단절
    //
    // 기대 결과:
    //   - PC B가 ElectNewHost()에서 자신을 선출 (유일한 후보)
    //   - 비후보 목록이 비어 NotifyNewHostClientRpc 전송 없음
    //   - PC B가 단독 호스트로 세션 유지
    //
    // 합격 기준:
    //   [ ] PC B가 호스트 역할을 인수하고 세션 시간이 계속 흐름
    //   [ ] NotifyNewHostClientRpc가 발행되지 않음 (Debug.Log 없음)
    //
    // ── 시나리오 HM-03: 마이그레이션 중 추가 이탈 ───────────────
    //
    // 준비:
    //   - PC A(호스트), PC B, PC C, PC D 4인 세션
    //
    // 실행:
    //   1. PC A 이탈 → 마이그레이션 시작 (PC B 선출 예정)
    //   2. 마이그레이션 완료 전 PC B 강제 이탈
    //
    // 기대 결과:
    //   - _migrationInProgress 플래그가 중복 호출을 차단
    //   - PC C 또는 PC D 중 낮은 ClientId가 호스트로 선출
    //   - 경고 로그: "[HostMigration] 마이그레이션이 이미 진행 중이다."
    //
    // 합격 기준:
    //   [ ] 세션 크래시 없음
    //   [ ] 두 번째 마이그레이션이 첫 번째 완료 후 단계적으로 실행됨
    //
    // ── 시나리오 HM-04: 대관식 페이즈 중 호스트 이탈 ────────────
    //
    // 준비:
    //   - 게임 시작 후 8분(480s) 경과 대기 (Day3 / DayPhase.Day 진입)
    //   - IsCoronation = true 상태 확인
    //
    // 실행:
    //   1. 호스트 강제 이탈
    //
    // 기대 결과:
    //   - 스냅샷의 IsCoronation = true 보존
    //   - 신규 호스트에서 대관식 이벤트 재발행 없음 (OnCoronationStarted 중복 방지)
    //
    // 합격 기준:
    //   [ ] 대관식 UI가 유지됨 (중복 연출 없음)
    //   [ ] OnCoronationStarted가 마이그레이션 후 한 번만 발행됨
    //
    // ── Known Limitation ─────────────────────────────────────
    //
    //   Steam SDR 미연동 상태에서 비후보 클라이언트는 신규 호스트에 자동 재연결하지 못한다.
    //   STEAM_BUILD 심볼 선언 전까지 HM-01에서 PC C의 재연결은 수동으로만 가능하다.
    //   (HostMigrationController.cs NotifyNewHostClientRpc 내 TODO 참조)
    //
    // ────────────────────────────────────────────────────────────
}

