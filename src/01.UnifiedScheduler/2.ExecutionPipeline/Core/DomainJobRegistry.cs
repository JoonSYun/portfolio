// [담당업무 2] 적재 단계 — DB 에 저장된 JOB_TYPE 문자열을 assembly scan 결과로 Type 에 매핑한다.

using System.Reflection;

namespace Portfolio.UnifiedScheduler.Jobs;

/// <summary>
/// Resolves a short Job class name (e.g. <c>"TisProductJob"</c>) to its
/// concrete <see cref="Type"/> implementing <see cref="IDomainJob"/>.
///
/// <para>
/// Populated at construction by scanning the assembly that owns this class
/// for non-abstract <see cref="IDomainJob"/> implementations. Storing short
/// class names in <c>SCH_DOMAIN.JOB_TYPE</c> keeps the database rows readable
/// and decouples persisted values from namespace / assembly renames.
/// </para>
///
/// <para>
/// If two jobs ever share a short name (across different namespaces), the
/// constructor throws at startup — surface the ambiguity loudly instead of
/// silently picking one.
/// </para>
/// </summary>
public class DomainJobRegistry
{
    private readonly IReadOnlyDictionary<string, Type> _map;

    public DomainJobRegistry()
    {
        var jobInterface = typeof(IDomainJob);
        var assembly     = jobInterface.Assembly;

        var groups = assembly.GetTypes()
            .Where(t => !t.IsAbstract
                        && !t.IsInterface
                        && t.IsClass
                        && t.IsPublic
                        && !t.ContainsGenericParameters
                        && jobInterface.IsAssignableFrom(t))
            .GroupBy(t => t.Name)
            .ToArray();

        var dupes = groups.Where(g => g.Count() > 1).ToArray();
        if (dupes.Length > 0)
        {
            var detail = string.Join("; ",
                dupes.Select(g => $"{g.Key} => [{string.Join(", ", g.Select(t => t.FullName))}]"));
            throw new InvalidOperationException(
                $"[DomainJobRegistry] Duplicate IDomainJob short names: {detail}");
        }

        _map = groups.ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Resolves <paramref name="shortName"/> (e.g. <c>"TisProductJob"</c>) to
    /// the concrete Job type. Throws if unknown — unknown names usually mean
    /// a DB row was set for a job that does not exist in this build.
    /// </summary>
    public Type Resolve(string shortName)
    {
        if (string.IsNullOrWhiteSpace(shortName))
            throw new InvalidOperationException("[DomainJobRegistry] JobType is empty");

        if (!_map.TryGetValue(shortName, out var type))
        {
            var known = string.Join(", ", _map.Keys.OrderBy(k => k));
            throw new InvalidOperationException(
                $"[DomainJobRegistry] Unknown JobType '{shortName}'. Known: [{known}]");
        }

        return type;
    }

    /// <summary>Snapshot of registered Job short names — handy for diagnostics.</summary>
    public IReadOnlyCollection<string> RegisteredNames => _map.Keys.ToArray();
}
