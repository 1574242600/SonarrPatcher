using System;
using System.Reflection;

namespace SonarrPatcher.Common
{
    /// <summary>
    /// Reflection helpers shared (source-linked) by every hook assembly. Pure
    /// <see cref="System.Reflection"/> — no dependency on Harmony or Sonarr, so any
    /// project that links the Common sources can use them.
    /// <para>
    /// The "Require*" members collapse the repeated
    /// <c>resolve-or-throw-InvalidOperationException</c> pattern used across the
    /// patches, keeping the exact exception message the caller asks for.
    /// </para>
    /// </summary>
    internal static class ReflectionHelper
    {
        /// <summary>
        /// Returns the resolved type, or throws with a message naming it. Callers pass
        /// the result of their own lookup (e.g. <c>AccessTools.TypeByName</c>) so no
        /// dependency on the lookup strategy is introduced here.
        /// </summary>
        internal static Type RequireType(Type type, string typeName)
        {
            return type ?? throw new InvalidOperationException(typeName + " type not found");
        }

        /// <summary>
        /// Returns the resolved constructor, or throws with a message naming it.
        /// </summary>
        internal static ConstructorInfo RequireConstructor(ConstructorInfo constructor, string description)
        {
            return constructor ?? throw new InvalidOperationException(description + " not found");
        }

        /// <summary>
        /// Returns the resolved method, or throws with a message naming it.
        /// </summary>
        internal static MethodInfo RequireMethod(MethodInfo method, string description)
        {
            return method ?? throw new InvalidOperationException(description + " not found");
        }

        /// <summary>
        /// Reads an instance field (public or non-public) reflectively, throwing with a
        /// precise message when the field does not exist instead of returning null.
        /// </summary>
        internal static object GetInstanceField(object instance, string fieldName)
        {
            var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
            {
                throw new InvalidOperationException(instance.GetType().Name + "." + fieldName + " not found");
            }

            return field.GetValue(instance);
        }

        /// <summary>
        /// Non-throwing variant of <see cref="GetInstanceField"/>: returns false when the
        /// field does not exist, for callers that degrade gracefully (e.g. warn + skip).
        /// </summary>
        internal static bool TryGetInstanceField(object instance, string fieldName, out object value)
        {
            var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
            {
                value = null;
                return false;
            }

            value = field.GetValue(instance);
            return true;
        }
    }
}
