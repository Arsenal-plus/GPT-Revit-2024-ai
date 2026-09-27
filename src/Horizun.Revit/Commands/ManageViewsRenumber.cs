// -----------------------------------------------------------------------------
// Horizun - original Horizun code.
//
// horizun_manage_views operation=renumber_sheets: a map old sheet number -> new
// sheet number written as ONE action of the batch's single transaction.
//
// Why it is not horizun_fix_planimetry's set_sheet_number: that one corrects a
// FINDING and refuses a number another sheet holds, which is exactly what a
// register-wide renumbering (a swap, a shift of a whole series) needs to do.
// The ordering and the temporary numbers are decided by the pure
// SheetRenumberRules; this file only reads the document's sheets, writes the
// planned steps and re-reads every sheet's number.
//
//   * The collision check runs against EVERY ViewSheet (placeholders included:
//     they hold numbers too). A sheet whose number cannot be read refuses the
//     action rather than being skipped - a check that skipped one is not a check.
//   * Numbers other actions of the same batch create are refused as targets, and
//     the targets are reserved so a later create in the batch cannot take them.
//     Numbers the map FREES are not offered to later creates in the same batch:
//     they are checked against the document as it was. One renumber_sheets per
//     batch, because a second would be planned against a state the first changes.
//   * The resolved plan binds every sheet's id and number, so a sheet renumbered
//     by someone between rehearsal and apply refuses the token as stale.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class ManageViewsCommand
    {
        private const string RenumberOnceKey = "renumber-sheets:once";
        private const string SheetNumberReservation = "sheet-number:";

        internal static bool IsRenumberOperation(string op) => string.Equals(op, "renumber_sheets", StringComparison.OrdinalIgnoreCase);

        private static List<KeyValuePair<string, string>> ReadRenumberMap(JObject a)
        {
            if (!(a["renumber"] is JObject map) || !map.HasValues)
                throw new ArgumentException("renumber_sheets needs renumber: an object of old sheet number -> new sheet number.");
            var list = new List<KeyValuePair<string, string>>();
            foreach (JProperty p in map.Properties())
            {
                if (p.Value.Type != JTokenType.String)
                    throw new ArgumentException("renumber['" + p.Name + "'] must be the new sheet number as a string.");
                list.Add(new KeyValuePair<string, string>(p.Name, p.Value.Value<string>()));
            }
            return list;
        }

        /// <summary>Every sheet of the document by number; an unreadable number refuses.</summary>
        private static Dictionary<string, ViewSheet> SheetsByNumber(Document doc)
        {
            var byNumber = new Dictionary<string, ViewSheet>(SheetRenumberRules.Numbers);
            foreach (ViewSheet sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            {
                string number;
                try { number = sheet.SheetNumber; }
                catch (Exception ex)
                {
                    throw new ArgumentException("the number of sheet " + Rid.Value(sheet.Id) + " could not be read (" + ex.Message +
                        "); a collision check that skipped it would not be a check. Nothing was written.");
                }
                if (number != null && !byNumber.ContainsKey(number)) byNumber[number] = sheet;
            }
            return byNumber;
        }

        internal static void ValidateRenumber(Document doc, JObject a, Dictionary<string, Type> known)
        {
            if (known.ContainsKey(RenumberOnceKey))
                throw new ArgumentException("only one renumber_sheets per batch: a second map would be planned against " +
                                            "numbers the first one changes. Merge the maps into one.");
            List<KeyValuePair<string, string>> map = ReadRenumberMap(a);
            var reserved = known.Keys.Where(k => k.StartsWith(SheetNumberReservation, StringComparison.Ordinal))
                                     .Select(k => k.Substring(SheetNumberReservation.Length)).ToList();
            SheetRenumberPlan plan = SheetRenumberRules.Plan(map, SheetsByNumber(doc).Keys, reserved);
            known.Add(RenumberOnceKey, typeof(ViewSheet));
            foreach (var kv in plan.Final)
            {
                string key = SheetNumberReservation + kv.Value.ToLowerInvariant();
                if (!known.ContainsKey(key)) known.Add(key, typeof(ViewSheet));
            }
        }

        /// <summary>The rehearsal's view of the plan: which sheet goes where, in write order.</summary>
        internal static JObject RenumberPreview(Document doc, JObject a)
        {
            var sheets = SheetsByNumber(doc);
            SheetRenumberPlan plan = SheetRenumberRules.Plan(ReadRenumberMap(a), sheets.Keys);
            return new JObject
            {
                ["final"] = new JArray(plan.Final.Select(kv => new JObject
                    { ["sheet_id"] = Rid.Value(sheets[kv.Key].Id), ["from"] = kv.Key, ["to"] = kv.Value })),
                ["steps"] = new JArray(plan.Steps.Select(s => new JObject
                    { ["sheet_id"] = Rid.Value(sheets[s.Sheet].Id), ["from"] = s.From, ["to"] = s.To, ["temporary"] = s.Temporary })),
                ["temporary_steps"] = plan.TemporarySteps,
                ["unchanged"] = new JArray(plan.Unchanged)
            };
        }

        /// <summary>
        /// Binds the whole register into the resolved plan: the collision check is only
        /// true of the numbers it read, so any sheet renumbered since refuses as stale.
        /// </summary>
        internal static void BindRenumber(Document doc, IDictionary<string, string> before)
        {
            var lines = new List<string>();
            foreach (ViewSheet sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
            {
                string number; try { number = sheet.SheetNumber; } catch { number = "<unreadable>"; }
                lines.Add(sheet.UniqueId + "=" + number);
            }
            lines.Sort(StringComparer.Ordinal);
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
                before["sheet_register_sha256"] = BitConverter.ToString(digest).Replace("-", "").ToLowerInvariant();
            }
            before["sheet_count"] = lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static Element ApplyRenumber(Document doc, JObject a)
        {
            List<KeyValuePair<string, string>> map = ReadRenumberMap(a);
            var sheets = SheetsByNumber(doc);
            // Planned again against the live document: sheets an earlier action of this
            // batch created now hold their numbers for real, which validation reserved.
            SheetRenumberPlan plan = SheetRenumberRules.Plan(map, sheets.Keys);
            foreach (SheetRenumberStep step in plan.Steps) sheets[step.Sheet].SheetNumber = step.To;
            a["__renumbered"] = new JArray(plan.Final.Select(kv => new JObject
                { ["sheet_id"] = Rid.Value(sheets[kv.Key].Id), ["from"] = kv.Key, ["to"] = kv.Value }));
            a["__unchanged"] = new JArray(plan.Unchanged.Select(n => new JObject
                { ["sheet_id"] = Rid.Value(sheets[n].Id), ["number"] = sheets[n].SheetNumber }));
            a["__temporary_steps"] = plan.TemporarySteps;
            return plan.Final.Count > 0 ? sheets[plan.Final[0].Key] : sheets[map[0].Key];
        }

        /// <summary>Every renumbered sheet reads back its new number exactly; every unchanged one its old.</summary>
        internal static bool VerifyRenumber(Document doc, JObject a)
        {
            if (!(a["__renumbered"] is JArray moved) || !(a["__unchanged"] is JArray kept)) return false;
            bool ok = true;
            foreach (JObject row in moved.Concat(kept).Cast<JObject>())
            {
                string wanted = row.Value<string>(row["to"] != null ? "to" : "number");
                string now;
                try { now = (doc.GetElement(Rid.Make(row.Value<long>("sheet_id"))) as ViewSheet)?.SheetNumber; }
                catch { now = null; }
                row["reread"] = now;
                row["verified"] = string.Equals(now, wanted, StringComparison.Ordinal);
                if (!string.Equals(now, wanted, StringComparison.Ordinal)) ok = false;
            }
            return ok;
        }

        internal static JObject RenumberDetail(JObject a) => new JObject
        {
            ["renumbered"] = a["__renumbered"], ["unchanged"] = a["__unchanged"], ["temporary_steps"] = a["__temporary_steps"]
        };
    }
}
