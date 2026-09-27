// -----------------------------------------------------------------------------
// Horizun Revit MCP - the energy analytical model that horizun_export format=gbxml and
// horizun_code_check operation=energy_readiness both build. Original Horizun code.
//
// ONE BUILD FOR BOTH, so the readiness check reads the same model the gbXML export
// writes. It must run inside the CALLER'S open transaction, which the caller ALWAYS
// rolls back: the existing main model is deleted first (so its type cannot mismatch
// the export), and the energy-settings change is undone by the same rollback.
// -----------------------------------------------------------------------------
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;

namespace Horizun.Revit.Commands
{
    internal static class EnergyModelBuild
    {
        internal const string Description = "rooms/spaces (SpatialElement), second-level boundaries";

        internal static EnergyAnalysisDetailModel CreateSpatial(Document doc)
        {
            EnergyAnalysisDetailModel current = EnergyAnalysisDetailModel.GetMainEnergyAnalysisDetailModel(doc);
            if (current != null) doc.Delete(current.Id);
            // EVERY year, inside the caller's rolled-back transaction: the document's energy
            // settings say rooms/spaces. 2027 builds the model from them, and 2026's gbXML
            // export defaults ExportEnergyModelType to AnalysisMode (RevitAPI.xml 2026:
            // "Default value is AnalysisMode"; 2023: SpatialElement), which follows them - left
            // at building elements, the export would not match the SpatialElement model built
            // here, and RevitAPI.xml says a mismatched export fails.
#if REVIT2023 || REVIT2024 || REVIT2025
            // GetEnergyDataSettings exists from 2026 only (RevitAPI.xml); GetFromDocument in every year.
            EnergyDataSettings.GetFromDocument(doc).AnalysisType = AnalysisMode.RoomsOrSpaces;
#else
            EnergyDataSettings.GetEnergyDataSettings(doc).AnalysisType = AnalysisMode.RoomsOrSpaces;
#endif
#if REVIT2027
            // 2027 deprecates the options overload: the model follows the settings set above.
            return EnergyAnalysisDetailModel.Create(doc);
#else
            return EnergyAnalysisDetailModel.Create(doc, new EnergyAnalysisDetailModelOptions
            {
                EnergyModelType = EnergyModelType.SpatialElement, Tier = EnergyAnalysisDetailModelTier.SecondLevelBoundaries
            });
#endif
        }
    }
}
