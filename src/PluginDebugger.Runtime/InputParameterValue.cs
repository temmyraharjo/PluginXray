using System;
using System.Globalization;
using Microsoft.Xrm.Sdk;

namespace PluginDebugger.Runtime
{
    /// <summary>
    /// The types an arbitrary <c>InputParameters</c> entry can hold (requirements FR-4.6). A
    /// superset of <see cref="SharedVariableType"/>: the scalar primitives plus the SDK types
    /// commonly passed as message parameters (Money, OptionSetValue, EntityReference).
    /// </summary>
    public enum InputParameterType
    {
        String,
        WholeNumber,
        Boolean,
        Decimal,
        Double,
        DateTime,
        Guid,
        Money,
        OptionSetValue,
        EntityReference
    }

    /// <summary>
    /// Parses an <c>InputParameters</c> entry's text into a boxed value of the chosen type. The
    /// boxed value is serialized as <c>object</c> (see <see cref="SdkXml"/>) so it round-trips into
    /// the child domain's <c>context.InputParameters</c> with its runtime type intact — mirroring
    /// how <see cref="SharedVariableValue"/> handles SharedVariables.
    /// </summary>
    public static class InputParameterValue
    {
        public static readonly string[] TypeNames = Enum.GetNames(typeof(InputParameterType));

        public static bool TryParseType(string name, out InputParameterType type) =>
            Enum.TryParse(name, out type);

        public static object Parse(InputParameterType type, string text)
        {
            text = (text ?? string.Empty).Trim();
            switch (type)
            {
                case InputParameterType.String:
                    return text;
                case InputParameterType.WholeNumber:
                    return int.Parse(text, CultureInfo.InvariantCulture);
                case InputParameterType.Boolean:
                    return bool.Parse(text);
                case InputParameterType.Decimal:
                    return decimal.Parse(text, CultureInfo.InvariantCulture);
                case InputParameterType.Double:
                    return double.Parse(text, CultureInfo.InvariantCulture);
                case InputParameterType.DateTime:
                    return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                case InputParameterType.Guid:
                    return Guid.Parse(text);
                case InputParameterType.Money:
                    return new Money(decimal.Parse(text, CultureInfo.InvariantCulture));
                case InputParameterType.OptionSetValue:
                    return new OptionSetValue(int.Parse(text, CultureInfo.InvariantCulture));
                case InputParameterType.EntityReference:
                    return ParseEntityReference(text);
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }
        }

        /// <summary>Parses the <c>logicalname:guid</c> form into an <see cref="EntityReference"/>.</summary>
        private static EntityReference ParseEntityReference(string text)
        {
            var separator = text.IndexOf(':');
            if (separator <= 0 || separator == text.Length - 1)
            {
                throw new FormatException("expected 'entitylogicalname:guid' (e.g. account:00000000-0000-0000-0000-000000000000).");
            }

            var logicalName = text.Substring(0, separator).Trim();
            var idText = text.Substring(separator + 1).Trim();
            if (!Guid.TryParse(idText, out var id))
            {
                throw new FormatException($"'{idText}' is not a valid Guid.");
            }

            return new EntityReference(logicalName, id);
        }
    }
}
