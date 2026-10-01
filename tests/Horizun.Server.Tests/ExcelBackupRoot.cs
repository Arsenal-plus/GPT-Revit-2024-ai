// -----------------------------------------------------------------------------
// Horizun Server tests - a Horizun data root that lives in the test's own temp dir.
//
// horizun_excel_write_rows now copies the pre-append workbook into the Horizun
// state folder (backups\excel under the data root) instead of beside the user's
// file. A test suite has no business filling the machine's real
// %USERPROFILE%\.horizun with backups of throwaway workbooks, so every class that
// appends to an existing workbook points the data root at a private directory for
// its lifetime and deletes it afterwards. The suite runs serially (Parallelism.cs),
// which is what makes moving the process-wide variable safe.
// -----------------------------------------------------------------------------
using System;
using System.IO;
using Horizun.Revit.Core;

namespace Horizun.Server.Tests
{
    internal sealed class ExcelBackupRoot : IDisposable
    {
        private readonly string _saved;
        internal string Root { get; }

        internal ExcelBackupRoot()
        {
            _saved = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            Root = Path.Combine(Path.GetTempPath(), "hz-xls-root-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, Root);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _saved);
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
