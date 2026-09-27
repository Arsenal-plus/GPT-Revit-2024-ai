// -----------------------------------------------------------------------------
// Horizun Revit MCP - horizun_federation_check, rule levels_match. Original Horizun
// code. READ-ONLY.
//
// Reads the host's levels and every loaded link's levels, the latter taken into
// host coordinates through the instance's TOTAL transform - the link's own
// placement, not its file's numbers, is what the federation sees. The comparison
// is Core/FederationLevelRules.cs.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class FederationCheckCommand
    {
        private static FederationLevelInput HostLevels(Document doc)
        {
            var input = new FederationLevelInput();
            foreach (Level lv in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
                // ProjectElevation is measured from the internal origin whatever the
                // level's Elevation Base says - the same frame GetTotalTransform maps into.
                input.Host.Add(new FederationLevelFact { Id = Rid.Value(lv.Id), Name = lv.Name, ElevationMm = lv.ProjectElevation * 304.8 });
            return input;
        }

        private static void ReadLinkLevels(RevitLinkInstance inst, Document linkDoc, long instanceId, FederationLevelInput input)
        {
            try
            {
                Transform t = inst.GetTotalTransform();
                var levels = new List<FederationLevelFact>();
                foreach (Level lv in new FilteredElementCollector(linkDoc).OfClass(typeof(Level)).Cast<Level>())
                    levels.Add(new FederationLevelFact
                    {
                        Id = Rid.Value(lv.Id), Name = lv.Name,
                        ElevationMm = t.OfPoint(new XYZ(0, 0, lv.ProjectElevation)).Z * 304.8
                    });
                input.ByLink[instanceId] = levels;
            }
            catch (Exception ex)
            {
                input.WhyNotRead[instanceId] = "the link's levels could not be read: " + ex.Message;
            }
        }
    }
}
