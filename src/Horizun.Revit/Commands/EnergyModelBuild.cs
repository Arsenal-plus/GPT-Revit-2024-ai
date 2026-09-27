// -----------------------------------------------------------------------------
// Horizun Revit MCP - the energy analytical model that horizun_export format=gbxml and
// horizun_code_check operation=energy_readiness both build. Original Horizun code.
//
// ONE BUILD FOR BOTH, so the readiness check reads the same model the gbXML export
// writes. It must run inside the CALLER'S open transaction, which the caller ALWAYS
// rolls back: the existing main model is deleted first (so its type cannot mismatch
// the export), and 2027's settings change is undone by the same rollback.
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
#if REVIT2027
            // 2027 deprecates the options overload: the model follows the document's energy
            // settings, set here to rooms/spaces inside the caller's rolled-back transaction.
            EnergyDataSettings.GetEnergyDataSettings(doc).AnalysisType = AnalysisMode.RoomsOrSpaces;
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
