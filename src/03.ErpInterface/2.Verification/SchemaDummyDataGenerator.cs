using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Portfolio.ErpInterface.Verification
{
    /// <summary>
    /// [담당업무 2] 스키마 기반 더미데이터 생성기.
    ///
    /// 상대 시스템(ERP) 준비가 늦어져도 우리 쪽 24개 인터페이스는 먼저 검증해야 했다.
    /// 요청 DTO 타입을 리플렉션으로 읽어 유효 전문을 만든다. 핵심은 "아무 값이나" 가 아니라
    /// **실제 마스터에 존재하는 코드만 샘플링** 한다는 점 — 브랜드·SKU·창고·배송 코드가 진짜라서,
    /// 생성된 전문은 검증기를 통과하고 처리 로직까지 실제로 태울 수 있다.
    /// </summary>
    public sealed class SchemaDummyDataGenerator
    {
        private readonly IMasterCodeSampler _master;
        private readonly Random _rng = new Random();

        // 필드명 → 마스터 샘플러. 이름 규약으로 매핑되므로 DTO 가 늘어도 생성기는 안 바뀐다.
        private readonly Dictionary<string, Func<string>> _codeFields;

        public SchemaDummyDataGenerator(IMasterCodeSampler master)
        {
            _master = master;
            _codeFields = new Dictionary<string, Func<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["BrandCode"] = () => _master.Sample("BRAND"),
                ["Sku"] = () => _master.Sample("ITEM"),
                ["WarehouseCode"] = () => _master.Sample("WAREHOUSE"),
                ["ShipMethod"] = () => _master.Sample("SHIP_METHOD"),
                ["CustomerCode"] = () => _master.Sample("CUSTOMER"),
                ["OrderNo"] = () => "DMY" + DateTime.Now.ToString("yyMMddHHmmss") + _rng.Next(100, 999),
                ["TransactionId"] = () => Guid.NewGuid().ToString("N"),
                ["InterfaceVersion"] = () => "1.2",
            };
        }

        public T Generate<T>(int listSize = 3) where T : new() => (T)Generate(typeof(T), listSize, depth: 0);

        private object Generate(Type type, int listSize, int depth)
        {
            if (depth > 6) return null;
            var obj = Activator.CreateInstance(type);

            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
            {
                object value;
                if (_codeFields.TryGetValue(p.Name, out var sampler) && p.PropertyType == typeof(string))
                    value = sampler();                                             // ← 실제 마스터 코드
                else if (p.PropertyType == typeof(string))
                    value = p.Name + "_" + _rng.Next(1000);
                else if (p.PropertyType == typeof(int))
                    value = p.Name.EndsWith("Qty") ? _rng.Next(1, 20) : _rng.Next(1, 100);
                else if (p.PropertyType == typeof(decimal))
                    value = (decimal)_rng.Next(1000, 99000);
                else if (p.PropertyType == typeof(DateTime))
                    value = DateTime.Now.AddHours(-_rng.Next(0, 72));
                else if (p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    var elem = p.PropertyType.GetGenericArguments()[0];
                    var list = (IList)Activator.CreateInstance(p.PropertyType);
                    for (var i = 0; i < listSize; i++) list.Add(elem == typeof(string) ? _codeFields["Sku"]() : Generate(elem, listSize, depth + 1));
                    value = list;
                }
                else if (p.PropertyType.IsClass)
                    value = Generate(p.PropertyType, listSize, depth + 1);
                else continue;

                p.SetValue(obj, value);
            }
            return obj;
        }
    }

    /// <summary>마스터 테이블에서 실존 코드를 무작위 샘플링. 캐시 후 TOP N ORDER BY NEWID().</summary>
    public interface IMasterCodeSampler { string Sample(string masterKind); }
}
