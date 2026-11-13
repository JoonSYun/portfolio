using System.Reflection;

namespace Portfolio.WmsOrchestration.Infrastructure
{
    public static class ReflectionHelper
    {
        

        /// <summary>
        /// Gets the domain type by name.
        /// Only types decorated with <see cref="MassTransitAutoBindAttribute"/> are considered.
        /// </summary>
        /// <param name="typeName"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public static Type GetDomainType(string typeName)
        {
            var domainType = GetDomainTypes()
                            .FirstOrDefault(t => t.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase));
            if (domainType == null)
            {
                throw new InvalidOperationException($"Domain type '{typeName}' not found.");
            }
            return domainType;
        }

        /// <summary>
        /// Gets all domain types in the current AppDomain.
        /// Only types decorated with <see cref="MassTransitAutoBindAttribute"/> are considered.
        /// </summary>
        /// <returns></returns>
        public static List<Type> GetDomainTypes()
        {
            return AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(a => a.FullName.StartsWith("Portfolio."))
                .SelectMany(a =>
                {
                    try { return a.GetTypes(); }
                    catch { return Type.EmptyTypes; }
                })
                //.Where(t =>
                //    t.GetCustomAttribute<MassTransitAutoBindAttribute>() != null)
                .ToList();
        }
    }
}
