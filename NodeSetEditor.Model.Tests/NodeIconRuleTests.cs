using System;
using System.Collections.Generic;
using NodeSetEditor.Model;
using Xunit;

namespace NodeSetEditor.Model.Tests
{
    /// <summary>
    /// The icon rules themselves, with no database or HTTP in the way.
    ///
    /// <see cref="NodeIconContractTests"/> in the Server suite proves the key survives the
    /// round trip to the client; these pin the decisions it makes, which is where the
    /// precedence between overlapping anchors actually lives. Event types made that
    /// precedence load-bearing: BaseEventType sits under BaseObjectType and ConditionType
    /// sits under BaseEventType, so all three anchors are on one chain.
    /// </summary>
    public class NodeIconRuleTests
    {
        // The real core supertype chains, trimmed to what these tests walk.
        private static readonly Dictionary<string, string?> CoreSuperTypes =
            new(StringComparer.Ordinal)
            {
                ["i=58"] = null,        // BaseObjectType — the top
                ["i=61"] = "i=58",      // FolderType
                ["i=2041"] = "i=58",    // BaseEventType
                ["i=2130"] = "i=2041",  // SystemEventType
                ["i=2782"] = "i=2041",  // ConditionType
                ["i=2915"] = "i=2782",  // AlarmConditionType
                ["i=9482"] = "i=2915",  // ExclusiveLevelAlarmType
                ["i=1001"] = "i=58",    // a vendor MachineType, under nothing special
            };

        private static string? Resolve(string nodeId) =>
            NodeIcons.Resolve(nodeId, id => CoreSuperTypes.TryGetValue(id, out var s) ? s : null);

        [Fact]
        public void BaseEventType_ResolvesToEventType()
        {
            Assert.Equal(NodeIcons.EventType, Resolve("i=2041"));
        }

        /// <summary>
        /// The anchor is nearer than BaseObjectType, so an event type must not come back as a
        /// plain "object" — which is exactly what every event type in an existing database is
        /// stamped with today, and why the rollout needs a re-stamp rather than a backfill.
        /// </summary>
        [Fact]
        public void ASubtypeOfBaseEventType_ResolvesToEventType_NotObject()
        {
            Assert.Equal(NodeIcons.EventType, Resolve("i=2130"));
        }

        [Fact]
        public void ConditionType_ResolvesToCondition_BeatingTheEventTypeAnchorAbove()
        {
            Assert.Equal(NodeIcons.Condition, Resolve("i=2782"));
        }

        [Theory]
        [InlineData("i=2915")] // AlarmConditionType
        [InlineData("i=9482")] // ExclusiveLevelAlarmType, two levels further down
        public void ConditionSubtypes_ResolveToCondition(string nodeId)
        {
            Assert.Equal(NodeIcons.Condition, Resolve(nodeId));
        }

        [Fact]
        public void AnOrdinaryObjectType_StillResolvesToObject()
        {
            Assert.Equal(NodeIcons.Object, Resolve("i=1001"));
        }

        [Fact]
        public void FolderType_IsUnaffectedByTheEventAnchors()
        {
            Assert.Equal(NodeIcons.Folder, Resolve("i=61"));
        }

        /// <summary>
        /// Both event keys describe the TYPE, so the type node wears them — unlike "property",
        /// which describes a PropertyType's instances and must not land on PropertyType.
        /// </summary>
        [Theory]
        [InlineData(NodeIcons.EventType)]
        [InlineData(NodeIcons.Condition)]
        [InlineData(NodeIcons.InterfaceType)]
        public void TypeDescribingKeys_AreTypeLevel(string icon)
        {
            Assert.True(NodeIcons.IsTypeLevel(icon));
        }

        [Theory]
        [InlineData(NodeIcons.Object)]
        [InlineData(NodeIcons.Property)]
        [InlineData(NodeIcons.DataVariable)]
        [InlineData(NodeIcons.Folder)]
        public void InstanceDescribingKeys_AreNotTypeLevel(string icon)
        {
            Assert.False(NodeIcons.IsTypeLevel(icon));
        }

        /// <summary>
        /// The asymmetry that scopes the glyph: nothing well-formed is an Object typed by a
        /// non-Condition event type, so "eventType" stops at the type node. Conditions are
        /// stateful and do exist as Objects, so "condition" flows down to them.
        /// </summary>
        [Fact]
        public void EventType_IsNotInheritedByInstances()
        {
            Assert.False(NodeIcons.IsInheritedByInstances(NodeIcons.EventType));
        }

        [Theory]
        [InlineData(NodeIcons.Condition)]
        [InlineData(NodeIcons.Object)]
        [InlineData(NodeIcons.Property)]
        [InlineData(NodeIcons.DataVariable)]
        [InlineData(NodeIcons.Folder)]
        public void EveryOtherKey_IsInheritedByInstances(string icon)
        {
            Assert.True(NodeIcons.IsInheritedByInstances(icon));
        }

        /// <summary>
        /// "No rule applies" is the normal case, not an error, so it must not be mistaken for
        /// a blocked key — an unstamped TypeDefinition leaves its instances unstamped too.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void AnAbsentKey_IsTreatedAsInheritable(string? icon)
        {
            Assert.True(NodeIcons.IsInheritedByInstances(icon));
        }

        [Fact]
        public void ATypeUnderNoAnchor_ResolvesToNull()
        {
            Assert.Null(NodeIcons.Resolve("i=9999", _ => null));
        }

        /// <summary>
        /// A HasSubtype cycle is a malformed NodeSet, but the importer must not hang on one.
        /// </summary>
        [Fact]
        public void ACycleTerminates()
        {
            var cyclic = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["a"] = "b",
                ["b"] = "a",
            };

            Assert.Null(NodeIcons.Resolve("a", id => cyclic.TryGetValue(id, out var s) ? s : null));
        }
    }
}
