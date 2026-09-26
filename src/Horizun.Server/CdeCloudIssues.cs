// -----------------------------------------------------------------------------
// Horizun MCP server - original Horizun code.
//
// horizun_cde_cloud, ACC Issues: issues_list (read), issue_create and issue_update
// (WRITE: dry_run by default -> confirmation_token -> apply), through the Autodesk
// Construction Cloud Issues API v1 on Autodesk Platform Services. Endpoints, from
// Autodesk's public reference (https://aps.autodesk.com/en/docs/acc/v1/reference/http/,
// section "Issues"; overview at https://aps.autodesk.com/en/docs/acc/v1/overview/field-guide/issues/):
//
//   GET   /construction/issues/v1/projects/{projectId}/issues                  issues-issues-GET
//   GET   /construction/issues/v1/projects/{projectId}/issues/{issueId}        issues-issues-issueId-GET
//   POST  /construction/issues/v1/projects/{projectId}/issues                  issues-issues-POST
//   PATCH /construction/issues/v1/projects/{projectId}/issues/{issueId}        issues-issues-issueId-PATCH
//   GET   /construction/issues/v1/projects/{projectId}/issue-types?include=subtypes
//   GET   /construction/issues/v1/projects/{projectId}/issue-root-cause-categories?include=rootcauses
//
// projectId there is the project GUID WITHOUT the Data Management "b." prefix. Lists
// answer {pagination:{limit,offset,totalResults}, results:[...]} with limit <= 100. A
// POST needs title, issueSubtypeId (the SUBTYPE, not the type) and status; dates are
// YYYY-MM-DD; assignedTo goes with assignedToType (user|company|role). Writes need a
// USER context - a 3-legged token with data:write. A 2-legged app token is refused
// here, before any write request, with the steps to obtain a 3-legged one.
//
// IDEMPOTENCY. The caller's key (external_key, or the finding's own id) is stored in
// the issue description as a last line "[horizun-key:<key>]" - the one place every
// ACC project has, where a custom attribute would need a per-project definition id.
// Before a create every issue of the project is scanned for that marker (bounded by
// max_calls); a scan that could not finish REFUSES the apply, because "not found" in a
// page that was never read is not "absent". A POST is not retried on a lost answer
// (it is not idempotent): the lost answer is reconciled by scanning for the key again.
//
// VERIFICATION. After a write the issue is read back with a GET and compared field by
// field - title, status, assignee, dates, subtype, location, root cause, the text and
// the key marker; the description whole, for equality. host_verified is true only
// when every field sent reads back as sent, and never when nothing was compared.
//
// Nothing binary is attached in this pass (no snapshot, file, markup or linked
// document); the reply says so.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Horizun.Revit.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Horizun.Server
{
    internal static partial class CdeCloudTool
    {
        internal const string IssuesWriteScope = "data:read data:write";
        private const int IssuesPage = 100;
        private const int IssueListDefault = 100, IssueListMax = 1000;
        private const int MaxIssuePages = 500;
        private const int ExcerptLength = 300;
        private static readonly Regex KeyPattern = new Regex(@"^[A-Za-z0-9._:\-]{1,100}$", RegexOptions.CultureInvariant);
        private static readonly Regex MarkerPattern = new Regex(@"\[horizun-key:([A-Za-z0-9._:\-]{1,100})\]", RegexOptions.CultureInvariant);

        /// <summary>Tokens of issue_create / issue_update. Process-wide: a dry run and its apply meet here.</summary>
        internal static readonly IssueTokenStore IssueConfirmations = new IssueTokenStore();

        private static readonly string[] IssueStatuses = { "draft", "open", "pending", "in_progress", "completed", "in_review", "not_approved", "in_dispute", "closed" };
        private static readonly string[] AssigneeTypes = { "user", "company", "role" };

        // argument name -> ACC field. The order is the order of the plan and of the verification.
        private static readonly KeyValuePair<string, string>[] IssueFieldMap =
        {
            Pair("title", "title"), Pair("description", "description"), Pair("issue_type_id", "issueSubtypeId"),
            Pair("status", "status"), Pair("assigned_to", "assignedTo"), Pair("assigned_to_type", "assignedToType"),
            Pair("due_date", "dueDate"), Pair("start_date", "startDate"), Pair("location_id", "locationId"),
            Pair("root_cause_id", "rootCauseId")
        };

        private static KeyValuePair<string, string> Pair(string a, string b) => new KeyValuePair<string, string>(a, b);

        // A coordination ledger row (CSV columns or JSON keys) names the same things its own
        // way. First present wins; explicit issue fields override all of it. This repo's own
        // ledger (CoordinationRules.CsvHeader / CoordinationLedger.ToJson) has no title column:
        // its title is built from category_a/category_b (LedgerTitle), its text is the note,
        // and its clash context (sides, point, responsible, suggested action) goes below it.
        private static readonly string[] FindingTitleKeys = { "title", "name", "summary", "clash_name", "check" };
        private static readonly string[] FindingTextKeys = { "description", "detail", "details", "comment", "message", "reason", "note" };
        private static readonly string[] FindingIdKeys = { "external_key", "finding_id", "clash_id", "issue_key", "guid", "id" };
        private static readonly string[] FindingContextKeys =
        {
            "category_a", "category_b", "side_a", "side_b", "point_mm", "priority", "responsible", "immovable_discipline",
            "suggested_action", "scope",
            "severity", "discipline", "category", "test", "level", "grid", "location", "zone",
            "element_a", "element_b", "element_ids", "elements", "distance", "point", "x", "y", "z", "source", "model"
        };

        internal static string IssuesBase(string projectId) => ApsBase + "/construction/issues/v1/projects/" + Esc(BareProject(projectId));
        private static string BareProject(string projectId) => projectId.StartsWith("b.", StringComparison.Ordinal) ? projectId.Substring(2) : projectId;
        internal static string IssueMarker(string key) => "[horizun-key:" + key + "]";

        /// <summary>The ACC web page of an issue (acc.autodesk.com; an EMEA account serves the same path under its own region host).</summary>
        internal static string IssueWebUrl(string projectId, string issueId) =>
            "https://acc.autodesk.com/build/issues/projects/" + BareProject(projectId) + "/issues?issueId=" + Uri.EscapeDataString(issueId ?? "");

        private sealed class IssueDraft
        {
            public JObject Fields = new JObject();   // ACC field names; only what the caller set, never the description
            public string Text;                       // the description WITHOUT the key marker
            public bool HasText;
            public string Key;
            public JArray Sources = new JArray();     // which finding column fed which field
        }

        private sealed class IssueScan
        {
            public List<JObject> Issues = new List<JObject>();
            public bool Complete = true, Truncated;
            public string Error;
            public int Total = -1;
        }

        // ================================================================================
        // entry
        // ================================================================================

        private static JObject AccIssues(string op, JObject args, CancellationToken ct, CdeCloudEnvironment env, string projectId, int maxCalls)
        {
            bool write = op != "issues_list";
            bool dryRun = Bool(args, "dry_run", true);
            string token = Str(args, "confirmation_token");
            string issueId = Str(args, "issue_id");
            if (issueId != null && issueId.Trim().Length == 0) throw new ToolRefusal("issue_id is empty. Nothing was read or written.");
            // Every argument is decided before the first request.
            IssueDraft draft = write ? ReadIssueDraft(args, op) : null;
            string listQuery = write ? null : ListFilterQuery(args);
            string listKey = write ? null : KeyArg(Str(args, "external_key"));
            if (write && !dryRun)
            {
                if (string.IsNullOrWhiteSpace(token))
                    throw new ToolRefusal(op + " with dry_run=false needs the confirmation_token of its dry run: the plan is what gets " +
                                          "confirmed, not the intention. Nothing was read or written.");
                RequireIssueWriteProfile(op);
            }
            if (!ApsAuth.AnyConfigured(env)) throw new ToolRefusal(ApsAuth.NotConfiguredMessage(env));
            if (write) RefuseWithoutUserToken(env, op);

            using (var http = new CdeCloudHttp(env, maxCalls, ct))
            {
                http.AllowHost(ApsAuth.Host);
                CloudCredential cred = ApsAuth.Resolve(env, http, write ? IssuesWriteScope : null);
                string userContext = write ? UserContext(cred, op, env) : null;
                http.BearerToken = cred.Token;
                var result = new JObject { ["operation"] = op, ["provider"] = "acc", ["project_id"] = projectId };
                if (op == "issues_list") IssuesList(args, http, projectId, issueId, listQuery, listKey, result);
                else if (op == "issue_create") IssueCreate(http, projectId, draft, dryRun, token, result);
                else IssueUpdate(http, projectId, draft, issueId, dryRun, token, result);
                if (userContext != null) result["user_context"] = userContext;
                if (write)
                    result["attachments"] = "none: this pass attaches nothing binary (no snapshot, file, markup or linked document).";
                result["auth"] = cred.Describe();
                result["http"] = http.Describe();
                return result;
            }
        }

        private static void RequireIssueWriteProfile(string op)
        {
            string refusal;
            if (!Settings.AllowsExternalSideEffect(out refusal))
                throw new ToolRefusal(op + " with dry_run=false writes to Autodesk Construction Cloud, outside the model, and that needs " +
                                      "the profile: " + refusal + " The dry run and issues_list stay available. Nothing was written.");
        }

        // ================================================================================
        // credentials: a user, with data:write
        // ================================================================================

        internal static string TwoLeggedRefusal(CdeCloudEnvironment env, string op, string why, bool noRequest) =>
            op + " writes an ACC issue AS A USER, and ACC accepts issue writes only in a user context: a 3-legged token with " +
            "data:write. " + why + ". To get one: (1) give your APS app a callback URL and have an ACC account admin add it as a " +
            "custom integration; (2) sign in once with the authorization-code flow asking scope 'data:read data:write'; (3) save " +
            "{access_token, refresh_token, expires_at} to " + env.ResolvedTokenFile() + " and keep HORIZUN_APS_CLIENT_ID (and the " +
            "secret, for a confidential app) set so it refreshes - or set " + ApsAuth.AccessTokenName + " to a 3-legged token. " +
            "Nothing was written" + (noRequest ? " and no request was made." : ".");

        /// <summary>Only client credentials configured means only a 2-legged token can exist: refused with zero calls.</summary>
        private static void RefuseWithoutUserToken(CdeCloudEnvironment env, string op)
        {
            if (!string.IsNullOrWhiteSpace(env.Variable(ApsAuth.AccessTokenName))) return;
            if (File.Exists(env.ResolvedTokenFile())) return;
            throw new ToolRefusal(TwoLeggedRefusal(env, op,
                "Only 2-legged app credentials (HORIZUN_APS_CLIENT_ID/SECRET) are configured, and an app token has no user", true));
        }

        /// <summary>
        /// Reads the token's own claims (APS access tokens are JWTs) to refuse an app token or
        /// a token without data:write BEFORE the write. The claims are never echoed. A token
        /// that is not a readable JWT is not refused: ACC's own 401/403 decides, and says so.
        /// </summary>
        private static string UserContext(CloudCredential cred, string op, CdeCloudEnvironment env)
        {
            if (cred.Mode == "two_legged")
                throw new ToolRefusal(TwoLeggedRefusal(env, op, "The credential resolved to a 2-legged app token", false));
            JObject claims = JwtClaims(cred.Token);
            if (claims == null)
            {
                cred.Warnings.Add("The token is not a readable JWT, so its user context and data:write scope were not checked " +
                                  "before the request; ACC answers 401/403 if either is missing.");
                return "unknown";
            }
            List<string> scopes = ScopesOf(claims);
            if (scopes != null && !scopes.Contains("data:write"))
                throw new ToolRefusal(op + " needs data:write and the token from " + cred.Source + " carries only [" + string.Join(" ", scopes) +
                                      "]. Sign in again asking 'data:read data:write' (a refresh cannot widen a scope). Nothing was written.");
            if (claims["userid"] == null && claims["user_id"] == null)
                throw new ToolRefusal(TwoLeggedRefusal(env, op, "The token from " + cred.Source + " carries no user id claim: it is an app (2-legged) token", false));
            return "user";
        }

        internal static JObject JwtClaims(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;
            string[] parts = token.Split('.');
            if (parts.Length != 3) return null;
            string p = parts[1].Replace('-', '+').Replace('_', '/');
            switch (p.Length % 4)
            {
                case 1: return null;
                case 2: p += "=="; break;
                case 3: p += "="; break;
            }
            try { return JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p))); }
            catch (FormatException) { return null; }
            catch (JsonException) { return null; }
        }

        internal static List<string> ScopesOf(JObject claims)
        {
            JToken s = claims["scope"];
            if (s is JArray a) return a.Select(x => (string)x).Where(x => x != null).ToList();
            if (s != null && s.Type == JTokenType.String)
                return ((string)s).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            return null;
        }

        // ================================================================================
        // arguments
        // ================================================================================

        private static string KeyArg(string raw)
        {
            if (raw == null) return null;
            string key = raw.Trim();
            if (!KeyPattern.IsMatch(key))
                throw new ToolRefusal("external_key '" + raw + "' must be 1-100 characters of A-Z a-z 0-9 . _ : - (it is written into the " +
                                      "issue description as [horizun-key:<key>]). Nothing was read or written.");
            return key;
        }

        private static string ListFilterQuery(JObject args)
        {
            if (args["finding"] != null && args["finding"].Type != JTokenType.Null)
                throw new ToolRefusal("finding is for issue_create / issue_update. Nothing was read.");
            JToken raw = args["issue"];
            if (raw == null || raw.Type == JTokenType.Null) return "";
            if (!(raw is JObject issue)) throw new ToolRefusal("issue must be an object. Nothing was read.");
            var sb = new StringBuilder();
            foreach (JProperty p in issue.Properties())
            {
                if (p.Value.Type != JTokenType.String) throw new ToolRefusal("issue." + p.Name + " must be a string. Nothing was read.");
                string v = ((string)p.Value).Trim();
                switch (p.Name)
                {
                    case "status":
                        if (!IssueStatuses.Contains(v)) throw new ToolRefusal("issue.status must be one of " + string.Join(", ", IssueStatuses) + ". Nothing was read.");
                        sb.Append("&filter%5Bstatus%5D=").Append(Esc(v));
                        break;
                    case "issue_type_id": sb.Append("&filter%5BissueSubtypeId%5D=").Append(Esc(v)); break;
                    case "assigned_to": sb.Append("&filter%5BassignedTo%5D=").Append(Esc(v)); break;
                    default: throw new ToolRefusal("issues_list filters by issue.status, issue.issue_type_id and issue.assigned_to only; '" + p.Name + "' is not a filter. Nothing was read.");
                }
            }
            return sb.ToString();
        }

        private static IssueDraft ReadIssueDraft(JObject args, string op)
        {
            var d = new IssueDraft();
            JToken rawIssue = args["issue"], rawFinding = args["finding"];
            if (rawIssue != null && rawIssue.Type != JTokenType.Null && !(rawIssue is JObject))
                throw new ToolRefusal("issue must be an object. Nothing was read or written.");
            if (rawFinding != null && rawFinding.Type != JTokenType.Null && !(rawFinding is JObject))
                throw new ToolRefusal("finding must be an object: one ledger row, its CSV columns or JSON keys as properties. Nothing was read or written.");
            JObject issue = rawIssue as JObject ?? new JObject();
            var known = new HashSet<string>(IssueFieldMap.Select(p => p.Key), StringComparer.Ordinal);
            foreach (JProperty p in issue.Properties())
            {
                if (!known.Contains(p.Name))
                    throw new ToolRefusal("issue." + p.Name + " is not an issue field. Known: " + string.Join(", ", known) + ". Nothing was read or written.");
                if (p.Value.Type != JTokenType.String && p.Value.Type != JTokenType.Null)
                    throw new ToolRefusal("issue." + p.Name + " must be a string. Nothing was read or written.");
            }

            string title = null, text = null;
            if (rawFinding is JObject finding)
            {
                var cols = new Dictionary<string, JToken>(StringComparer.Ordinal);
                foreach (JProperty p in finding.Properties())
                {
                    string k = p.Name.Trim().ToLowerInvariant().Replace(' ', '_');
                    if (!cols.ContainsKey(k)) cols[k] = p.Value;
                }
                title = FromFinding(cols, FindingTitleKeys, "title", d.Sources) ?? LedgerTitle(cols, d.Sources);
                text = FromFinding(cols, FindingTextKeys, "description", d.Sources);
                d.Key = FromFinding(cols, FindingIdKeys, "external_key", d.Sources);
                var lines = new List<string>();
                var used = new JArray();
                foreach (string k in FindingContextKeys)
                {
                    JToken v;
                    string s = cols.TryGetValue(k, out v) ? Scalar(v) : null;
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    lines.Add(k + ": " + s.Trim());
                    used.Add(k);
                }
                if (lines.Count > 0)
                {
                    text = (string.IsNullOrWhiteSpace(text) ? "" : text.TrimEnd() + "\n\n") + string.Join("\n", lines);
                    d.Sources.Add(new JObject { ["field"] = "description", ["from"] = used });
                }
            }

            foreach (KeyValuePair<string, string> pair in IssueFieldMap)
            {
                string v = (string)issue[pair.Key];
                if (v == null) continue;
                if (pair.Key == "title") { title = v; continue; }
                if (pair.Key == "description") { text = v; continue; }
                v = v.Trim();
                if (v.Length == 0) throw new ToolRefusal("issue." + pair.Key + " is empty. Nothing was read or written.");
                if (pair.Key == "status" && !IssueStatuses.Contains(v))
                    throw new ToolRefusal("issue.status must be one of " + string.Join(", ", IssueStatuses) + ". Nothing was read or written.");
                if (pair.Key == "assigned_to_type" && !AssigneeTypes.Contains(v))
                    throw new ToolRefusal("issue.assigned_to_type must be user, company or role. Nothing was read or written.");
                DateTime date;
                if ((pair.Key == "due_date" || pair.Key == "start_date") &&
                    !DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                    throw new ToolRefusal("issue." + pair.Key + " must be YYYY-MM-DD. Nothing was read or written.");
                d.Fields[pair.Value] = v;
            }
            if (title != null)
            {
                title = title.Trim();
                if (title.Length == 0) throw new ToolRefusal("The issue title is empty. Nothing was read or written.");
                var withTitle = new JObject { ["title"] = title };
                foreach (JProperty p in d.Fields.Properties()) withTitle[p.Name] = p.Value;
                d.Fields = withTitle;
            }
            if (text != null) { d.Text = MarkerPattern.Replace(text.Replace("\r\n", "\n"), "").TrimEnd(); d.HasText = true; }
            string explicitKey = Str(args, "external_key");
            d.Key = KeyArg(explicitKey ?? d.Key);
            if (d.Fields["assignedTo"] != null && d.Fields["assignedToType"] == null) d.Fields["assignedToType"] = "user";
            if (d.Fields["assignedToType"] != null && d.Fields["assignedTo"] == null)
                throw new ToolRefusal("issue.assigned_to_type without issue.assigned_to. Nothing was read or written.");

            if (op == "issue_create")
            {
                if (d.Fields["title"] == null)
                    throw new ToolRefusal("issue_create needs a title: issue.title, or a finding with title/name/summary or category_a/category_b. Nothing was read or written.");
                if (d.Fields["issueSubtypeId"] == null)
                    throw new ToolRefusal("issue_create needs issue.issue_type_id: ACC requires an issue SUBTYPE id, and issues_list returns " +
                                          "issue_types with their subtypes. Nothing was read or written.");
                if (d.Key == null)
                    throw new ToolRefusal("issue_create needs external_key, or a finding with finding_id/clash_id/guid/id: the key is written " +
                                          "into the description so a retry finds the issue instead of creating a second one. Nothing was read or written.");
                if (d.Fields["status"] == null) d.Fields["status"] = "open";
            }
            else if (d.Fields.Count == 0 && !d.HasText)
                throw new ToolRefusal("issue_update needs at least one field in issue (or a finding). Nothing was read or written.");
            return d;
        }

        /// <summary>A ledger row has no title column: "Clash: &lt;category_a&gt; vs &lt;category_b&gt;".</summary>
        private static string LedgerTitle(Dictionary<string, JToken> cols, JArray sources)
        {
            JToken a, b;
            string ca = cols.TryGetValue("category_a", out a) ? Scalar(a)?.Trim() : null;
            string cb = cols.TryGetValue("category_b", out b) ? Scalar(b)?.Trim() : null;
            var from = new JArray();
            if (!string.IsNullOrEmpty(ca)) from.Add("category_a");
            if (!string.IsNullOrEmpty(cb)) from.Add("category_b");
            if (from.Count == 0) return null;
            sources.Add(new JObject { ["field"] = "title", ["from"] = from });
            return "Clash: " + (from.Count == 2 ? ca + " vs " + cb : !string.IsNullOrEmpty(ca) ? ca : cb);
        }

        private static string FromFinding(Dictionary<string, JToken> cols, string[] keys, string field, JArray sources)
        {
            foreach (string k in keys)
            {
                JToken v;
                if (!cols.TryGetValue(k, out v)) continue;
                string s = Scalar(v);
                if (string.IsNullOrWhiteSpace(s)) continue;
                sources.Add(new JObject { ["field"] = field, ["from"] = k });
                return s;
            }
            return null;
        }

        // ================================================================================
        // operations
        // ================================================================================

        private static void IssuesList(JObject args, CdeCloudHttp http, string projectId, string issueId, string query, string key, JObject result)
        {
            var problems = new JArray();
            if (issueId != null)
            {
                string error;
                JObject one = ReadIssue(http, projectId, issueId.Trim(), out error);
                if (error != null) problems.Add("issue " + issueId + ": " + error);
                result["issues"] = one == null ? new JArray() : new JArray(DescribeIssue(one, projectId));
                result["count"] = one == null ? 0 : 1;
            }
            else
            {
                int offset = Int(args, "offset", 0, 0, int.MaxValue);
                int limit = Int(args, "limit", IssueListDefault, 1, IssueListMax);
                IssueScan scan = key != null ? ReadIssues(http, projectId, query, 0, int.MaxValue) : ReadIssues(http, projectId, query, offset, limit);
                if (!scan.Complete) problems.Add("issues: " + scan.Error);
                List<JObject> issues = key != null ? scan.Issues.Where(i => KeyOf(i) == key).ToList() : scan.Issues;
                result["issues"] = new JArray(issues.Select(i => DescribeIssue(i, projectId)));
                result["count"] = issues.Count;
                result["total_results"] = scan.Total >= 0 ? (JToken)scan.Total : JValue.CreateNull();
                result["truncated"] = scan.Truncated;
            }
            string typesError, causesError;
            result["issue_types"] = ReadIssueTypes(http, projectId, out typesError);
            result["root_cause_categories"] = ReadRootCauses(http, projectId, out causesError);
            if (typesError != null) problems.Add("issue-types: " + typesError);
            if (causesError != null) problems.Add("issue-root-cause-categories: " + causesError);
            result["coverage_complete"] = problems.Count == 0;
            result["problems"] = problems;
            result["note"] = "Read-only: nothing was written. issue_type_id is the SUBTYPE id a create needs; external_key is read from " +
                             "the [horizun-key:...] line of the description. 'coverage_complete=false' names what was not read.";
        }

        private static void IssueCreate(CdeCloudHttp http, string projectId, IssueDraft d, bool dryRun, string token, JObject result)
        {
            JObject payload = (JObject)d.Fields.DeepClone();
            payload["description"] = ComposeDescription(d.Text, d.Key);
            // An unpublished issue is visible to its creator only; a draft stays a draft.
            payload["published"] = (string)payload["status"] != "draft";
            result["external_key"] = d.Key;
            result["mapped_from_finding"] = d.Sources;

            string typesError;
            JArray types = ReadIssueTypes(http, projectId, out typesError);
            string subtypeId = (string)payload["issueSubtypeId"];
            JObject subtype = FindSubtype(types, subtypeId);
            if (typesError == null && subtype == null)
                throw new ToolRefusal("issue.issue_type_id '" + subtypeId + "' is not an active issue SUBTYPE of this project. Active: " +
                                      SubtypeList(types) + ". Nothing was written.");
            result["issue_type"] = subtype;
            if (typesError != null) result["issue_type_check"] = "not checked (" + typesError + "); ACC validates it on apply";

            IssueScan scan = ReadIssues(http, projectId, "", 0, int.MaxValue);
            List<JObject> existing = scan.Issues.Where(i => KeyOf(i) == d.Key).ToList();
            result["idempotency_scan"] = new JObject { ["issues_read"] = scan.Issues.Count, ["complete"] = scan.Complete, ["reason"] = scan.Error };
            if (existing.Count > 0)
            {
                // The retry case: the issue is already there. Nothing is created; what is there
                // is read back and compared with what this call would have sent.
                result["state"] = "already_exists";
                if (existing.Count > 1) result["duplicates"] = new JArray(existing.Select(i => i["id"]));
                VerifyInto(http, projectId, (string)existing[0]["id"], Comparable(payload), d.Key, (string)payload["description"], result);
                result["note"] = "An issue already carries " + IssueMarker(d.Key) + ": nothing was created. A difference in verification " +
                                 "is reported, not overwritten - issue_update changes it.";
                return;
            }
            string blocked = scan.Complete ? null :
                "the project's issues could not all be read (" + scan.Error + "), so no issue carrying " + IssueMarker(d.Key) +
                " is not proved and a create could duplicate one. Raise max_calls or retry.";
            string planHash = IssueTokenStore.HashPlan("issue_create", projectId, payload.ToString(Formatting.None), d.Key);
            const string command = ToolName + ":issue_create";
            string docKey = "acc:" + projectId;
            if (dryRun)
            {
                result["state"] = "rehearsed";
                result["plan"] = new JObject { ["method"] = "POST", ["path"] = "/issues", ["body"] = payload };
                if (blocked == null && http.Budget - http.Calls < WriteCalls)
                    blocked = "max_calls=" + http.Budget + " leaves " + (http.Budget - http.Calls) + " call(s) after the scan; the apply needs the " +
                              "same reads plus " + WriteCalls + " (the POST and its read-back). Raise max_calls.";
                if (blocked != null) result["apply_blocked"] = blocked;
                else
                {
                    IssueTokenStore.Issued c = IssueConfirmations.Issue(command, docKey, planHash);
                    result["confirmation_token"] = c.Token;
                    result["token_expires_utc"] = c.ExpiresUtc.ToString("o", CultureInfo.InvariantCulture);
                }
                result["note"] = "Dry run: nothing was written. Send the same arguments with dry_run=false and this confirmation_token; " +
                                 "the issue is read back and compared after the POST.";
                return;
            }
            if (blocked != null) throw new ToolRefusal("Not applied: " + blocked + " Nothing was written.");
            RequireWriteBudget(http, "POST");
            string tokenProblem = IssueConfirmations.Validate(token, command, docKey, planHash);
            if (tokenProblem != null) throw new ToolRefusal(tokenProblem + " Nothing was written.");

            CloudResponse r = http.SendJson(HttpMethod.Post, IssuesBase(projectId) + "/issues", payload, false);
            if (r.BudgetExhausted) throw new ToolRefusal("The call budget was spent before the POST: no request was made. Nothing was written.");
            string id = r.Ok ? (string)IssueBody(r.Body)?["id"] : null;
            if (id == null)
            {
                // A 2xx whose body is empty or not a JSON object is an ACCEPTED write with a
                // lost answer, not a refusal: it goes through the same re-scan by key.
                bool lost = r.Ok || r.Status == 0 || r.Status >= 500 || (r.Status >= 200 && r.Status < 300);
                if (!lost) throw new ToolRefusal("ACC refused the create: " + r.Error + ApiDetail(r) + ". Nothing was written.");
                IssueScan again = ReadIssues(http, projectId, "", 0, int.MaxValue);
                JObject landed = again.Issues.FirstOrDefault(i => KeyOf(i) == d.Key);
                if (landed == null)
                    throw new ToolRefusal("The create got no usable answer (" + (r.Error ?? "no id in the reply") + ApiDetail(r) + ") and a re-scan " +
                                          (again.Complete ? "found no issue carrying " + IssueMarker(d.Key) : "could not finish (" + again.Error + ")") +
                                          ". Rehearse again: the key keeps the retry from duplicating it.");
                id = (string)landed["id"];
                result["reconciled"] = "the POST's answer was lost; the issue was found again by its key";
            }
            result["state"] = "applied";
            VerifyInto(http, projectId, id, Comparable(payload), d.Key, (string)payload["description"], result);
        }

        private static void IssueUpdate(CdeCloudHttp http, string projectId, IssueDraft d, string issueId, bool dryRun, string token, JObject result)
        {
            if (issueId == null && d.Key == null)
                throw new ToolRefusal("issue_update needs issue_id, or external_key (or a finding with an id) to find the issue by its key. Nothing was written.");
            if (issueId == null)
            {
                IssueScan scan = ReadIssues(http, projectId, "", 0, int.MaxValue);
                List<JObject> matches = scan.Issues.Where(i => KeyOf(i) == d.Key).ToList();
                if (matches.Count > 1)
                    throw new ToolRefusal(matches.Count + " issues carry " + IssueMarker(d.Key) + " (" + string.Join(", ", matches.Select(i => (string)i["id"])) +
                                          "). Pass issue_id. Nothing was written.");
                if (matches.Count == 0)
                    throw new ToolRefusal(scan.Complete
                        ? "No issue of this project carries " + IssueMarker(d.Key) + ". Nothing was written."
                        : "No issue carrying " + IssueMarker(d.Key) + " was found in the part read (" + scan.Error + "); the rest was not read, " +
                          "which is not 'absent'. Nothing was written.");
                issueId = (string)matches[0]["id"];
            }
            issueId = issueId.Trim();
            string error;
            JObject current = ReadIssue(http, projectId, issueId, out error);
            if (current == null) throw new ToolRefusal("Issue " + issueId + " could not be read (" + error + "). Nothing was written.");
            string currentKey = KeyOf(current);
            if (d.Key != null && currentKey != null && currentKey != d.Key)
                throw new ToolRefusal("Issue " + issueId + " carries " + IssueMarker(currentKey) + ", not " + IssueMarker(d.Key) + ". Nothing was written.");
            string key = currentKey ?? d.Key;

            JObject wanted = (JObject)d.Fields.DeepClone();
            if (d.HasText) wanted["description"] = key != null ? ComposeDescription(d.Text, key) : d.Text;
            // A key named for an issue that carries none is WRITTEN (the text kept as it is):
            // reporting external_key without storing it would let a later keyed create miss it.
            else if (d.Key != null && currentKey == null) wanted["description"] = ComposeDescription((string)current["description"], d.Key);
            var patch = new JObject();
            var changes = new JArray();
            foreach (JProperty p in wanted.Properties())
            {
                string before = Scalar(current[p.Name]);
                if (Same(before, (string)p.Value, p.Name)) continue;
                patch[p.Name] = p.Value;
                changes.Add(new JObject { ["field"] = p.Name, ["before"] = before, ["after"] = p.Value });
            }
            result["issue_id"] = issueId;
            result["web_url"] = IssueWebUrl(projectId, issueId);
            result["external_key"] = key;
            result["mapped_from_finding"] = d.Sources;
            result["changes"] = changes;
            if (patch.Count == 0)
            {
                result["state"] = "no_change";
                result["host_verified"] = true;
                result["issue"] = DescribeIssue(current, projectId);
                result["note"] = "The issue already reads as requested (just read back): nothing to write, no token needed.";
                return;
            }
            // The BEFORE values are part of the plan: somebody else editing the issue between
            // the dry run and the apply invalidates the token instead of being overwritten.
            string planHash = IssueTokenStore.HashPlan("issue_update", projectId, issueId, patch.ToString(Formatting.None),
                                                         changes.ToString(Formatting.None));
            const string command = ToolName + ":issue_update";
            string docKey = "acc:" + projectId;
            if (dryRun)
            {
                IssueTokenStore.Issued c = IssueConfirmations.Issue(command, docKey, planHash);
                result["state"] = "rehearsed";
                result["plan"] = new JObject { ["method"] = "PATCH", ["path"] = "/issues/" + issueId, ["body"] = patch };
                result["confirmation_token"] = c.Token;
                result["token_expires_utc"] = c.ExpiresUtc.ToString("o", CultureInfo.InvariantCulture);
                result["note"] = "Dry run: nothing was written. Send the same arguments with dry_run=false and this confirmation_token; " +
                                 "only the fields listed in changes are sent, then the issue is read back and compared.";
                return;
            }
            RequireWriteBudget(http, "PATCH");
            string tokenProblem = IssueConfirmations.Validate(token, command, docKey, planHash);
            if (tokenProblem != null) throw new ToolRefusal(tokenProblem + " Nothing was written.");

            // A PATCH with the same values is idempotent, so a lost answer may be retried.
            CloudResponse r = http.SendJson(new HttpMethod("PATCH"), IssuesBase(projectId) + "/issues/" + Esc(issueId), patch, true);
            if (r.BudgetExhausted) throw new ToolRefusal("The call budget was spent before the PATCH: no request was made. Nothing was written.");
            bool accepted = r.Status >= 200 && r.Status < 300;
            if (!r.Ok && accepted)
                result["answer"] = "ACC accepted the PATCH (HTTP " + r.Status + ") without a usable body (" + r.Error + "); the read-back decides.";
            if (!r.Ok && !accepted)
                throw new ToolRefusal("ACC refused the update: " + r.Error + ApiDetail(r) + ". " +
                                      (r.Status == 0 || r.Status >= 500
                                          ? "It may or may not have landed; rehearse again - the dry run compares against what is there now."
                                          : "Nothing was written."));
            result["state"] = "applied";
            string sent = (string)patch["description"];
            VerifyInto(http, projectId, issueId, Comparable(patch), sent != null ? KeyOf(new JObject { ["description"] = sent }) : null, sent, result);
        }

        /// <summary>A write needs the request and its read-back: refused up front, before the token is spent.</summary>
        private const int WriteCalls = 2;

        private static void RequireWriteBudget(CdeCloudHttp http, string method)
        {
            if (http.Budget - http.Calls < WriteCalls)
                throw new ToolRefusal("max_calls=" + http.Budget + " leaves " + (http.Budget - http.Calls) + " call(s): the " + method + " and its read-back need " +
                                      WriteCalls + ". Raise max_calls. No " + method + " was sent and the confirmation_token was not spent. Nothing was written.");
        }

        // ================================================================================
        // reading and verifying
        // ================================================================================

        private static IssueScan ReadIssues(CdeCloudHttp http, string projectId, string query, int offset, int max)
        {
            var s = new IssueScan();
            int at = offset;
            for (int page = 0; page < MaxIssuePages; page++)
            {
                int want = (int)Math.Min(IssuesPage, (long)max - s.Issues.Count);
                if (want <= 0) { s.Truncated = s.Total < 0 || at < s.Total; return s; }
                // Sorted on displayId - assigned once, growing with each create - so an issue edited
                // during a multi-page scan cannot move to a page already read. To measure live.
                CloudResponse r = http.Get(IssuesBase(projectId) + "/issues?limit=" + want + "&offset=" + at + query + "&sortBy=displayId");
                if (!r.Ok) { s.Complete = false; s.Error = r.Error; return s; }
                JArray results = r.Body["results"] as JArray ?? new JArray();
                JToken total = r.Body["pagination"]?["totalResults"];
                if (total != null && total.Type == JTokenType.Integer) s.Total = (int)total;
                foreach (JObject i in results.OfType<JObject>()) s.Issues.Add(i);
                at += results.Count;
                if (results.Count == 0 || (s.Total >= 0 && at >= s.Total) || (s.Total < 0 && results.Count < want)) return s;
            }
            s.Complete = false;
            s.Error = "page limit " + MaxIssuePages + " reached; the rest was not read";
            return s;
        }

        private static JObject ReadIssue(CdeCloudHttp http, string projectId, string issueId, out string error)
        {
            CloudResponse r = http.Get(IssuesBase(projectId) + "/issues/" + Esc(issueId));
            error = r.Ok ? null : r.Error;
            JObject issue = r.Ok ? IssueBody(r.Body) : null;
            if (r.Ok && issue == null) error = "the answer carries no issue";
            return issue;
        }

        private static JObject IssueBody(JObject body)
        {
            if (body == null) return null;
            if (body["id"] != null) return body;
            if (body["data"] is JObject data && data["id"] != null) return data;
            return (body["results"] as JArray)?.OfType<JObject>().FirstOrDefault();
        }

        private static JArray ReadIssueTypes(CdeCloudHttp http, string projectId, out string error)
        {
            var types = new JArray();
            CloudResponse r = http.Get(IssuesBase(projectId) + "/issue-types?include=subtypes&limit=200");
            error = r.Ok ? null : r.Error;
            if (!r.Ok) return types;
            JArray results = r.Body["results"] as JArray ?? new JArray();
            foreach (JObject t in results.OfType<JObject>())
                types.Add(new JObject
                {
                    ["id"] = t["id"], ["title"] = t["title"], ["is_active"] = t["isActive"],
                    ["subtypes"] = new JArray((t["subtypes"] as JArray ?? new JArray()).OfType<JObject>().Select(s => new JObject
                    {
                        ["id"] = s["id"], ["title"] = s["title"], ["code"] = s["code"], ["is_active"] = s["isActive"]
                    }))
                });
            JToken total = r.Body["pagination"]?["totalResults"];
            if (total != null && total.Type == JTokenType.Integer && (int)total > results.Count)
                error = "only " + results.Count + " of " + (int)total + " issue types were read";
            return types;
        }

        private static JArray ReadRootCauses(CdeCloudHttp http, string projectId, out string error)
        {
            var categories = new JArray();
            CloudResponse r = http.Get(IssuesBase(projectId) + "/issue-root-cause-categories?include=rootcauses&limit=200");
            error = r.Ok ? null : r.Error;
            if (!r.Ok) return categories;
            foreach (JObject c in (r.Body["results"] as JArray ?? new JArray()).OfType<JObject>())
                categories.Add(new JObject
                {
                    ["id"] = c["id"], ["title"] = c["title"], ["is_active"] = c["isActive"],
                    ["root_causes"] = new JArray((c["rootCauses"] as JArray ?? new JArray()).OfType<JObject>().Select(x => new JObject
                    {
                        ["id"] = x["id"], ["title"] = x["title"], ["is_active"] = x["isActive"]
                    }))
                });
            return categories;
        }

        private static JObject FindSubtype(JArray types, string subtypeId)
        {
            foreach (JObject t in types.OfType<JObject>())
            {
                if (t["is_active"]?.Type == JTokenType.Boolean && !(bool)t["is_active"]) continue;
                foreach (JObject s in ((JArray)t["subtypes"]).OfType<JObject>())
                    if ((string)s["id"] == subtypeId && !(s["is_active"]?.Type == JTokenType.Boolean && !(bool)s["is_active"]))
                        return new JObject { ["id"] = s["id"], ["title"] = s["title"], ["code"] = s["code"], ["type_title"] = t["title"] };
            }
            return null;
        }

        private static string SubtypeList(JArray types)
        {
            var names = new List<string>();
            foreach (JObject t in types.OfType<JObject>())
                foreach (JObject s in ((JArray)t["subtypes"]).OfType<JObject>())
                    if (!(s["is_active"]?.Type == JTokenType.Boolean && !(bool)s["is_active"]))
                        names.Add("'" + (string)s["id"] + "' (" + (string)t["title"] + " / " + (string)s["title"] + ")");
            return names.Count == 0 ? "none readable" : string.Join(", ", names.Take(40));
        }

        /// <param name="description">The WHOLE description sent (marker included), compared for equality; null when none was sent.</param>
        private static void VerifyInto(CdeCloudHttp http, string projectId, string id, JObject wanted, string key, string description, JObject result)
        {
            result["issue_id"] = id;
            result["web_url"] = IssueWebUrl(projectId, id);
            string error;
            JObject back = ReadIssue(http, projectId, id, out error);
            if (back == null)
            {
                result["host_verified"] = false;
                result["verification"] = new JArray();
                result["verification_reason"] = "the issue could not be read back: " + error;
                if ((string)result["state"] == "applied") result["state"] = "applied_unverified";
                return;
            }
            var checks = new JArray();
            bool all = true;
            foreach (JProperty p in wanted.Properties())
            {
                string actual = Scalar(back[p.Name]);
                bool ok = Same(actual, (string)p.Value, p.Name);
                all &= ok;
                checks.Add(new JObject { ["field"] = p.Name, ["expected"] = p.Value, ["actual"] = actual, ["ok"] = ok });
            }
            if (key != null)
            {
                bool ok = KeyOf(back) == key;
                all &= ok;
                checks.Add(new JObject { ["field"] = "description_marker", ["expected"] = IssueMarker(key), ["ok"] = ok });
            }
            if (description != null)
            {
                // Equality, not containment: a PATCH that never landed must not pass because the
                // old text still contains the new one, and an emptied description is compared too.
                string expected = Normalize(description), actual = Normalize((string)back["description"]);
                bool ok = actual == expected;
                all &= ok;
                checks.Add(new JObject { ["field"] = "description", ["ok"] = ok, ["expected_length"] = expected.Length, ["actual_length"] = actual.Length });
            }
            if (checks.Count == 0)
            {
                all = false;
                result["verification_reason"] = "nothing sent could be compared with the read-back";
            }
            result["verification"] = checks;
            result["host_verified"] = all;
            result["display_id"] = back["displayId"];
            result["issue"] = DescribeIssue(back, projectId);
            if ((string)result["state"] == "applied" && !all) result["state"] = "applied_unverified";
        }

        /// <summary>The fields a read-back is judged on field by field: the description is compared whole, published only reported.</summary>
        private static JObject Comparable(JObject payload)
        {
            var o = (JObject)payload.DeepClone();
            o.Remove("description");
            o.Remove("published");
            return o;
        }

        private static JObject DescribeIssue(JObject i, string projectId)
        {
            string description = (string)i["description"];
            string id = (string)i["id"];
            return new JObject
            {
                ["issue_id"] = id, ["display_id"] = i["displayId"], ["title"] = i["title"], ["status"] = i["status"],
                ["issue_type_id"] = i["issueSubtypeId"], ["assigned_to"] = i["assignedTo"], ["assigned_to_type"] = i["assignedToType"],
                ["due_date"] = i["dueDate"], ["start_date"] = i["startDate"], ["location_id"] = i["locationId"],
                ["root_cause_id"] = i["rootCauseId"], ["published"] = i["published"], ["updated_at"] = i["updatedAt"],
                ["external_key"] = KeyOf(i),
                ["description_excerpt"] = description == null ? null : description.Length <= ExcerptLength ? description : description.Substring(0, ExcerptLength) + "...",
                ["web_url"] = id == null ? null : IssueWebUrl(projectId, id)
            };
        }

        internal static string KeyOf(JObject issue)
        {
            string description = (string)issue?["description"];
            if (description == null) return null;
            MatchCollection m = MarkerPattern.Matches(description);
            return m.Count == 0 ? null : m[m.Count - 1].Groups[1].Value;
        }

        internal static string ComposeDescription(string text, string key)
        {
            string t = MarkerPattern.Replace((text ?? "").Replace("\r\n", "\n"), "").TrimEnd();
            return t.Length == 0 ? IssueMarker(key) : t + "\n\n" + IssueMarker(key);
        }

        private static string Normalize(string s) => (s ?? "").Replace("\r\n", "\n").Trim();

        private static bool Same(string actual, string expected, string field)
        {
            if (actual == null || expected == null) return actual == expected;
            if (field == "dueDate" || field == "startDate")
                return actual.Length >= 10 && expected.Length >= 10 && string.Equals(actual.Substring(0, 10), expected.Substring(0, 10), StringComparison.Ordinal);
            if (field == "description") return string.Equals(Normalize(actual), Normalize(expected), StringComparison.Ordinal);
            return string.Equals(actual, expected, StringComparison.Ordinal);
        }

        private static string Scalar(JToken t)
        {
            if (t == null) return null;
            switch (t.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined: return null;
                case JTokenType.String: return (string)t;
                case JTokenType.Boolean: return (bool)t ? "true" : "false";
                case JTokenType.Integer:
                case JTokenType.Float: return Convert.ToString(((JValue)t).Value, CultureInfo.InvariantCulture);
                case JTokenType.Array:
                    var parts = ((JArray)t).Select(Scalar).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                    return parts.Count == 0 ? null : string.Join(", ", parts);
                default: return null;
            }
        }

        private static string ApiDetail(CloudResponse r) => string.IsNullOrEmpty(r.Detail) ? "" : " - ACC says: " + r.Detail;
    }

    /// <summary>
    /// The confirmation half of issue_create / issue_update, host-side: a token bound to the
    /// command, the project and a hash of the plan; single-use; ten minutes. The same rule as
    /// the add-in's Core/Confirmation.cs, kept here because the server does not link that file
    /// (its element-fingerprint half means nothing for a cloud record).
    /// </summary>
    internal sealed class IssueTokenStore
    {
        internal static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

        internal sealed class Issued
        {
            public string Token, Command, DocumentKey, PlanHash;
            public DateTime ExpiresUtc;
            public bool Used;
        }

        private readonly Dictionary<string, Issued> _issued = new Dictionary<string, Issued>(StringComparer.Ordinal);
        private readonly object _lock = new object();

        public Issued Issue(string command, string documentKey, string planHash)
        {
            var c = new Issued
            {
                Token = Guid.NewGuid().ToString("N"), Command = command, DocumentKey = documentKey, PlanHash = planHash,
                ExpiresUtc = DateTime.UtcNow + Ttl
            };
            lock (_lock)
            {
                foreach (string k in _issued.Where(p => p.Value.ExpiresUtc < DateTime.UtcNow.AddHours(-1)).Select(p => p.Key).ToList())
                    _issued.Remove(k);
                _issued[c.Token] = c;
            }
            return c;
        }

        /// <summary>Null when the token confirms exactly this plan (it is then spent); otherwise what broke and what to do.</summary>
        public string Validate(string token, string command, string documentKey, string planHash)
        {
            lock (_lock)
            {
                Issued c;
                if (token == null || !_issued.TryGetValue(token.Trim(), out c))
                    return "The confirmation_token was not issued by this server (or the server restarted since the dry run). Run the dry run again.";
                if (c.Used) return "The confirmation_token was already used: one token authorises one write. Run the dry run again.";
                if (DateTime.UtcNow > c.ExpiresUtc) return "The confirmation_token expired (" + (int)Ttl.TotalMinutes + " minutes). Run the dry run again.";
                if (c.Command != command) return "The confirmation_token was issued for " + c.Command + ", not " + command + ".";
                if (c.DocumentKey != documentKey) return "The confirmation_token was issued for another project (" + c.DocumentKey + ").";
                if (c.PlanHash != planHash)
                    return "The plan changed since the dry run (other arguments, or the issue changed in ACC meanwhile). Run the dry run again and confirm the new plan.";
                c.Used = true;
                return null;
            }
        }

        public static string HashPlan(params string[] parts)
        {
            var sb = new StringBuilder();
            foreach (string p in parts) { sb.Append(p ?? ""); sb.Append((char)31); }
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())), 0, 12).Replace("-", "").ToLowerInvariant();
        }
    }
}
