using MassTransit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    public class ExchangePrefixFormatter : IEntityNameFormatter
    {
        private readonly string _prefix;

        public ExchangePrefixFormatter(string prefix = "exchange")
        {
            _prefix = prefix;
        }

        public string FormatEntityName<T>()
        {
            var type = typeof(T);
            var className = type.Name;

            // Generic 타입 처리
            if (type.IsGenericType)
            {
                // ChunkPayload_Message`1 에서 base name 가져오기
                var baseName = className.Substring(0, className.IndexOf("`"));

                // Generic 인자 가져오기
                var genericArgs = type.GetGenericArguments();
                var genericArgName = genericArgs[0].Name;

                // underscore를 제거
                baseName = baseName.Replace("_", "");

                // exchange-ChunkPayloadMessage-ProductsReq
                return $"{_prefix}-{baseName}-{genericArgName}";
            }

            // underscore를 제거만 함 (PascalCase 유지)
            var cleanName = className.Replace("_", "");

            // exchange-ChunkSuccessEvent
            return $"{_prefix}-{cleanName}";
        }
    }
}
