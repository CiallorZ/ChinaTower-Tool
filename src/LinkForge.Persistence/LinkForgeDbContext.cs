using System.Text.Json;
using LinkForge.Application.Assets;
using LinkForge.Domain.Assets;
using LinkForge.Domain.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LinkForge.Persistence;

/// <summary>
/// LinkForge 的关系型数据上下文（LF-004，ADR-003 / ADR-032）。
/// </summary>
/// <remarks>
/// <para>
/// 直接映射 Domain 记录（<see cref="Project"/>、<see cref="Asset"/>、
/// <see cref="EnvironmentProfile"/>），不另建一套"持久化实体"：
/// 两套模型必然漂移，而"数据库里少了 Domain 的一个不变量字段"这类缺陷
/// 只会在报告或导出时才暴露。
/// </para>
/// <para>
/// <b>本上下文不包含任何秘密列。</b>资产只持有
/// <see cref="Asset.CredentialReference"/>（引用键），因此"数据库泄露即密码泄露"
/// 在结构上不成立（ADR-008）。
/// </para>
/// </remarks>
public sealed class LinkForgeDbContext : DbContext
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // 不缩进：这两列是机器可读的传输形态，不是给人看的；
        // 数据库体积与解析开销都随条目数增长。
        WriteIndented = false
    };

    /// <summary>创建上下文。</summary>
    /// <param name="options">上下文选项。</param>
    public LinkForgeDbContext(DbContextOptions<LinkForgeDbContext> options)
        : base(options)
    {
    }

    /// <summary>项目。</summary>
    public DbSet<Project> Projects => Set<Project>();

    /// <summary>资产。</summary>
    public DbSet<Asset> Assets => Set<Asset>();

    /// <summary>环境配置。</summary>
    public DbSet<EnvironmentProfile> Environments => Set<EnvironmentProfile>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        ConfigureProject(modelBuilder);
        ConfigureAsset(modelBuilder);
        ConfigureEnvironment(modelBuilder);
    }

    /// <summary>配置项目表。</summary>
    private static void ConfigureProject(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("projects");
            entity.HasKey(static p => p.Id);

            entity.Property(static p => p.Id).HasMaxLength(64).IsRequired();
            entity.Property(static p => p.Name).HasMaxLength(200).IsRequired();
            entity.Property(static p => p.Description).HasMaxLength(1024);
            entity.Property(static p => p.SchemaVersion).IsRequired();
            entity.Property(static p => p.CreatedAt).IsRequired();

            // 项目名重名会让"打开哪个项目"在界面上不可分辨，
            // 因此名称唯一由数据库保证，而不是只在界面上提示。
            entity.HasIndex(static p => p.Name).IsUnique();
        });
    }

    /// <summary>配置资产表。</summary>
    private static void ConfigureAsset(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Asset>(entity =>
        {
            entity.ToTable("assets");
            entity.HasKey(static a => a.Id);

            entity.Property(static a => a.Id).HasMaxLength(64).IsRequired();
            entity.Property(static a => a.ProjectId).HasMaxLength(64).IsRequired();
            entity.Property(static a => a.Name).HasMaxLength(AssetCsvLimits.NameLength).IsRequired();
            entity.Property(static a => a.Host).HasMaxLength(AssetCsvLimits.HostLength).IsRequired();
            entity.Property(static a => a.Vendor).HasMaxLength(AssetCsvLimits.VendorLength);
            entity.Property(static a => a.Note).HasMaxLength(AssetCsvLimits.NoteLength);
            entity.Property(static a => a.CredentialReference).HasMaxLength(128);

            // 类型存字符串：数据库里出现 "Camera" 比出现 "1" 更容易排查，
            // 也让枚举重排不会静默改变已有数据的含义。
            entity.Property(static a => a.Kind).HasConversion<string>().HasMaxLength(32).IsRequired();

            entity.Property(static a => a.Tags)
                .HasConversion(TagsConverter, TagsComparer)
                .HasMaxLength(AssetCsvLimits.TagsJsonLength);

            // 计算属性：数据库列里没有它的位置，必须显式忽略。
            entity.Ignore(static a => a.TargetText);

            entity.HasIndex(static a => a.ProjectId);
            entity.HasIndex(static a => new { a.ProjectId, a.Host, a.Port });

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(static a => a.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>配置环境表。</summary>
    private static void ConfigureEnvironment(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EnvironmentProfile>(entity =>
        {
            entity.ToTable("environments");
            entity.HasKey(static e => e.Id);

            entity.Property(static e => e.Id).HasMaxLength(64).IsRequired();
            entity.Property(static e => e.ProjectId).HasMaxLength(64).IsRequired();
            entity.Property(static e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(static e => e.ProxyUrl).HasMaxLength(512);

            entity.Property(static e => e.Variables)
                .HasConversion(VariablesConverter, VariablesComparer)
                .HasMaxLength(AssetCsvLimits.VariablesJsonLength);

            entity.HasIndex(static e => e.ProjectId);

            // 同一项目下环境名唯一：否则"切到测试环境"会有两个候选。
            entity.HasIndex(static e => new { e.ProjectId, e.Name }).IsUnique();

            entity.HasOne<Project>()
                .WithMany()
                .HasForeignKey(static e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>标签列：JSON 数组。</summary>
    private static readonly ValueConverter<IReadOnlyList<string>, string> TagsConverter = new(
        tags => JsonSerializer.Serialize(tags, JsonOptions),
        json => JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? new List<string>());

    /// <summary>
    /// 标签列的变更检测。
    /// </summary>
    /// <remarks>
    /// EF 默认用引用相等判断集合是否变化；<c>IReadOnlyList</c> 每次反序列化都是新实例，
    /// 没有比较器会导致"改了标签却没写库"或"没改也写库"。
    /// 三个函数写成静态方法而不是 lambda：EF 要求它们是表达式树，
    /// 而表达式树不允许 <c>out var</c> 声明与集合表达式等语法。
    /// </remarks>
    private static readonly ValueComparer<IReadOnlyList<string>> TagsComparer = new(
        (left, right) => TagsEqual(left, right),
        tags => TagsHash(tags),
        tags => TagsSnapshot(tags));

    /// <summary>环境变量列：JSON 对象。</summary>
    private static readonly ValueConverter<IReadOnlyDictionary<string, string>, string> VariablesConverter = new(
        variables => JsonSerializer.Serialize(variables, JsonOptions),
        json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOptions)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>环境变量列的变更检测（按键值对无序比较）。</summary>
    private static readonly ValueComparer<IReadOnlyDictionary<string, string>> VariablesComparer = new(
        (left, right) => VariablesEqual(left, right),
        variables => VariablesHash(variables),
        variables => VariablesSnapshot(variables));

    /// <summary>标签集合是否等价。</summary>
    private static bool TagsEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        left is not null && right is not null && left.SequenceEqual(right);

    /// <summary>标签集合的哈希。</summary>
    private static int TagsHash(IReadOnlyList<string> tags)
    {
        var hash = 0;

        foreach (var tag in tags)
        {
            hash = HashCode.Combine(hash, tag);
        }

        return hash;
    }

    /// <summary>标签集合的快照（EF 用它保存"原始值"）。</summary>
    private static IReadOnlyList<string> TagsSnapshot(IReadOnlyList<string> tags) => tags.ToList();

    /// <summary>环境变量是否等价。</summary>
    private static bool VariablesEqual(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var value) || !string.Equals(value, pair.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>环境变量的哈希。</summary>
    private static int VariablesHash(IReadOnlyDictionary<string, string> variables)
    {
        var hash = 0;

        foreach (var pair in variables)
        {
            hash = HashCode.Combine(hash, pair.Key, pair.Value);
        }

        return hash;
    }

    /// <summary>环境变量的快照。</summary>
    private static IReadOnlyDictionary<string, string> VariablesSnapshot(
        IReadOnlyDictionary<string, string> variables) =>
        new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase);
}
