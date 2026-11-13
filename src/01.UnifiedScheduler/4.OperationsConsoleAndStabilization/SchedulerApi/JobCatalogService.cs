using System.Reflection;
using System.Text.Json;
using Portfolio.UnifiedScheduler.Jobs;
using Portfolio.UnifiedScheduler.Models;

namespace Portfolio.UnifiedScheduler.Infrastructure;

/// <summary>
/// Reflects the Portfolio.UnifiedScheduler assembly to expose the list of IDomainJob
/// implementations (for JOB_TYPE dropdown) and payload DTOs (for PARAM_DTO_TYPE
/// dropdown + Monaco schema hint). Computed once at construction.
/// </summary>
public class JobCatalogService
{
    private readonly List<JobTypeItem>  _jobTypes;
    private readonly List<ParamDtoItem> _paramDtos;
    private readonly Dictionary<string, Type> _typeIndex;
    private readonly Dictionary<string, Type> _nameIndex;

    public JobCatalogService()
    {
        var asm = typeof(JobCatalogService).Assembly;

        _jobTypes = asm.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(IDomainJob).IsAssignableFrom(t))
            .Select(t => new JobTypeItem
            {
                Fqdn                   = $"{t.FullName}, {asm.GetName().Name}",
                Name                   = t.Name,
                Namespace              = t.Namespace,
                AssemblyQualifiedName  = t.AssemblyQualifiedName ?? "",
                ParamName              = ResolveParamTypeName(t),
            })
            .OrderBy(x => x.Name)
            .ToList();

        // Payload DTOs share the ISchedulerParam marker interface. Using the marker
        // is more robust than namespace matching — folder layout and the
        // declared C# namespace are not required to agree.
        _paramDtos = asm.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(ISchedulerParam).IsAssignableFrom(t))
            .Select(t => new ParamDtoItem
            {
                Fqdn                  = $"{t.FullName}, {asm.GetName().Name}",
                Name                  = t.Name,
                Namespace             = t.Namespace,
                AssemblyQualifiedName = t.AssemblyQualifiedName ?? "",
            })
            .OrderBy(x => x.Name)
            .ToList();

        _typeIndex = asm.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.FullName != null)
            .ToDictionary(t => t.FullName!, t => t, StringComparer.Ordinal);

        // Secondary index keyed by simple class name. Not guaranteed unique,
        // but admin UI payload DTOs/jobs all have distinct names today, so a
        // short-name lookup is a safe fallback for legacy values stored as
        // e.g. "DBReindexParam" instead of the assembly-qualified form.
        _nameIndex = asm.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    public IReadOnlyList<JobTypeItem>  JobTypes  => _jobTypes;
    public IReadOnlyList<ParamDtoItem> ParamDtos => _paramDtos;

    /// <summary>
    /// Accepts, in order of precedence:
    ///   1. assembly-qualified "WS.X.Y.Foo, WS.Asm"
    ///   2. full type name "WS.X.Y.Foo"
    ///   3. simple class name "Foo" (when unambiguous within the assembly)
    /// Returns null when nothing matches.
    /// </summary>
    public Type? ResolveType(string fqdn)
    {
        if (string.IsNullOrWhiteSpace(fqdn)) return null;
        var input = fqdn.Trim();

        var direct = Type.GetType(input, throwOnError: false);
        if (direct is not null) return direct;

        var bare = input.Split(',', 2)[0].Trim();
        if (_typeIndex.TryGetValue(bare, out var byFull)) return byFull;

        return _nameIndex.TryGetValue(bare, out var byName) ? byName : null;
    }

    /// <summary>
    /// Introspects public instance properties and builds a lightweight schema + skeleton JSON.
    /// </summary>
    public ParamDtoSchema? GetSchema(string fqdn)
    {
        var t = ResolveType(fqdn);
        if (t is null) return null;

        var reflected = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToList();

        var props = reflected
            .Select(p => new ParamProperty
            {
                Name     = p.Name,
                Type     = FriendlyTypeName(p.PropertyType),
                Nullable = IsNullable(p),
                Required = !IsNullable(p),
            })
            .ToList();

        // 스켈레톤 기본값은 friendly 문자열이 아니라 실제 Type 으로 계산한다 —
        // Dictionary 같은 제네릭은 friendly 이름만으로 분기하기 어렵기 때문.
        var skeleton = new Dictionary<string, object?>();
        foreach (var p in reflected)
            skeleton[p.Name] = DefaultValueFor(p.PropertyType);

        return new ParamDtoSchema
        {
            Fqdn         = fqdn,
            Name         = t.Name,
            Properties   = props,
            SkeletonJson = JsonSerializer.Serialize(skeleton, new JsonSerializerOptions { WriteIndented = true }),
        };
    }

    /// <summary>
    /// Job 타입이 상속한 <c>JobBase&lt;TSelf, TParam&gt;</c> 를 BaseType 체인에서 찾아
    /// TParam 의 짧은 클래스명을 반환. 해당 제네릭 베이스를 못 찾으면 null.
    /// Domain 생성 폼의 JobType → ParamDto 자동 채움에 쓰인다.
    /// </summary>
    private static string? ResolveParamTypeName(Type jobType)
    {
        for (var t = jobType.BaseType; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(JobBase<,>))
                return t.GetGenericArguments()[1].Name;
        }
        return null;
    }

    private static bool IsNullable(PropertyInfo p)
    {
        var nullableInfo = new NullabilityInfoContext().Create(p);
        return nullableInfo.ReadState == NullabilityState.Nullable;
    }

    private static string FriendlyTypeName(Type t)
    {
        var underlying = Nullable.GetUnderlyingType(t) ?? t;

        // Dictionary/IDictionary 는 key/value 타입을 재귀적으로 풀어 map<k,v> 로 표기한다.
        // (그렇지 않으면 underlying.Name 이 "Dictionary`2" 같은 CLR mangled 이름으로 새어 나간다)
        if (TryGetDictionaryTypes(underlying, out var key, out var value))
            return $"map<{FriendlyTypeName(key)},{FriendlyTypeName(value)}>";

        // List/IReadOnlyList/배열도 같은 이유로 원소 타입을 풀어 list<T> 로 표기한다("IReadOnlyList`1" 방지).
        if (TryGetListElementType(underlying, out var element))
            return $"list<{FriendlyTypeName(element)}>";

        return underlying switch
        {
            _ when underlying == typeof(string)   => "string",
            _ when underlying == typeof(int)      => "int",
            _ when underlying == typeof(long)     => "long",
            _ when underlying == typeof(bool)     => "bool",
            _ when underlying == typeof(DateTime) => "datetime",
            _ when underlying == typeof(decimal)  => "decimal",
            _ when underlying == typeof(double)   => "double",
            _                                     => underlying.Name,
        };
    }

    private static object? DefaultValueFor(Type t)
    {
        var underlying = Nullable.GetUnderlyingType(t) ?? t;

        // Dictionary 는 null 대신 빈 오브젝트({}) 를 스켈레톤에 넣어 "key/value 를 채워라" 를 명시한다.
        if (TryGetDictionaryTypes(underlying, out _, out _))
            return new Dictionary<string, object?>();

        // 목록은 null 대신 빈 배열([]) 을 넣어 "배열로 채워라" 를 명시한다.
        if (TryGetListElementType(underlying, out _))
            return Array.Empty<object>();

        return underlying switch
        {
            _ when underlying == typeof(string)   => "",
            _ when underlying == typeof(int)      => 0,
            _ when underlying == typeof(long)     => 0L,
            _ when underlying == typeof(bool)     => false,
            _ when underlying == typeof(DateTime) => null,
            _ when underlying == typeof(decimal)  => 0m,
            _ when underlying == typeof(double)   => 0d,
            _                                     => null,
        };
    }

    /// <summary>
    /// <paramref name="t"/> 가 <see cref="IDictionary{TKey,TValue}"/> / <see cref="IReadOnlyDictionary{TKey,TValue}"/>
    /// 를 구현(또는 그 자체)하면 key/value 타입을 넘겨주고 true. 아니면 false.
    /// </summary>
    private static bool TryGetDictionaryTypes(Type t, out Type key, out Type value)
    {
        var dict = new[] { t }.Concat(t.GetInterfaces())
            .FirstOrDefault(i => i.IsGenericType &&
                (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                 i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));

        if (dict is null)
        {
            key = value = typeof(object);
            return false;
        }

        var args = dict.GetGenericArguments();
        key   = args[0];
        value = args[1];
        return true;
    }

    /// <summary>
    /// <paramref name="t"/> 가 <see cref="IEnumerable{T}"/> 를 구현(또는 그 자체)하는 목록이면 원소 타입을 넘겨주고 true.
    /// string(문자의 목록)과 Dictionary(키·값 쌍의 목록)는 목록이 아니라 각자의 표기를 쓰므로 false.
    /// </summary>
    private static bool TryGetListElementType(Type t, out Type element)
    {
        element = typeof(object);
        if (t == typeof(string) || TryGetDictionaryTypes(t, out _, out _)) return false;

        var seq = new[] { t }.Concat(t.GetInterfaces())
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        if (seq is null) return false;

        element = seq.GetGenericArguments()[0];
        return true;
    }
}
