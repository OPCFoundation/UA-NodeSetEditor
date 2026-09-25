using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NodeSetEditor.Model;
using NodeSetEditor.Server.Model;

namespace NodeSetEditor.Server.Services
{
    /// <summary>
    /// EF Core-backed implementation of <see cref="IConformanceUnitService"/>.
    /// </summary>
    public class ConformanceUnitService : IConformanceUnitService
    {
        /// <summary>
        /// Aggregates the conformance units out of the nodes' Attributes JSON. Doing the
        /// unnest/group in PostgreSQL keeps a large model (the Core nodeset is tens of
        /// thousands of nodes) from being pulled into memory just to count strings.
        ///
        /// The column is `json`, not `jsonb` (Variant round-trip — see NodeSetEditorDbContext),
        /// so it is cast per row; `jsonb_typeof` guards against a "Category" that isn't an
        /// array rather than letting jsonb_array_elements_text raise.
        /// </summary>
        private const string ListSql = @"
            SELECT unit AS name, COUNT(*)::int AS node_count
            FROM (
                SELECT jsonb_array_elements_text(cat) AS unit
                FROM (
                    SELECT (""Attributes""::jsonb) -> 'Category' AS cat
                    FROM ""Nodes""
                    WHERE ""ModelId"" = @modelId AND ""Attributes"" IS NOT NULL
                ) attrs
                WHERE jsonb_typeof(cat) = 'array'
            ) units
            GROUP BY unit
            ORDER BY unit";

        private readonly NodeSetEditorDbContext _db;

        public ConformanceUnitService(NodeSetEditorDbContext db)
        {
            _db = db;
        }

        public async Task<IReadOnlyList<ConformanceUnitInfo>> ListAsync(Guid modelId)
        {
            var units = new List<ConformanceUnitInfo>();

            var connection = _db.Database.GetDbConnection();
            await _db.Database.OpenConnectionAsync();
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = ListSql;
                command.Parameters.Add(Parameter(command, "modelId", modelId));

                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    units.Add(new ConformanceUnitInfo
                    {
                        Name = reader.GetString(0),
                        NodeCount = reader.GetInt32(1),
                    });
                }
            }
            finally
            {
                await _db.Database.CloseConnectionAsync();
            }

            return units;
        }


        private static DbParameter Parameter(DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            return parameter;
        }
    }
}
