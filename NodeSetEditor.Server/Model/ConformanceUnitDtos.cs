namespace NodeSetEditor.Server.Model
{
    /// <summary>
    /// One conformance unit declared by a NodeSet, with how many of its nodes name it.
    /// Conformance units are the NodeSet XML's &lt;Category&gt; elements; a node may declare
    /// several, and the same unit is typically named by many nodes.
    /// </summary>
    public class ConformanceUnitInfo
    {
        public string Name { get; set; } = null!;

        /// <summary>Number of nodes in the model that declare this conformance unit.</summary>
        public int NodeCount { get; set; }
    }
}
