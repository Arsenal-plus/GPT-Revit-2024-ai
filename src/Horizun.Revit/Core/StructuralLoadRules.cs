// -----------------------------------------------------------------------------
// Horizun Revit MCP - how a structural load's case is named in a reply.
// Original Horizun code, no Revit types.
//
// A load read by query_structure mode=loads is grouped by its load case, and the
// case NAME is the key of that group. A load can have no case at all: Revit gives
// a point load created through the API in a document with no load case an invalid
// LoadCaseId and an EMPTY LoadCaseName (MEASURED 2026-09-27 in Revit 2026). Used as
// it came, that name made a JSON property called "" - valid JSON, and a reply
// PowerShell's ConvertFrom-Json refuses whole ("a property whose name is an empty
// string"), so the harness waited out its timeout on an answer it already had.
//
// So a load says which of three things is true: it has a case (named), it has none
// (not a failure to read - a fact about the load), or the name could not be read.
// -----------------------------------------------------------------------------
namespace Horizun.Revit.Core
{
    public static class StructuralLoadRules
    {
        /// <summary>The group of loads with no load case assigned.</summary>
        public const string NoLoadCase = "(no load case)";

        /// <summary>The group of loads whose case name could not be read.</summary>
        public const string UnreadableCase = "(unreadable)";

        /// <summary>
        /// The by_load_case key for one load. <paramref name="assigned"/> is whether the load
        /// has a case (a valid LoadCaseId), null when that could not be read. Never empty.
        /// </summary>
        public static string CaseKey(string name, bool? assigned)
        {
            if (assigned == false) return NoLoadCase;
            if (string.IsNullOrWhiteSpace(name)) return UnreadableCase;
            return name;
        }

        /// <summary>
        /// Whether the case name is a failure to read: a load with a case (or one whose case
        /// could not be told) that gives no name. A load with no case gives none by design.
        /// </summary>
        public static bool CaseNameUnread(string name, bool? assigned)
            => assigned != false && string.IsNullOrWhiteSpace(name);
    }
}
