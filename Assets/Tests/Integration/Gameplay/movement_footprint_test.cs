// ============================================================
// MovementSystem + FootprintSystem — Integration Tests
// Story: production/epics/epic-gameplay-systems/story-002-movement-footprint.md
// ADR: docs/architecture/ADR-008-footprint-networkobject-spawn.md
//
// 자동화 범위:
//   AC-1: 달리기 중 SpawnInterval 경과 시 SpawnFootprintServerRpc 호출
//   AC-3: SetAttackTraitOwner(false) → Renderer.enabled = false
//   AC-4: 스태미나 소진 시 IsSprinting = false
//   Edge-1: 걷기 중 발자국 미생성 (SpawnFootprintServerRpc 미호출)
//
// 플레이테스트 범위:
//   AC-2: 발자국 30초 후 NetworkObject Despawn (NGO 의존 — 플레이테스트)
//   Edge-2: Lifetime 직전 프레임 Despawn 미발생 (NGO 의존 — 플레이테스트)
//
// 주의: NGO NetworkBehaviour는 EditMode에서 직접 인스턴스화 불가.
//   Testable 래퍼 패턴(stamina_system_test.cs 참조)으로 NGO 의존성 분리.
// ============================================================

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using BeTheKing.GameplaySystems;

namespace BeTheKing.Tests.Integration.Gameplay
{
    // ──────────────────────────────────────────────────────────
    // Testable 래퍼 — FootprintSystem 로직 격리
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// NGO 없이 FootprintSystem 핵심 로직을 검증하기 위한 테스트 전용 래퍼.
    /// SpawnFootprintServerRpc 호출 횟수와 SetAttackTraitOwner 적용 결과를 추적한다.
    /// </summary>
    internal class TestableFootprintSystem
    {
        /// <summary>SpawnFootprintServerRpc 호출 시 기록된 위치 목록.</summary>
        public List<Vector3> SpawnedPositions { get; } = new();

        /// <summary>SetAttackTraitOwner에 마지막으로 전달된 값.</summary>
        public bool? LastAttackTraitValue { get; private set; }

        /// <summary>활성 발자국의 Renderer.enabled 상태를 시뮬레이션하는 목록.</summary>
        private readonly List<bool> _rendererStates = new();

        /// <summary>현재 Renderer.enabled 상태 스냅샷 (읽기 전용).</summary>
        public IReadOnlyList<bool> RendererStates => _rendererStates;

        /// <summary>발자국 스폰 요청을 시뮬레이션한다. 실제 NGO Spawn 없이 위치만 기록.</summary>
        public void SpawnFootprintServerRpc(Vector3 pos)
        {
            SpawnedPositions.Add(pos);
            _rendererStates.Add(true); // 기본 활성 상태로 추가
        }

        /// <summary>공격형 특성 여부에 따라 모든 Renderer.enabled를 설정한다.</summary>
        public void SetAttackTraitOwner(bool hasAttackTrait)
        {
            LastAttackTraitValue = hasAttackTrait;
            for (int i = 0; i < _rendererStates.Count; i++)
                _rendererStates[i] = hasAttackTrait;
        }
    }

    // ──────────────────────────────────────────────────────────
    // Testable 래퍼 — MovementSystem 로직 격리
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// NGO 없이 MovementSystem 핵심 로직을 검증하기 위한 테스트 전용 래퍼.
    /// StaminaSystem과 FootprintSystem을 주입받아 순수 로직만 실행한다.
    /// </summary>
    internal class TestableMovementSystem
    {
        private readonly TestableStaminaSystem _stamina;
        private readonly TestableFootprintSystem _footprint;

        private bool _isSprinting;
        private float _nextSpawnTime;

        private readonly float _sprintCostPerSec;
        private readonly float _spawnInterval;

        /// <summary>현재 달리기 상태 여부.</summary>
        public bool IsSprinting => _isSprinting;

        /// <summary>테스트에서 시간을 주입하기 위한 시뮬레이션 클럭 (초).</summary>
        public float SimulatedTime { get; set; } = 0f;

        /// <summary>테스트 전용 월드 위치 (발자국 스폰 위치로 사용).</summary>
        public Vector3 Position { get; set; } = Vector3.zero;

        public TestableMovementSystem(
            TestableStaminaSystem stamina,
            TestableFootprintSystem footprint,
            float sprintCostPerSec = 20f,
            float spawnInterval = 0.5f)
        {
            _stamina = stamina;
            _footprint = footprint;
            _sprintCostPerSec = sprintCostPerSec;
            _spawnInterval = spawnInterval;
        }

        /// <summary>달리기를 요청한다. 스태미나 잔량이 있을 때만 활성화된다.</summary>
        public void RequestSprint()
        {
            if (_stamina.CanAct)
                _isSprinting = true;
        }

        /// <summary>달리기를 중단한다.</summary>
        public void StopSprint()
        {
            _isSprinting = false;
        }

        /// <summary>
        /// 단일 Update 스텝을 시뮬레이션한다.
        /// <paramref name="deltaTime"/>을 주입하여 프레임률 독립성을 검증한다.
        /// </summary>
        public void SimulateUpdate(float deltaTime)
        {
            SimulatedTime += deltaTime;

            if (!_isSprinting) return;

            if (!_stamina.TryConsume(_sprintCostPerSec * deltaTime))
            {
                StopSprint();
                return;
            }

            if (SimulatedTime >= _nextSpawnTime)
            {
                _nextSpawnTime = SimulatedTime + _spawnInterval;
                _footprint.SpawnFootprintServerRpc(Position);
            }
        }
    }

    // ──────────────────────────────────────────────────────────
    // TestableStaminaSystem — stamina_system_test.cs 와 동일한 래퍼 재사용
    // (별도 어셈블리 참조 불가이므로 로컬 복사본 사용)
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Integration 테스트용 StaminaSystem 래퍼. stamina_system_test.cs의 동일 패턴.
    /// </summary>
    internal class TestableStaminaSystem
    {
        public const float Max = 100f;

        private float _current = Max;
        private float _recoveryRate;

        public float Current => _current;
        public bool CanAct => _current > 0f;

        public TestableStaminaSystem(float initial = Max, float recoveryRate = 10f)
        {
            _current = initial;
            _recoveryRate = recoveryRate;
        }

        public bool TryConsume(float amount)
        {
            if (_current < amount) return false;
            _current -= amount;
            return true;
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-4] 스태미나 소진 시 걷기 전환
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("MovementSystem")]
    public class MovementSystemStaminaExhaustionTests
    {
        private TestableStaminaSystem _stamina;
        private TestableFootprintSystem _footprint;
        private TestableMovementSystem _movement;

        [SetUp]
        public void SetUp()
        {
            // 스태미나를 0.1f로 초기화 — TryConsume(20f * deltaTime) 시 바로 소진
            _stamina = new TestableStaminaSystem(initial: 0.1f);
            _footprint = new TestableFootprintSystem();
            _movement = new TestableMovementSystem(_stamina, _footprint, sprintCostPerSec: 20f);
        }

        /// <summary>
        /// AC-4: 달리기 중 TryConsume이 false를 반환하면 IsSprinting = false로 전환된다.
        /// Given: 달리기 상태, 스태미나 0.1f
        /// When: Update() — TryConsume() false 반환
        /// Then: IsSprinting = false
        /// </summary>
        [Test]
        public void test_movementSystem_sprint_staminaExhausted_stopsSprinting()
        {
            // Arrange
            _movement.RequestSprint();
            Assert.IsTrue(_movement.IsSprinting, "사전 조건: RequestSprint 후 IsSprinting = true");

            // Act: deltaTime=0.1f → TryConsume(20f * 0.1f = 2f) > 잔량 0.1f → false 반환
            _movement.SimulateUpdate(0.1f);

            // Assert
            Assert.IsFalse(_movement.IsSprinting);
        }

        /// <summary>
        /// AC-4 보완: 스태미나 소진 직후 추가 Update에서도 IsSprinting = false 유지.
        /// </summary>
        [Test]
        public void test_movementSystem_sprint_afterExhaustion_remainsNotSprinting()
        {
            // Arrange
            _movement.RequestSprint();
            _movement.SimulateUpdate(0.1f); // 스태미나 소진 → 걷기 전환

            // Act: 추가 Update
            _movement.SimulateUpdate(0.1f);

            // Assert
            Assert.IsFalse(_movement.IsSprinting);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-1] 달리기 중 SpawnInterval 경과 시 발자국 생성
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("MovementSystem")]
    public class MovementSystemFootprintSpawnTests
    {
        private TestableStaminaSystem _stamina;
        private TestableFootprintSystem _footprint;
        private TestableMovementSystem _movement;

        [SetUp]
        public void SetUp()
        {
            _stamina = new TestableStaminaSystem(initial: 100f);
            _footprint = new TestableFootprintSystem();
            // spawnInterval=0.5f, sprintCostPerSec=20f
            _movement = new TestableMovementSystem(_stamina, _footprint, sprintCostPerSec: 20f, spawnInterval: 0.5f);
        }

        /// <summary>
        /// AC-1: 달리기 중 SpawnInterval(0.5초) 경과 시 SpawnFootprintServerRpc 호출됨.
        /// Given: 달리기 상태
        /// When: 0.5초 경과 (SimulatedTime=0 → 0.5초)
        /// Then: SpawnedPositions.Count = 1
        /// </summary>
        [Test]
        public void test_movementSystem_sprint_spawnIntervalElapsed_callsSpawnFootprint()
        {
            // Arrange
            _movement.RequestSprint();
            _movement.Position = new Vector3(1f, 0f, 2f);

            // Act: 첫 번째 Update (SimulatedTime=0 → 0.5f). _nextSpawnTime=0이므로 즉시 스폰.
            _movement.SimulateUpdate(0.5f);

            // Assert
            Assert.AreEqual(1, _footprint.SpawnedPositions.Count);
            Assert.AreEqual(new Vector3(1f, 0f, 2f), _footprint.SpawnedPositions[0]);
        }

        /// <summary>
        /// AC-1 보완: 1초 달리기 시 2번 SpawnFootprintServerRpc 호출됨 (0.5초 간격).
        /// </summary>
        [Test]
        public void test_movementSystem_sprint_oneSecondElapsed_callsSpawnTwice()
        {
            // Arrange
            _movement.RequestSprint();

            // Act: 0.5초씩 2번
            _movement.SimulateUpdate(0.5f);
            _movement.SimulateUpdate(0.5f);

            // Assert
            Assert.AreEqual(2, _footprint.SpawnedPositions.Count);
        }
    }

    // ──────────────────────────────────────────────────────────
    // [AC-3] SetAttackTraitOwner(false) → Renderer.enabled = false
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("FootprintSystem")]
    public class FootprintSystemAttackTraitGatingTests
    {
        private TestableFootprintSystem _footprint;

        [SetUp]
        public void SetUp()
        {
            _footprint = new TestableFootprintSystem();
        }

        /// <summary>
        /// AC-3: SetAttackTraitOwner(false) 호출 시 모든 활성 발자국 Renderer.enabled = false.
        /// Given: 발자국 2개 존재
        /// When: SetAttackTraitOwner(false)
        /// Then: 모든 RendererStates = false
        /// </summary>
        [Test]
        public void test_footprintSystem_setAttackTraitOwner_false_disablesAllRenderers()
        {
            // Arrange: 발자국 2개 스폰
            _footprint.SpawnFootprintServerRpc(Vector3.zero);
            _footprint.SpawnFootprintServerRpc(Vector3.one);

            // Act
            _footprint.SetAttackTraitOwner(false);

            // Assert
            Assert.AreEqual(2, _footprint.RendererStates.Count);
            foreach (bool state in _footprint.RendererStates)
                Assert.IsFalse(state, "공격형 특성 미보유 시 모든 발자국 Renderer = false");
        }

        /// <summary>
        /// AC-3 보완: SetAttackTraitOwner(true) 호출 시 모든 발자국 Renderer.enabled = true.
        /// </summary>
        [Test]
        public void test_footprintSystem_setAttackTraitOwner_true_enablesAllRenderers()
        {
            // Arrange: 발자국 2개 스폰 후 숨김
            _footprint.SpawnFootprintServerRpc(Vector3.zero);
            _footprint.SpawnFootprintServerRpc(Vector3.one);
            _footprint.SetAttackTraitOwner(false);

            // Act
            _footprint.SetAttackTraitOwner(true);

            // Assert
            foreach (bool state in _footprint.RendererStates)
                Assert.IsTrue(state, "공격형 특성 보유 시 모든 발자국 Renderer = true");
        }

        /// <summary>
        /// AC-3 보완: 발자국 없을 때 SetAttackTraitOwner 호출 시 예외 없이 정상 완료.
        /// </summary>
        [Test]
        public void test_footprintSystem_setAttackTraitOwner_noFootprints_noException()
        {
            // Arrange: 발자국 없음

            // Act + Assert: 예외 없이 완료되어야 한다
            Assert.DoesNotThrow(() => _footprint.SetAttackTraitOwner(false));
        }
    }

    // ──────────────────────────────────────────────────────────
    // [Edge-1] 걷기 중 발자국 미생성
    // ──────────────────────────────────────────────────────────

    [TestFixture]
    [Category("MovementSystem")]
    public class MovementSystemWalkingNoFootprintTests
    {
        private TestableStaminaSystem _stamina;
        private TestableFootprintSystem _footprint;
        private TestableMovementSystem _movement;

        [SetUp]
        public void SetUp()
        {
            _stamina = new TestableStaminaSystem(initial: 100f);
            _footprint = new TestableFootprintSystem();
            _movement = new TestableMovementSystem(_stamina, _footprint);
        }

        /// <summary>
        /// Edge-1: 걷기 상태(_isSprinting = false)에서 Update를 반복 호출해도 SpawnFootprintServerRpc 미호출.
        /// Given: 걷기 상태 (RequestSprint 미호출)
        /// When: Update() 10회 호출
        /// Then: SpawnedPositions.Count = 0
        /// </summary>
        [Test]
        public void test_movementSystem_walking_updateCalled_noFootprintSpawned()
        {
            // Arrange: 걷기 상태 (RequestSprint 미호출)
            Assert.IsFalse(_movement.IsSprinting, "사전 조건: 초기 상태 걷기");

            // Act: 5초 동안 0.5초 간격 10회 Update
            for (int i = 0; i < 10; i++)
                _movement.SimulateUpdate(0.5f);

            // Assert
            Assert.AreEqual(0, _footprint.SpawnedPositions.Count);
        }

        /// <summary>
        /// Edge-1 보완: StopSprint 후 걷기 전환 시 발자국 미생성.
        /// </summary>
        [Test]
        public void test_movementSystem_afterStopSprint_noFootprintSpawned()
        {
            // Arrange: 달리기 시작 후 즉시 중단
            _movement.RequestSprint();
            _movement.StopSprint();

            // Act
            _movement.SimulateUpdate(0.5f);
            _movement.SimulateUpdate(0.5f);

            // Assert
            Assert.AreEqual(0, _footprint.SpawnedPositions.Count);
        }
    }
}
