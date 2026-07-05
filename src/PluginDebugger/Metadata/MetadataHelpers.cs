using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk.Metadata;

namespace PluginDebugger.Metadata
{
    /// <summary>One choice in an option set, flattened for binding to combos/check-lists.</summary>
    internal sealed class OptionChoice
    {
        public OptionChoice(int value, string label)
        {
            Value = value;
            Label = label;
        }

        public int Value { get; }
        public string Label { get; }

        public override string ToString() => $"{Label} ({Value})";
    }

    internal static class MetadataHelpers
    {
        public static string DisplayName(AttributeMetadata attr)
        {
            var display = attr.DisplayName?.UserLocalizedLabel?.Label;
            return string.IsNullOrEmpty(display) ? attr.LogicalName : $"{display} [{attr.LogicalName}]";
        }

        /// <summary>The choices for picklist / state / status / multi-select attributes.</summary>
        public static IReadOnlyList<OptionChoice> GetOptions(AttributeMetadata attr)
        {
            OptionSetMetadataBase optionSet = null;
            switch (attr)
            {
                case MultiSelectPicklistAttributeMetadata m: optionSet = m.OptionSet; break;
                case PicklistAttributeMetadata p: optionSet = p.OptionSet; break;
                case StateAttributeMetadata s: optionSet = s.OptionSet; break;
                case StatusAttributeMetadata st: optionSet = st.OptionSet; break;
            }

            if (optionSet is OptionSetMetadata osm && osm.Options != null)
            {
                return osm.Options
                    .Select(o => new OptionChoice(
                        o.Value ?? 0,
                        o.Label?.UserLocalizedLabel?.Label ?? (o.Value?.ToString() ?? "?")))
                    .ToList();
            }

            return new List<OptionChoice>();
        }

        /// <summary>Target entities for a lookup; more than one means it is polymorphic (FR-5.3).</summary>
        public static string[] GetLookupTargets(AttributeMetadata attr)
        {
            return (attr as LookupAttributeMetadata)?.Targets ?? new string[0];
        }
    }
}
