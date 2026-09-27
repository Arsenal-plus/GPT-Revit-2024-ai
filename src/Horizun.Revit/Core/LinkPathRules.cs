// -----------------------------------------------------------------------------
// Horizun Revit MCP - original Horizun code.
//
// ONE SPELLING OF A LOCAL PATH FOR REVIT'S LINKS. A link created from
// "C:/dir/model.rvt" LOADS - the type reads Loaded and the document is in memory -
// yet RevitLinkInstance.GetLinkDocument() returns null for its instance, so every
// reader of the link (levels, shared coordinates, linked elements) sees it as not
// loaded. The same file given as "C:\dir\model.rvt" links and reads normally.
// MEASURED 2026-09-27 in Revit 2026 with three different source files, and with a
// Python-made link as the control. Local paths are therefore normalised to the
// platform's own separators before they reach ModelPathUtils; server and cloud
// paths ("RSN://", "Autodesk Docs://") are left exactly as given.
// -----------------------------------------------------------------------------
using System;
using System.IO;

namespace Horizun.Revit.Core
{
    public static class LinkPathRules
    {
        /// <summary>The path to hand to Revit: a rooted local path in its full, native form; anything else as given.</summary>
        public static string ForRevit(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            if (path.IndexOf("://", StringComparison.Ordinal) >= 0) return path;
            if (!Path.IsPathRooted(path)) return path;
            try { return Path.GetFullPath(path); }
            catch (Exception) { return path; }
        }
    }
}
