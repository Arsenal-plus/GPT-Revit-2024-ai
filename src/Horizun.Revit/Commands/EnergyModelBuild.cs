// -----------------------------------------------------------------------------
// Horizun Revit MCP - the energy analytical model that horizun_export format=gbxml and
// horizun_code_check operation=energy_readiness both build. Original Horizun code.
//
// ONE BUILD FOR BOTH, so the readiness check reads the same model the gbXML export
// writes. It must run inside the CALLER'S open transaction, which the caller ALWAYS
// rolls back: the existing main model is deleted first (so its type cannot mismatch
// the export), and the energy-settings change is undone by the same rollback.
//
// TIER FINAL, Revit's documented default (RevitAPI.xml EnergyAnalysisDetailModelOptions.Tier:
// "The default value is EnergyAnalysisModelTier::Final"). Only Final computes
// "Constructions, schedules, non-graphical data"; SecondLevelBoundaries is "Analytical
// surfaces." alone, so a construction read on it would find none and the gbXML would be
// written below Revit's own default. 2027's Create(doc) takes no options: the tier is what
// Revit builds, so callers read model.Tier BACK (IsFinal) instead of assuming it.
//
// SCOPE: with AnalysisType = RoomsOrSpaces the model is built from the settings'
// ExportCategory (rooms or MEP spaces; RevitAPI.xml 2027: "only applies if AnalysisType is
// RoomsOrSpaces") in the settings' ProjectPhase ("The project phase of the EnergyData
// information"). Scope/InPhase let callers count and judge only what the model can hold.
// -----------------------------------------------------------------------------
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    internal static class EnergyModelBuild
    {
        internal const string Description = "rooms/spaces (SpatialElement) at tier Final, the tier that computes constructions";

        internal static EnergyDataSettings Settings(Document doc)
        {
#if REVIT2023 || REVIT2024 || REVIT2025
            // GetEnergyDataSettings exists from 2026 only (RevitAPI.xml); GetFromDocument in every year.
            return EnergyDataSettings.GetFromDocument(doc);
#else
            return EnergyDataSettings.GetEnergyDataSettings(doc);
#endif
        }

        /// <summary>The export category (OST_Rooms or OST_MEPSpaces) and the phase the model is built from; null / invalid when unreadable.</summary>
        internal static void Scope(Document doc, out BuiltInCategory? category, out ElementId phase)
        {
            category = null;
            phase = ElementId.InvalidElementId;
            EnergyDataSettings settings = null;
            try { settings = Settings(doc); } catch { }
            if (settings == null) return;
            // ExportCategory is the category's ElementId (OST_Rooms or OST_MEPSpaces).
            try { ElementId id = settings.ExportCategory; if (id != null && id != ElementId.InvalidElementId) category = (BuiltInCategory)Rid.Value(id); } catch { }
            try { phase = settings.ProjectPhase ?? ElementId.InvalidElementId; } catch { }
        }

        /// <summary>
        /// True unless the element's own phase is readable and differs from <paramref name="phase"/>:
        /// an unreadable phase is never grounds to exclude.
        /// </summary>
        internal static bool InPhase(Element e, ElementId phase)
        {
            if (phase == null || phase == ElementId.InvalidElementId) return true;
            ElementId own = null;
            try { own = e.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId(); } catch { }
            return own == null || own == ElementId.InvalidElementId || own == phase;
        }

        internal static string ScopeText(Document doc, BuiltInCategory? category, ElementId phase)
        {
            string what = category == BuiltInCategory.OST_Rooms ? "rooms" : category == BuiltInCategory.OST_MEPSpaces ? "MEP spaces" : "rooms or MEP spaces";
            if (phase == null || phase == ElementId.InvalidElementId) return what + " of every phase (the energy settings' phase is unreadable)";
            string name = null;
            try { name = doc.GetElement(phase)?.Name; } catch { }
            return what + " of phase '" + (name ?? phase.ToString()) + "'";
        }

        internal static bool IsFinal(EnergyAnalysisDetailModel model)
        {
            try { return model.Tier == EnergyAnalysisDetailModelTier.Final; } catch { return false; }
        }

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
            Settings(doc).AnalysisType = AnalysisMode.RoomsOrSpaces;
#if REVIT2027
            // 2027 deprecates the options overload: the model follows the settings set above.
            return EnergyAnalysisDetailModel.Create(doc);
#else
            return EnergyAnalysisDetailModel.Create(doc, new EnergyAnalysisDetailModelOptions
            {
                EnergyModelType = EnergyModelType.SpatialElement, Tier = EnergyAnalysisDetailModelTier.Final
            });
#endif
        }
    }
}
