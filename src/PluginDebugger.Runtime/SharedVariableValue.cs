using System;
using System.Globalization;

namespace PluginDebugger.Runtime
{
    /// <summary>The primitive types a SharedVariables entry can hold (requirements FR-4.4 "typed where applicable").</summary>
    public enum SharedVariableType
    {
        String,
        WholeNumber,
        Boolean,
        Decimal,
        Double,
        DateTime,
        Guid
    }

    /// <summary>
    /// Parses a SharedVariables entry's text into a boxed primitive of the chosen type. The boxed
    /// value is then serialized as <c>object</c> (see <see cref="SdkXml"/>) so it round-trips into
    /// the child domain's <c>context.SharedVariables</c> with its runtime type intact.
    /// </summary>
    public static class SharedVariableValue
    {
        public static readonly string[] TypeNames = Enum.GetNames(typeof(SharedVariableType));

        public static bool TryParseType(string name, out SharedVariableType type) =>
            Enum.TryParse(name, out type);

        public static object Parse(SharedVariableType type, string text)
        {
            text = (text ?? string.Empty).Trim();
            switch (type)
            {
                case SharedVariableType.String:
                    return text;
                case SharedVariableType.WholeNumber:
                    return int.Parse(text, CultureInfo.InvariantCulture);
                case SharedVariableType.Boolean:
                    return bool.Parse(text);
                case SharedVariableType.Decimal:
                    return decimal.Parse(text, CultureInfo.InvariantCulture);
                case SharedVariableType.Double:
                    return double.Parse(text, CultureInfo.InvariantCulture);
                case SharedVariableType.DateTime:
                    return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                case SharedVariableType.Guid:
                    return Guid.Parse(text);
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }
        }
    }
}
