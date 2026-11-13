using Microsoft.EntityFrameworkCore;
using Portfolio.UnifiedScheduler.Data;

namespace Portfolio.UnifiedScheduler.Data;

public class SchedulerHubDbContext : DbContext
{
    public SchedulerHubDbContext(DbContextOptions<SchedulerHubDbContext> options)
        : base(options) { }

    // 엔티티 DbSet
    public DbSet<SchDomain>              Domains         { get; set; } = null!;
    public DbSet<SchDomainGroup>         DomainGroups    { get; set; } = null!;
    public DbSet<SchDomainGroupMap>      DomainGroupMaps { get; set; } = null!;
    public DbSet<SchDomainSchedule>      Schedules       { get; set; } = null!;
    public DbSet<SchDomainBrandOverride> Overrides       { get; set; } = null!;
    public DbSet<SchDomainBrandCron>     BrandCrons      { get; set; } = null!;
    public DbSet<SchBrand>               Brands          { get; set; } = null!;
    public DbSet<SchBrandAddMail>        BrandAddMails   { get; set; } = null!;
    public DbSet<SchQueue>               Queues          { get; set; } = null!;
    public DbSet<SchJobLog>              JobLogs         { get; set; } = null!;
    public DbSet<SchJobLogBk>            JobLogsBk       { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // 복합 PK: SCH_DOMAIN_BRAND_OVERRIDE
        modelBuilder.Entity<SchDomainBrandOverride>()
            .HasKey(o => new { o.DomainCode, o.BrandCode });

        // SCH_DOMAIN_BRAND_CRON — 시간대별 cron 자식 (override 와 복합 FK, CASCADE)
        modelBuilder.Entity<SchDomainBrandCron>()
            .HasOne(c => c.Override)
            .WithMany(o => o.Crons)
            .HasForeignKey(c => new { c.DomainCode, c.BrandCode })
            .OnDelete(DeleteBehavior.Cascade);

        // SCH_BRAND_ADD_MAIL — 브랜드별 추가 메일 수신자 (브랜드와 FK, CASCADE)
        modelBuilder.Entity<SchBrandAddMail>()
            .HasOne(m => m.Brand)
            .WithMany()
            .HasForeignKey(m => m.BrandCode)
            .OnDelete(DeleteBehavior.Cascade);

        // SCH_DOMAIN ↔ SCH_DOMAIN_SCHEDULE 1:1
        modelBuilder.Entity<SchDomain>()
            .HasOne(d => d.Schedule)
            .WithOne(s => s.Domain!)
            .HasForeignKey<SchDomainSchedule>(s => s.DomainCode);

        // SCH_DOMAIN_GROUP_MAP — 복합 PK + 양쪽 FK (Domain CASCADE, Group RESTRICT)
        modelBuilder.Entity<SchDomainGroupMap>()
            .HasKey(m => new { m.DomainCode, m.GroupCode });
        modelBuilder.Entity<SchDomainGroupMap>()
            .HasOne(m => m.Domain)
            .WithMany(d => d.GroupMaps)
            .HasForeignKey(m => m.DomainCode)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<SchDomainGroupMap>()
            .HasOne(m => m.Group)
            .WithMany()
            .HasForeignKey(m => m.GroupCode)
            .OnDelete(DeleteBehavior.Restrict);

        // SCH_JOB_LOG.LOG_ID UNIQUE 인덱스
        modelBuilder.Entity<SchJobLog>()
            .HasIndex(r => r.LogId)
            .IsUnique();

        // SCH_JOB_LOG 의 코드/식별자 컬럼은 DB 가 varchar(Korean_Wansung_CI_AS) 다. 기본 string 매핑(nvarchar)
        // 으로 보내면 varchar 컬럼이 nvarchar 로 implicit convert 되어 인덱스 키 seek 가 깨진다(non-SARGable):
        //   - LOG_ID    → clustered PK(LOG_ID) 단건 seek (모든 상태전이 UPDATE/INSERT 가드)
        //   - DOMAIN/BRAND_CODE → IX_SCH_JOB_LOG_ACTIVE 키 seek (활성잡 조회)
        //   - STATUS    → 같은 filtered index 의 WHERE STATUS IN(...) 매칭
        // ※ 자유텍스트 nvarchar 컬럼(ERROR_MESSAGE 등)은 CP949 데이터 손실 위험이 있어 건드리지 않는다 —
        //   그래서 전역 convention 이 아니라 seek 대상 코드 컬럼만 per-property 로 비유니코드 지정한다.
        modelBuilder.Entity<SchJobLog>().Property(r => r.LogId).IsUnicode(false);
        modelBuilder.Entity<SchJobLog>().Property(r => r.DomainCode).IsUnicode(false);
        modelBuilder.Entity<SchJobLog>().Property(r => r.BrandCode).IsUnicode(false);

        // SCH_JOB_LOG.STATUS — enum ↔ VARCHAR 저장. STATUS 는 enum→string 변환이라 위 코드 컬럼과 별개로
        // 변환 매핑에 직접 IsUnicode(false) 를 건다 (filtered index 매칭에 필수).
        modelBuilder.Entity<SchJobLog>()
            .Property(r => r.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsUnicode(false);

        modelBuilder.Entity<SchJobLog>()
            .HasIndex(r => new { r.Status, r.EnqueuedDt });

        modelBuilder.Entity<SchJobLog>()
            .HasIndex(r => new { r.DomainCode, r.BrandCode, r.ScheduledDt });

        // SCH_JOB_LOG_BK.STATUS — enum ↔ VARCHAR 저장 (archive 도 동일 매핑 + 동일 비유니코드 정렬)
        modelBuilder.Entity<SchJobLogBk>()
            .Property(r => r.Status)
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsUnicode(false);
    }
}
