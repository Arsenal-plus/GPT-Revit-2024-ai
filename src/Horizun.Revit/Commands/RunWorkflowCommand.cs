using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Horizun.Contracts;
using Settings = Horizun.Revit.Core.Settings;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace Horizun.Revit.Commands
{
    // One dispatcher turn: other MCP calls cannot interleave with dependent steps.
    // This is a saga, not a cross-document transaction. Completed steps are preserved on failure.
    public sealed class RunWorkflowCommand : ICommand
    {
        private readonly Func<string, ICommand> resolve;
        public RunWorkflowCommand(Func<string, ICommand> resolve) { this.resolve = resolve; }
        public string Name => "horizun_run_workflow";
        public string Description => "Prepare explicit documents and execute a bounded verified workflow.";
        public CommandResult Execute(UIApplication app, string json)
        {
            JObject r = VerifiedModelEdit.Parse(json, out CommandResult parse);
            if (r == null) return parse;
            string invalid = WorkflowRules.Validate(r);
            if (invalid != null) return CommandResult.Fail(invalid);
            var trace = new JArray();
            var results = new Dictionary<string, JToken>(StringComparer.Ordinal);
            int completed = 0;
            bool recovered = false;
            bool stepStarted = false;
            string workflowId = r.Value<string>("idempotency_key");
            string currentStep = null;
            WorkflowJournal journal = null;
            try
            {
                DemandAllowed(Name);
                Document root = FindOpen(app, r.Value<string>("target_document"));
                if (root == null) return Precondition("The target must identify one already-open document.", "identify_document");
                string identity = DocumentGate.IdentityOf(root, app.Application.VersionNumber).Fingerprint();
                var bound = (JObject)r.DeepClone();
                // Bind every currently open document; later-created families are governed by child previews.
                bound["context"] = new JArray(app.Application.Documents.Cast<Document>().Where(d => !d.IsLinked)
                    .OrderBy(d => d.Title, StringComparer.Ordinal).Select(d => new JObject
                    { ["identity"] = DocumentGate.IdentityOf(d, app.Application.VersionNumber).Fingerprint(),
                      ["model_state"] = ModelStateFingerprint.Read(d) }));
                string hash = DocumentGate.PlanHash(bound, "target_document", "steps", "context");
                foreach (JObject step in (JArray)r["steps"]) DemandAllowed(step.Value<string>("tool"));
                if (r.Value<bool?>("dry_run") != false)
                {
                    var token = DocumentGate.Confirmations.Issue(Name, identity, hash);
                    return CommandResult.Ok(new JObject { ["dry_run"] = true, ["confirmation_token"] = token.Token,
                        ["workflow_id"] = workflowId, ["steps"] = r["steps"].DeepClone(), ["model_changes"] = new JObject(),
                        ["preview_scope"] = "Exact requested steps and open-document state. Child construction is checked immediately before each step; this is not a construction rehearsal.",
                        ["atomic"] = false, ["execution_policy"] = "Stop at first failure; never replay completed writes automatically." });
                }
                if (string.IsNullOrWhiteSpace(workflowId)) return Precondition("idempotency_key is required.", "supply_idempotency_key");
                var approval = DocumentGate.Confirmations.Validate(r.Value<string>("confirmation_token"), Name, identity, hash);
                if (!approval.Ok) return Precondition(approval.Message, "preview_again");
                journal = new WorkflowJournal(Path.Combine(HorizunPaths.DataRoot(), "workflows"), workflowId);
                journal.Append(new JObject { ["phase"] = "approved", ["workflow_id"] = workflowId, ["plan_hash"] = hash });
                foreach (JObject step in (JArray)r["steps"])
                {
                    string key = step.Value<string>("key"), tool = step.Value<string>("tool");
                    stepStarted = false;
                    currentStep = key;
                    DemandAllowed(tool);
                    journal.Append(new JObject { ["step"] = key, ["tool"] = tool, ["phase"] = "preparation_started" });
                    var args = PlanReferences.Resolve(step["arguments"], results, out string referenceError) as JObject;
                    if (referenceError != null || args == null) throw new InvalidOperationException(referenceError ?? "No resolved arguments.");
                    string target = args.Value<string>("target_document") ?? (string.IsNullOrEmpty(root.PathName) ? root.Title : root.PathName);
                    if (tool != "horizun_document_session")
                    {
                        Document destination = FindOpen(app, target);
                        if (destination != null && Settings.ForceReadOnlyOnWorkshared && destination.IsWorkshared)
                            throw new InvalidOperationException("Central protection prevents workflow execution in the target document.");
                        if (destination != null && !destination.Equals(app.ActiveUIDocument?.Document) && string.IsNullOrWhiteSpace(destination.PathName))
                            throw new InvalidOperationException("Cannot activate an unsaved document by path; activate the specified document in Revit first.");
                        if (destination == null && !Path.IsPathRooted(target ?? ""))
                            throw new InvalidOperationException("A non-open target must be an explicit absolute file path.");
                        if (destination == null || !destination.Equals(app.ActiveUIDocument?.Document))
                        {
                            var activate = new JObject { ["operation"] = "open", ["file_path"] = destination?.PathName ?? target,
                                ["expected_version"] = app.Application.VersionNumber, ["dry_run"] = false, ["allow_upgrade"] = false };
                            DemandAllowed("horizun_document_session");
                            CommandResult opened = resolve("horizun_document_session").Execute(app, activate.ToString(Formatting.None));
                            var openedData = AsObject(opened.Data);
                            trace.Add(new JObject { ["step"] = key, ["phase"] = "prepare_document", ["success"] = opened.Success,
                                ["data"] = openedData, ["error"] = opened.Error });
                            journal.Append((JObject)trace.Last);
                            if (!opened.Success || openedData.Value<bool?>("active_document_verified") != true)
                                throw new InvalidOperationException(opened.Error ?? "Document activation was not verified.");
                            recovered = true;
                        }
                        args["target_document"] = target;
                    }
                    CheckCentralProtection(app);
                    ICommand command = resolve(tool);
                    bool read = tool == "horizun_capture_view" || tool == "horizun_model_snapshot";
                    if (!read && tool != "horizun_document_session")
                    {
                        args["dry_run"] = true;
                        CommandResult preview = command.Execute(app, args.ToString(Formatting.None));
                        var previewData = AsObject(preview.Data);
                        trace.Add(new JObject { ["step"] = key, ["phase"] = "rehearsal", ["success"] = preview.Success,
                            ["data"] = previewData, ["detail"] = preview.Detail, ["error"] = preview.Error });
                        journal.Append((JObject)trace.Last);
                        if (!preview.Success || string.IsNullOrWhiteSpace(previewData.Value<string>("confirmation_token")))
                            return Failed(key, trace, completed, false, preview.Error ?? "Child did not issue an executable confirmation.", workflowId, journal.Path);
                        args["confirmation_token"] = previewData["confirmation_token"].DeepClone();
                        args["dry_run"] = false;
                        args["idempotency_key"] = workflowId + ":" + key;
                    }
                    else if (!read) { args["dry_run"] = false; args["allow_upgrade"] = false; args["expected_version"] = app.Application.VersionNumber; }
                    journal.Append(new JObject { ["step"] = key, ["tool"] = tool, ["phase"] = "execution_started" });
                    stepStarted = true;
                    CommandResult result;
                    using (var changes = new ChangeWatch(app.Application))
                    {
                        result = command.Execute(app, args.ToString(Formatting.None));
                        SpatialAfterWrite.Attach(tool, changes, result);
                    }
                    JObject data = AsObject(result.Data);
                    bool verified = result.Success && (read
                        ? (tool == "horizun_model_snapshot" ? data.Value<bool?>("complete") == true : data.Value<bool?>("captured") == true && data.Value<bool?>("view_restored") != false)
                        : tool == "horizun_document_session" ? data.Value<bool?>("active_document_verified") == true
                        : ApplicationOutcome.IsFullyApplied(ApplicationOutcome.Read(data)));
                    trace.Add(new JObject { ["step"] = key, ["phase"] = "execute", ["tool"] = tool,
                        ["success"] = result.Success, ["verified"] = verified, ["data"] = data,
                        ["detail"] = result.Detail, ["error"] = result.Error });
                    journal.Append((JObject)trace.Last);
                    if (!verified) return Failed(key, trace, completed, true, result.Error ?? "Child result is not fully verified.", workflowId, journal.Path);
                    results[key] = data;
                    completed++;
                }
                var outputs = new JObject();
                foreach (var item in results) outputs[item.Key] = item.Value;
                var payload = new JObject { ["workflow_id"] = workflowId, ["workflow"] = new JObject
                    { ["recovered"] = recovered, ["completed_steps"] = completed, ["state"] = "verified_completed" },
                    ["steps"] = trace, ["results"] = outputs, ["atomic"] = false, ["trace_path"] = journal.Path,
                    };
                var checks = results.Values.OfType<JObject>().Select(v => v["spatial_check"] as JObject).Where(c => c != null).ToList();
                if (checks.Count > 0) payload["spatial_check"] = new JObject { ["status"] = "per_step", ["checked_steps"] = checks.Count,
                    ["errors"] = checks.Sum(c => c.Value<int?>("errors") ?? 0), ["warnings"] = checks.Sum(c => c.Value<int?>("warnings") ?? 0),
                    ["scope"] = "See each step result for document scope and findings; a spatial finding is distinct from execution verification." };
                ApplicationOutcome.Stamp(payload, ApplicationOutcome.Declare(ApplicationState.VerifiedApplied,
                    "per_step", completed, completed, completed, 0, 0, 0));
                return CommandResult.Ok(payload);
            }
            catch (Exception ex) { return Failed(currentStep, trace, completed, stepStarted, ex.Message, workflowId, journal?.Path); }
            finally { journal?.Dispose(); }
        }
        private void DemandAllowed(string tool)
        {
            var contract = Contract.Find(tool);
            if (contract == null || (tool != Name && resolve(tool) == null)) throw new InvalidOperationException("Tool is not installed: " + tool);
            if (!Settings.IsToolAllowed(contract, out string refusal)) throw new InvalidOperationException(refusal);
        }
        private static void CheckCentralProtection(UIApplication app)
        {
            if (Settings.ForceReadOnlyOnWorkshared && app.ActiveUIDocument?.Document.IsWorkshared == true)
                throw new InvalidOperationException("Central protection prevents workflow execution in this workshared document.");
        }
        private static Document FindOpen(UIApplication app, string target)
        {
            var matches = app.Application.Documents.Cast<Document>().Where(d => !d.IsLinked &&
                (Path.IsPathRooted(target ?? "") ? DocIdentity.SamePath(d.PathName, target) : d.Title == target)).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }
        private static JObject AsObject(object data) => data as JObject ?? (data == null ? new JObject() : JObject.FromObject(data));
        private static CommandResult Precondition(string message, string action) => CommandResult.FailWithDetail(message,
            new JObject { ["category"] = "precondition", ["code"] = "workflow_precondition", ["write_started"] = false,
                ["changes_applied"] = false, ["recovery"] = new JObject { ["action"] = action } });
        private static CommandResult Failed(string key, JArray trace, int completed, bool started, string message, string id, string tracePath) =>
            CommandResult.FailWithDetail(message, new JObject { ["code"] = "workflow_stopped", ["category"] = completed == 0 && !started ? "precondition" : "execution", ["failed_action"] = key,
                ["workflow_id"] = id, ["trace_path"] = tracePath, ["completed_steps"] = completed, ["steps"] = trace, ["write_started"] = started || completed > 0,
                ["changes_applied"] = started || completed > 0 ? (JToken)null : false,
                ["application"] = new JObject { ["state"] = completed > 0 ? "partial" : started ? "uncertain" : "failed" },
                ["recovery"] = new JObject { ["action"] = "inspect_completed_steps_before_new_plan", ["automatic_retry"] = false } });
    }
}
