// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// The owner's switch for horizun_document_session operation=sync_with_central.
//
// Gated the way horizun_execute_python is: OFF on a fresh install, and only a
// person at THIS Revit can turn it on. A synchronize with central publishes the
// local's changes into the file every other person on the project works from and
// hands ownership back to the server; there is no rehearsal and no rollback. So
// the decision lives in a dialog the MCP caller can neither see nor answer, and
// the flag it writes sits in the settings file no MCP call writes.
// -----------------------------------------------------------------------------
using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Revit.Core;
using BridgeSettings = Horizun.Revit.Core.Settings;

namespace Horizun.Revit
{
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class SyncCentralPermissionCommand : IExternalCommand
    {
        internal static string Title(bool es) =>
            RibbonText.T(es, "Sincronizar con el central", "Synchronize with central");

        internal static string Detail(bool es) =>
            RibbonText.T(es,
                "Si el asistente puede sincronizar un modelo compartido con su central. Publica los cambios para todo el equipo y no se puede deshacer.",
                "Whether the assistant may synchronize a shared model with its central. It publishes the changes to the whole team and cannot be undone.");

        internal static string State(bool es, bool enabled) =>
            enabled ? RibbonText.T(es, "permitido", "allowed") : RibbonText.T(es, "no permitido (por defecto)", "not allowed (default)");

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                bool es = RibbonText.IsSpanish(data);
                bool enabling = !BridgeSettings.SyncWithCentralOwnerEnabled;
                var dialog = new TaskDialog("Horizun — " + Title(es))
                {
                    MainInstruction = enabling
                        ? RibbonText.T(es, "¿Permitir que el asistente sincronice con el central?", "Allow the assistant to synchronize with central?")
                        : RibbonText.T(es, "El asistente dejará de poder sincronizar con el central.", "The assistant will no longer be able to synchronize with central."),
                    // The protection and the profile are named by their own row titles, so the
                    // owner can find the controls this text talks about.
                    MainContent = enabling
                        ? RibbonText.T(es,
                            "Sincronizar publica los cambios de tu local en el archivo central que usa todo el equipo y devuelve lo que pidas de tus subproyectos y elementos prestados. No hay ensayo ni deshacer: el asistente te mostrará una ESTIMACIÓN y necesitará confirmarla. Nunca sincroniza copias desvinculadas, ni modelos bajo «" + RibbonText.CentralTitle(true) + "». Para la operación tipada, «" + RibbonText.ModeTitle(true) + "» debe permitir abrir y cerrar documentos (full_write). Queda activo hasta que lo apagues aquí.",
                            "Synchronizing publishes your local's changes into the central file the whole team uses and gives back what you choose of your worksets and borrowed elements. There is no rehearsal and no undo: the assistant shows an ESTIMATE and must confirm it. It never synchronizes detached copies, nor models under \"" + RibbonText.CentralTitle(false) + "\". For the typed operation, \"" + RibbonText.ModeTitle(false) + "\" must allow opening and closing documents (full_write). It stays on until you turn it off here.")
                        : RibbonText.T(es,
                            "Cualquier sincronización que pida el asistente se rechazará, también la de un script de Python (o de uno de sus includes) que nombre SynchronizeWithCentral o un comando Synchronize: se revisa por su texto, así que un nombre armado en tiempo de ejecución no se detecta; no es un aislamiento. Tu propio «Sincronizar con el central» de Revit no cambia.",
                            "Any synchronize the assistant asks for will be refused, including a Python script (or one of its includes) that names SynchronizeWithCentral or a Synchronize command: it is checked by its text, so a name assembled at runtime is not caught; it is not a sandbox. Revit's own Synchronize with Central is unchanged."),
                    CommonButtons = TaskDialogCommonButtons.Cancel
                };
                dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                    enabling ? RibbonText.T(es, "Permitir", "Allow") : RibbonText.T(es, "No permitir", "Do not allow"));
                using (Interference.WithDialogAnswer(DialogAnswer.Human))
                    if (dialog.Show() != TaskDialogResult.CommandLink1) return Result.Cancelled;
                if (!BridgeSettings.TrySetSyncWithCentralOwnerGrant(enabling, out string error))
                {
                    message = error;
                    return Result.Failed;
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
