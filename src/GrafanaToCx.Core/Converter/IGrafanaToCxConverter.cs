using Newtonsoft.Json.Linq;

namespace GrafanaToCx.Core.Converter;

public interface IGrafanaToCxConverter
{
    string Convert(string grafanaJson, ConversionOptions? options = null);
    JObject ConvertToJObject(string grafanaJson, ConversionOptions? options = null);
    IReadOnlyList<PanelConversionDiagnostic> ConversionDiagnostics { get; }

    /// <summary>
    /// Losses that are not panels: annotations, links, repeats, transformations, variables.
    /// </summary>
    IReadOnlyList<DashboardConversionDiagnostic> DashboardDiagnostics { get; }

    IReadOnlyList<JObject> ConversionDecisionEvents { get; }
}
