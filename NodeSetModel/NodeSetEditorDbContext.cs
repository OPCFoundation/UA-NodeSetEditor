using Microsoft.EntityFrameworkCore;

namespace NodeSetEditor.Model
{
    public class NodeSetEditorDbContext : DbContext
    {
        public DbSet<UserPreference> UserPreferences => Set<UserPreference>();
        public DbSet<LoginCode> LoginCodes => Set<LoginCode>();
        public DbSet<LicenseOption> LicenseOptions => Set<LicenseOption>();
        public DbSet<Workspace> Workspaces => Set<Workspace>();
        public DbSet<WorkspaceAcl> WorkspaceAcls => Set<WorkspaceAcl>();
        public DbSet<Model> Models => Set<Model>();
        public DbSet<WorkspaceModel> WorkspaceModels => Set<WorkspaceModel>();
        public DbSet<Node> Nodes => Set<Node>();
        public DbSet<Reference> References => Set<Reference>();
        public DbSet<SubTypeHierarchy> SubTypeHierarchy => Set<SubTypeHierarchy>();
        public DbSet<NodeSetType> NodeSetTypes => Set<NodeSetType>();
        public DbSet<TypeDependency> TypeDependencies => Set<TypeDependency>();
        public DbSet<ValidationDocument> ValidationDocuments => Set<ValidationDocument>();
        public DbSet<ValidationJob> ValidationJobs => Set<ValidationJob>();
        public DbSet<ValidationJobResult> ValidationJobResults => Set<ValidationJobResult>();
        public DbSet<ValidationUploadChunk> ValidationUploadChunks => Set<ValidationUploadChunk>();
        public DbSet<NodeSetUploadChunk> NodeSetUploadChunks => Set<NodeSetUploadChunk>();

        public NodeSetEditorDbContext(DbContextOptions<NodeSetEditorDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            ConfigureUserPreference(modelBuilder);
            ConfigureLoginCode(modelBuilder);
            ConfigureLicenseOption(modelBuilder);
            ConfigureWorkspace(modelBuilder);
            ConfigureWorkspaceAcl(modelBuilder);
            ConfigureModel(modelBuilder);
            ConfigureWorkspaceModel(modelBuilder);
            ConfigureNode(modelBuilder);
            ConfigureReference(modelBuilder);
            ConfigureSubTypeHierarchy(modelBuilder);
            ConfigureNodeSetType(modelBuilder);
            ConfigureTypeDependency(modelBuilder);
            ConfigureValidationDocument(modelBuilder);
            ConfigureValidationJob(modelBuilder);
            ConfigureValidationJobResult(modelBuilder);
            ConfigureValidationUploadChunk(modelBuilder);
            ConfigureNodeSetUploadChunk(modelBuilder);
        }

        private static void ConfigureUserPreference(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UserPreference>(entity =>
            {
                entity.HasKey(e => e.UserId);
                entity.Property(e => e.UserId).HasMaxLength(256);
                entity.Property(e => e.ThemeMode).HasMaxLength(16);
                entity.Property(e => e.Name).HasMaxLength(256);
                entity.Property(e => e.DefaultDomain).HasMaxLength(256);
                entity.Property(e => e.DefaultLicense).HasMaxLength(128);
                entity.Property(e => e.DefaultLicenseUrl).HasMaxLength(512);
                entity.Property(e => e.DefaultCopyrightHolder).HasMaxLength(512);

                // Display name is shown to other users and must be globally unique.
                // App-level checks compare case-insensitively; this index is the
                // backstop (Postgres allows multiple NULLs, so unprovisioned rows
                // don't collide).
                entity.HasIndex(e => e.Name).IsUnique();
            });
        }

        private static void ConfigureLoginCode(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LoginCode>(entity =>
            {
                entity.HasKey(e => e.Email);
                entity.Property(e => e.Email).HasMaxLength(320); // RFC 5321 max email length
                entity.Property(e => e.CodeHash).HasMaxLength(128).IsRequired();
                entity.Property(e => e.Salt).HasMaxLength(64).IsRequired();
            });
        }

        private static void ConfigureLicenseOption(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LicenseOption>(entity =>
            {
                entity.HasKey(e => e.Id);

                // Fixed catalog seeded via HasData — keys are explicit, never generated.
                entity.Property(e => e.Id).ValueGeneratedNever();
                entity.Property(e => e.SpdxId).HasMaxLength(128).IsRequired();
                entity.Property(e => e.Name).HasMaxLength(256).IsRequired();
                entity.Property(e => e.ReferenceUrl).HasMaxLength(512);

                entity.HasIndex(e => e.SpdxId).IsUnique();

                entity.HasData(SeedLicenseOptions());
            });
        }

        /// <summary>
        /// Curated subset of SPDX licenses that drives the license selector, plus a single
        /// "Other / Proprietary" entry (<see cref="LicenseOption.IsCustom"/>) for licenses
        /// not in the list. Applied by EnsureCreated on DB (re)build. To add a license,
        /// append a new row with the next Id — never renumber existing Ids.
        /// </summary>
        private static LicenseOption[] SeedLicenseOptions()
        {
            LicenseOption L(int id, string spdx, string name, string? url, int sort) =>
                new() { Id = id, SpdxId = spdx, Name = name, ReferenceUrl = url, IsCustom = false, SortOrder = sort };

            return new[]
            {
                L(1,  "MIT",              "MIT License",                                  "https://spdx.org/licenses/MIT.html",              10),
                L(2,  "Apache-2.0",       "Apache License 2.0",                           "https://spdx.org/licenses/Apache-2.0.html",       20),
                L(11, "EPL-2.0",          "Eclipse Public License 2.0",                   "https://spdx.org/licenses/EPL-2.0.html",          30),
                L(12, "Unlicense",        "The Unlicense",                                "https://spdx.org/licenses/Unlicense.html",        40),
                L(13, "CC0-1.0",          "Creative Commons Zero v1.0 Universal",         "https://spdx.org/licenses/CC0-1.0.html",          50),
                L(14, "CC-BY-4.0",        "Creative Commons Attribution 4.0",             "https://spdx.org/licenses/CC-BY-4.0.html",        60),
                new LicenseOption
                {
                    Id = 99,
                    SpdxId = "LicenseRef-Custom",
                    Name = "Custom",
                    ReferenceUrl = null,
                    IsCustom = true,
                    SortOrder = 1000,
                },
            };
        }

        private static void ConfigureWorkspace(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Workspace>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Id)
                    .ValueGeneratedOnAdd();

                entity.HasIndex(e => e.OwnerUserId);

                entity.HasMany(e => e.Acl)
                    .WithOne(a => a.Workspace)
                    .HasForeignKey(a => a.WorkspaceId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasMany(e => e.Models)
                    .WithOne(wm => wm.Workspace)
                    .HasForeignKey(wm => wm.WorkspaceId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }

        private static void ConfigureWorkspaceAcl(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<WorkspaceAcl>(entity =>
            {
                entity.HasKey(e => new { e.WorkspaceId, e.Email });
                entity.HasIndex(e => e.Email);
            });
        }

        private static void ConfigureModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Model>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.Property(e => e.Id)
                    .ValueGeneratedOnAdd();

                entity.HasIndex(e => e.Uri);

                // One row per URI + normalized SemVer — also serves as sort index
                entity.HasIndex(e => new { e.Uri, e.VersionNorm })
                    .IsUnique();

                // Stored as text so the value is readable in the DB and new members can be
                // added without renumbering. Unknown = rows written before provenance existed.
                entity.Property(e => e.Origin)
                    .HasConversion<string>()
                    .HasMaxLength(16)
                    .HasDefaultValue(ModelOrigin.Unknown);

                entity.Property(e => e.License).HasMaxLength(128);
                entity.Property(e => e.LicenseUrl).HasMaxLength(512);
                entity.Property(e => e.CopyrightHolder).HasMaxLength(512);

                entity.HasMany(e => e.Nodes)
                    .WithOne(n => n.Model)
                    .HasForeignKey(n => n.ModelId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasMany(e => e.References)
                    .WithOne(r => r.Model)
                    .HasForeignKey(r => r.ModelId)
                    .OnDelete(DeleteBehavior.Cascade);

                // `json` (not `jsonb`) — jsonb normalises numbers to canonical
                // form via PostgreSQL's `numeric` type, which writes very
                // large whole numbers in long-integer form (e.g.
                // "340282350000000000000000000000000000000" instead of
                // "3.4028235e+38"). Variant values written through here need
                // their original textual form preserved for Part 6 round-trip.
                // We don't query these columns by their inner JSON shape, so
                // jsonb's GIN-index advantages don't apply.
                entity.Property(e => e.Metadata)
                    .HasColumnType("json");

                // Content stored as bytea — can be large (10+ MB for Core nodeset)
                entity.Property(e => e.Content)
                    .HasColumnType("bytea");

                entity.HasMany(e => e.Workspaces)
                    .WithOne(wm => wm.Model)
                    .HasForeignKey(wm => wm.ModelId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }

        private static void ConfigureWorkspaceModel(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<WorkspaceModel>(entity =>
            {
                entity.HasKey(e => new { e.WorkspaceId, e.ModelId });
            });
        }

        private static void ConfigureNode(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Node>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasIndex(e => e.NodeId);

                entity.HasIndex(e => e.ModelId);

                entity.HasIndex(e => new { e.ModelId, e.NodeClass });

                entity.HasIndex(e => e.SuperTypeId);

                entity.HasIndex(e => e.ParentNodeId);

                // For name-based filtering in QueryNodesAsync
                entity.HasIndex(e => e.DisplayName);

                // `json` not `jsonb` — see Metadata for rationale; jsonb's
                // `numeric` normalisation expands large floats like
                // 3.4028235e+38 into long-integer form, breaking Part 6
                // round-trip for Variant values stored in attrs.Value.
                entity.Property(e => e.Attributes)
                    .HasColumnType("json");
            });
        }

        private static void ConfigureReference(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Reference>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasIndex(e => e.SourceNodeId);

                entity.HasIndex(e => e.TargetNodeId);

                entity.HasIndex(e => e.ModelId);

                entity.HasIndex(e => new { e.ReferenceTypeId, e.SourceNodeId });
            });
        }

        private static void ConfigureSubTypeHierarchy(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SubTypeHierarchy>(entity =>
            {
                entity.HasKey(e => new { e.SubTypeNodeId, e.SuperTypeNodeId });

                entity.HasIndex(e => e.SuperTypeNodeId);
            });
        }

        private static void ConfigureNodeSetType(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<NodeSetType>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasIndex(e => e.NodeId);

                entity.HasIndex(e => e.ModelId);

                entity.HasIndex(e => new { e.ModelId, e.NodeClass });

                entity.HasIndex(e => e.SuperTypeId);

                // Same rationale as Nodes.Attributes — using `json` so the
                // schema matches across all blob columns and any nested
                // numeric we ever serialise here keeps its original textual
                // form. Children / References don't need jsonb's GIN index
                // since they're loaded as a whole alongside the type row.
                entity.Property(e => e.Children)
                    .HasColumnType("json");

                entity.Property(e => e.References)
                    .HasColumnType("json");

                entity.HasOne(e => e.Model)
                    .WithMany()
                    .HasForeignKey(e => e.ModelId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasMany(e => e.Dependencies)
                    .WithOne(d => d.Type)
                    .HasForeignKey(d => d.TypeId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }

        private static void ConfigureTypeDependency(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TypeDependency>(entity =>
            {
                entity.HasKey(e => e.Id);

                entity.HasIndex(e => e.TypeId);

                entity.HasIndex(e => e.ReferencedTypeNodeId);

                entity.HasIndex(e => new { e.TypeId, e.ReferencedTypeNodeId })
                    .IsUnique();
            });
        }

        private static void ConfigureValidationDocument(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ValidationDocument>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).ValueGeneratedOnAdd();

                entity.Property(e => e.FileName).HasMaxLength(512).IsRequired();
                entity.Property(e => e.ContentType).HasMaxLength(256);
                entity.Property(e => e.UploadedByUserId).HasMaxLength(256).IsRequired();

                // Word docs up to 64 MB — stored inline as bytea like Model.Content.
                entity.Property(e => e.Content).HasColumnType("bytea");

                // Documents are listed per workspace.
                entity.HasIndex(e => e.WorkspaceId);

                // Cascade from Workspace only (documents are per-workspace; the model to
                // validate against is chosen per job, not tied to the document).
                entity.HasOne(e => e.Workspace)
                    .WithMany()
                    .HasForeignKey(e => e.WorkspaceId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasMany(e => e.Jobs)
                    .WithOne(j => j.Document)
                    .HasForeignKey(j => j.DocumentId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }

        private static void ConfigureValidationJob(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ValidationJob>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.Property(e => e.Id).ValueGeneratedOnAdd();

                entity.Property(e => e.State).HasConversion<int>();
                entity.Property(e => e.ModelUri).HasMaxLength(512);
                entity.Property(e => e.RequestedByUserId).HasMaxLength(256).IsRequired();
                entity.Property(e => e.Summary).HasMaxLength(2048);
                entity.Property(e => e.WorkerMessage).HasMaxLength(2048);
                entity.Property(e => e.WorkerId).HasMaxLength(256);
                entity.Property(e => e.LockToken).HasMaxLength(128);

                entity.HasIndex(e => e.DocumentId);
                // WorkspaceId/ModelId are denormalized columns (no FK) used for
                // worker queries and IDOR re-checks; the cascade runs through DocumentId.
                entity.HasIndex(e => new { e.WorkspaceId, e.ModelId });
                // Pop scans the oldest Queued job.
                entity.HasIndex(e => new { e.State, e.CreatedUtc });

                entity.HasOne(e => e.Result)
                    .WithOne(r => r.Job)
                    .HasForeignKey<ValidationJobResult>(r => r.JobId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }

        private static void ConfigureValidationJobResult(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ValidationJobResult>(entity =>
            {
                entity.HasKey(e => e.JobId);

                entity.Property(e => e.LogContent).HasColumnType("bytea");

                // `json` not `jsonb` — matches the other blob columns; the entries
                // array is loaded whole with the row, so GIN indexing isn't needed.
                entity.Property(e => e.Entries).HasColumnType("json");
            });
        }

        private static void ConfigureValidationUploadChunk(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ValidationUploadChunk>(entity =>
            {
                entity.HasKey(e => new { e.UploadId, e.ChunkIndex });
                entity.Property(e => e.UploadId).HasMaxLength(128);
                entity.Property(e => e.FileName).HasMaxLength(512).IsRequired();
                entity.Property(e => e.UploadedByUserId).HasMaxLength(256).IsRequired();
                entity.Property(e => e.Data).HasColumnType("bytea");
            });
        }

        private static void ConfigureNodeSetUploadChunk(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<NodeSetUploadChunk>(entity =>
            {
                entity.HasKey(e => new { e.UploadId, e.ChunkIndex });
                entity.Property(e => e.UploadId).HasMaxLength(128);
                entity.Property(e => e.FileName).HasMaxLength(512).IsRequired();
                entity.Property(e => e.Data).HasColumnType("bytea");
            });
        }
    }
}
