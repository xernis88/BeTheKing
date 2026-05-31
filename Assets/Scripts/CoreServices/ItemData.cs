// Implements: design/gdd/01-core-services.md — LootManager
// Story: production/epics/epic-core-services/story-004-loot-drop.md

using System;
using Unity.Netcode;
using UnityEngine;

namespace BeTheKing.CoreServices
{
    /// <summary>
    /// 아이템 한 개의 런타임 데이터. LootManager 드롭·스폰에 사용된다.
    /// NetworkVariable&lt;ItemData&gt;로 모든 클라이언트에 동기화된다.
    /// </summary>
    [System.Serializable]
    public struct ItemData : INetworkSerializable, IEquatable<ItemData>
    {
        [Tooltip("아이템 고유 식별자. 0이면 유효하지 않은 아이템.")]
        public int ItemId;

        [Tooltip("드롭 시 스폰할 NetworkObject 프리팹. LootConfig에서 ItemId로 조회한다.")]
        public string PrefabKey;

        [Tooltip("아이템 등급. 0=일반, 1=희귀, 2=영웅.")]
        public int Grade;

        /// <summary>유효한 아이템인지 검사한다 (ItemId > 0 및 PrefabKey 존재).</summary>
        public readonly bool IsValid => ItemId > 0 && !string.IsNullOrEmpty(PrefabKey);

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref ItemId);
            serializer.SerializeValue(ref Grade);
            if (serializer.IsReader)
            {
                serializer.GetFastBufferReader().ReadValueSafe(out PrefabKey);
                PrefabKey ??= string.Empty;
            }
            else
            {
                string key = PrefabKey ?? string.Empty;
                serializer.GetFastBufferWriter().WriteValueSafe(key);
            }
        }

        public bool Equals(ItemData other) =>
            ItemId == other.ItemId && Grade == other.Grade && PrefabKey == other.PrefabKey;

        public override bool Equals(object obj) => obj is ItemData other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(ItemId, Grade, PrefabKey);
    }
}
