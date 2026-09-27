// -----------------------------------------------------------------------------
// horizun_cde_cloud ACC Issues (CdeCloudIssues.cs) against a FAKE HttpMessageHandler:
// every answer below is written here, no request leaves the process, no credential is
// real. What is proved: a create rehearses, applies on its token only, and is read back;
// a retry finds the key and creates nothing; an update sends only what changed; a
// 2-legged-only machine is refused with zero calls; an API error reaches the caller.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Server.Tests
{
    [Collection("HorizunSettingsRoot")]
    public sealed class CdeCloudIssuesTests : IDisposable
    {
        private const string Aps = "https://developer.api.autodesk.com";
        private const string Guid2 = "22222222-2222-2222-2222-222222222222";
        private const string Project = "b." + Guid2;
        private const string Base = Aps + "/construction/issues/v1/projects/" + Guid2;
        private const string Types = Base + "/issue-types?include=subtypes&limit=200";
        private const string Scan = Base + "/issues?limit=100&offset=0&sortBy=displayId";

        private readonly string _dir;
        private readonly string _savedRoot;
        private readonly string _userToken;

        public CdeCloudIssuesTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "hz-acc-issues-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _savedRoot = Environment.GetEnvironmentVariable(HorizunPaths.RootOverrideVariable);
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _dir);
            Profile("full_write");
            _userToken = Jwt(new JObject { ["scope"] = new JArray("data:read", "data:write"), ["client_id"] = "c", ["userid"] = "U1" });
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(HorizunPaths.RootOverrideVariable, _savedRoot);
            try { Directory.Delete(_dir, true); } catch { }
        }

        private void Profile(string profile) =>
            File.WriteAllText(HorizunPaths.SettingsPath(), @"{""permission_profile"":""" + profile + @"""}");

        private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private static string Jwt(JObject claims) => B64(@"{""alg"":""RS256""}") + "." + B64(claims.ToString()) + ".c2ln";

        // ---- the fake cloud --------------------------------------------------------------

        private sealed class FakeCloud : HttpMessageHandler
        {
            public readonly List<(string Method, string Url, string Auth, string Body)> Requests = new List<(string, string, string, string)>();
            private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _routes = new Dictionary<string, Queue<Func<HttpResponseMessage>>>(StringComparer.Ordinal);

            public void On(string method, string url, params Func<HttpResponseMessage>[] answers)
            {
                string key = method + " " + url;
                if (!_routes.TryGetValue(key, out var q)) _routes[key] = q = new Queue<Func<HttpResponseMessage>>();
                foreach (var a in answers) q.Enqueue(a);
            }

            public void Json(string method, string url, params JObject[] bodies) =>
                On(method, url, bodies.Select(b => (Func<HttpResponseMessage>)(() => Reply(HttpStatusCode.OK, b))).ToArray());

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                string body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                Requests.Add((request.Method.Method, request.RequestUri.AbsoluteUri, request.Headers.Authorization?.ToString(), body));
                if (_routes.TryGetValue(request.Method.Method + " " + request.RequestUri.AbsoluteUri, out var q) && q.Count > 0)
                    return Task.FromResult((q.Count > 1 ? q.Dequeue() : q.Peek())());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
            }
        }

        private static HttpResponseMessage Reply(HttpStatusCode code, JObject body) =>
            new HttpResponseMessage(code) { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };

        private static JObject Page(params JObject[] issues) => new JObject
        {
            ["pagination"] = new JObject { ["limit"] = 100, ["offset"] = 0, ["totalResults"] = issues.Length },
            ["results"] = new JArray(issues)
        };

        private static JObject IssueTypes() => new JObject
        {
            ["results"] = new JArray(new JObject
            {
                ["id"] = "t1", ["title"] = "Coordination", ["isActive"] = true,
                ["subtypes"] = new JArray(new JObject { ["id"] = "sub-1", ["title"] = "Clash", ["code"] = "CLH", ["isActive"] = true })
            })
        };

        private static JObject Issue(string id, string title, string status, string description, string assignee = null) => new JObject
        {
            ["id"] = id, ["displayId"] = 7, ["title"] = title, ["status"] = status, ["description"] = description,
            ["issueSubtypeId"] = "sub-1", ["assignedTo"] = assignee, ["assignedToType"] = assignee == null ? null : "user",
            ["dueDate"] = "2026-10-15", ["published"] = true
        };

        private CdeCloudEnvironment Env(FakeCloud cloud, Dictionary<string, string> vars) => new CdeCloudEnvironment
        {
            Handler = cloud,
            Variable = n => vars != null && vars.TryGetValue(n, out var v) ? v : null,
            Sleep = (d, ct) => { },
            Now = () => new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero),
            TokenFilePath = Path.Combine(_dir, "aps-token.json")
        };

        private Dictionary<string, string> UserToken() => new Dictionary<string, string> { [ApsAuth.AccessTokenName] = _userToken };

        private static JObject CreateArgs(bool dryRun, string token = null, string title = null)
        {
            var a = new JObject
            {
                ["operation"] = "issue_create", ["provider"] = "acc", ["project_id"] = Project, ["dry_run"] = dryRun,
                ["finding"] = new JObject { ["Clash ID"] = "clash-42", ["name"] = "Duct vs beam", ["level"] = "L2", ["element_a"] = "123456" },
                ["issue"] = new JObject { ["issue_type_id"] = "sub-1", ["assigned_to"] = "U9", ["due_date"] = "2026-10-15" }
            };
            if (title != null) ((JObject)a["issue"])["title"] = title;
            if (token != null) a["confirmation_token"] = token;
            return a;
        }

        private void NoSecretIn(JObject result) => Assert.DoesNotContain(_userToken, result.ToString());

        // ---- create ------------------------------------------------------------------------

        [Fact]
        public void A_create_rehearses_then_applies_on_its_token_and_is_read_back()
        {
            var cloud = new FakeCloud();
            cloud.Json("GET", Types, IssueTypes());
            cloud.Json("GET", Scan, Page());
            cloud.Json("POST", Base + "/issues", new JObject { ["id"] = "iss-1" });
            string landed = "level: L2\nelement_a: 123456\n\n[horizun-key:clash-42]";
            cloud.Json("GET", Base + "/issues/iss-1", Issue("iss-1", "Duct vs beam", "open", landed, "U9"));

            JObject rehearsal = CdeCloudTool.Handle(CreateArgs(true), CancellationToken.None, Env(cloud, UserToken()));
            Assert.Equal("rehearsed", (string)rehearsal["state"]);
            Assert.DoesNotContain(cloud.Requests, r => r.Method != "GET");
            Assert.EndsWith("[horizun-key:clash-42]", (string)rehearsal["plan"]["body"]["description"]);
            Assert.Contains("level: L2", (string)rehearsal["plan"]["body"]["description"]);
            Assert.Equal("Duct vs beam", (string)rehearsal["plan"]["body"]["title"]);
            Assert.Equal("user", (string)rehearsal["plan"]["body"]["assignedToType"]);
            Assert.Equal("user", (string)rehearsal["user_context"]);
            string token = (string)rehearsal["confirmation_token"];
            Assert.False(string.IsNullOrEmpty(token));

            JObject applied = CdeCloudTool.Handle(CreateArgs(false, token), CancellationToken.None, Env(cloud, UserToken()));
            Assert.Equal("applied", (string)applied["state"]);
            Assert.True((bool)applied["host_verified"]);
            Assert.Equal("iss-1", (string)applied["issue_id"]);
            Assert.Contains("/build/issues/projects/" + Guid2 + "/issues?issueId=iss-1", (string)applied["web_url"]);
            var post = cloud.Requests.Single(r => r.Method == "POST");
            Assert.Equal("Bearer " + _userToken, post.Auth);
            Assert.Equal("sub-1", (string)JObject.Parse(post.Body)["issueSubtypeId"]);
            Assert.Contains("[horizun-key:clash-42]", (string)JObject.Parse(post.Body)["description"]);
            Assert.StartsWith("none", (string)applied["attachments"]);
            NoSecretIn(rehearsal);
            NoSecretIn(applied);

            // The same token twice is refused: one token authorises one write.
            cloud.Json("GET", Scan, Page());
            var reused = Assert.Throws<ToolRefusal>(() => CdeCloudTool.Handle(CreateArgs(false, token), CancellationToken.None, Env(cloud, UserToken())));
            Assert.Contains("already used", reused.Message);
        }

        [Fact]
        public void A_retry_finds_the_key_and_creates_nothing()
        {
            var cloud = new FakeCloud();
            cloud.Json("GET", Types, IssueTypes());
            string there = "level: L2\nelement_a: 123456\n\n[horizun-key:clash-42]";
            cloud.Json("GET", Scan, Page(Issue("iss-1", "Duct vs beam", "open", there, "U9")));
            cloud.Json("GET", Base + "/issues/iss-1", Issue("iss-1", "Duct vs beam", "open", there, "U9"));

            // The apply of a call whose answer was lost, sent again with its old token.
            JObject r = CdeCloudTool.Handle(CreateArgs(false, "a-token-from-before"), CancellationToken.None, Env(cloud, UserToken()));
            Assert.Equal("already_exists", (string)r["state"]);
            Assert.Equal("iss-1", (string)r["issue_id"]);
            Assert.True((bool)r["host_verified"]);
            Assert.DoesNotContain(cloud.Requests, q => q.Method == "POST");
        }

        [Fact]
        public void A_scan_that_could_not_finish_blocks_the_create()
        {
            var cloud = new FakeCloud();
            cloud.Json("GET", Types, IssueTypes());
            cloud.On("GET", Scan, () => Reply(HttpStatusCode.Forbidden, new JObject()));
            JObject r = CdeCloudTool.Handle(CreateArgs(true), CancellationToken.None, Env(cloud, UserToken()));
            Assert.Null(r["confirmation_token"]);
            Assert.Contains("not proved", (string)r["apply_blocked"]);
        }

        [Fact]
        public void A_changed_plan_invalidates_the_token()
        {
            var cloud = new FakeCloud();
            cloud.Json("GET", Types, IssueTypes());
            cloud.Json("GET", Scan, Page());
            string token = (string)CdeCloudTool.Handle(CreateArgs(true), CancellationToken.None, Env(cloud, UserToken()))["confirmation_token"];
            var ex = Assert.Throws<ToolRefusal>(() =>
                CdeCloudTool.Handle(CreateArgs(false, token, "Another title"), CancellationToken.None, Env(cloud, UserToken())));
            Assert.Contains("plan changed", ex.Message);
            Assert.DoesNotContain(cloud.Requests, q => q.Method == "POST");
        }

        // ---- update ------------------------------------------------------------------------

        [Fact]
        public void An_update_sends_only_what_changed_and_is_read_back()
        {
            var cloud = new FakeCloud();
            string d = "Duct vs beam\n\n[horizun-key:clash-42]";
            JObject before = Issue("iss-1", "Duct vs beam", "open", d, "U9");
            JObject after = Issue("iss-1", "Duct vs beam", "closed", d, "U9");
            cloud.Json("GET", Base + "/issues/iss-1", before, before, after);
            cloud.Json("PATCH", Base + "/issues/iss-1", after);
            Func<bool, string, JObject> args = (dry, token) => new JObject
            {
                ["operation"] = "issue_update", ["provider"] = "acc", ["project_id"] = Project, ["issue_id"] = "iss-1",
                ["dry_run"] = dry, ["confirmation_token"] = token,
                ["issue"] = new JObject { ["status"] = "closed", ["title"] = "Duct vs beam" }
            };

            JObject rehearsal = CdeCloudTool.Handle(args(true, null), CancellationToken.None, Env(cloud, UserToken()));
            Assert.Equal("rehearsed", (string)rehearsal["state"]);
            Assert.Single((JArray)rehearsal["changes"]);
            Assert.Equal("status", (string)rehearsal["changes"][0]["field"]);

            JObject applied = CdeCloudTool.Handle(args(false, (string)rehearsal["confirmation_token"]), CancellationToken.None, Env(cloud, UserToken()));
            Assert.Equal("applied", (string)applied["state"]);
            Assert.True((bool)applied["host_verified"]);
            var patch = cloud.Requests.Single(q => q.Method == "PATCH");
            Assert.True(JToken.DeepEquals(new JObject { ["status"] = "closed" }, JObject.Parse(patch.Body)));
        }

        [Fact]
        public void An_update_that_is_already_true_writes_nothing()
        {
            var cloud = new FakeCloud();
            cloud.Json("GET", Base + "/issues/iss-1", Issue("iss-1", "Duct vs beam", "closed", "x\n\n[horizun-key:k1]"));
            JObject r = CdeCloudTool.Handle(new JObject
            {
                ["operation"] = "issue_update", ["provider"] = "acc", ["project_id"] = Project, ["issue_id"] = "iss-1",
                ["issue"] = new JObject { ["status"] = "closed" }
            }, CancellationToken.None, Env(cloud, UserToken()));
            Assert.Equal("no_change", (string)r["state"]);
            Assert.Null(r["confirmation_token"]);
            Assert.All(cloud.Requests, q => Assert.Equal("GET", q.Method));
        }

        // ---- credentials and profile -------------------------------------------------------

        [Fact]
        public void Only_two_legged_credentials_are_refused_with_the_steps_and_zero_calls()
        {
            var cloud = new FakeCloud();
            var vars = new Dictionary<string, string> { ["HORIZUN_APS_CLIENT_ID"] = "id", ["HORIZUN_APS_CLIENT_SECRET"] = "not-a-real-secret" };
            var ex = Assert.Throws<ToolRefusal>(() => CdeCloudTool.Handle(CreateArgs(true), CancellationToken.None, Env(cloud, vars)));
            Assert.Contains("3-legged", ex.Message);
            Assert.Contains("data:write", ex.Message);
            Assert.Contains("aps-token.json", ex.Message);
            Assert.DoesNotContain("not-a-real-secret", ex.Message);
            Assert.Empty(cloud.Requests);
        }

        [Fact]
        public void An_app_token_without_a_user_is_refused_before_any_issue_request()
        {
            var cloud = new FakeCloud();
            string app = Jwt(new JObject { ["scope"] = new JArray("data:read", "data:write"), ["client_id"] = "c" });
            var vars = new Dictionary<string, string> { [ApsAuth.AccessTokenName] = app };
            var ex = Assert.Throws<ToolRefusal>(() => CdeCloudTool.Handle(CreateArgs(true), CancellationToken.None, Env(cloud, vars)));
            Assert.Contains("2-legged", ex.Message);
            Assert.DoesNotContain(app, ex.Message);
            Assert.Empty(cloud.Requests);
        }

        [Fact]
        public void A_token_without_data_write_is_refused()
        {
            var cloud = new FakeCloud();
            var vars = new Dictionary<string, string> { [ApsAuth.AccessTokenName] = Jwt(new JObject { ["scope"] = new JArray("data:read"), ["userid"] = "U1" }) };
            var ex = Assert.Throws<ToolRefusal>(() => CdeCloudTool.Handle(CreateArgs(true), CancellationToken.None, Env(cloud, vars)));
            Assert.Contains("data:write", ex.Message);
            Assert.Empty(cloud.Requests);
        }

        [Fact]
        public void An_apply_below_full_write_is_refused_with_zero_calls()
        {
            Profile("safe_write");
            var cloud = new FakeCloud();
            var ex = Assert.Throws<ToolRefusal>(() => CdeCloudTool.Handle(CreateArgs(false, "t"), CancellationToken.None, Env(cloud, UserToken())));
            Assert.Contains("Nothing was written", ex.Message);
            Assert.Empty(cloud.Requests);
        }

        // ---- the API's own error -----------------------------------------------------------

        [Fact]
        public void An_api_error_on_create_reaches_the_caller()
        {
            var cloud = new FakeCloud();
            cloud.Json("GET", Types, IssueTypes());
            cloud.Json("GET", Scan, Page());
            cloud.On("POST", Base + "/issues", () => Reply((HttpStatusCode)422,
                new JObject { ["title"] = "Unprocessable", ["detail"] = "assignedTo is not a project member" }));
            string token = (string)CdeCloudTool.Handle(CreateArgs(true), CancellationToken.None, Env(cloud, UserToken()))["confirmation_token"];
            var ex = Assert.Throws<ToolRefusal>(() => CdeCloudTool.Handle(CreateArgs(false, token), CancellationToken.None, Env(cloud, UserToken())));
            Assert.Contains("HTTP 422", ex.Message);
            Assert.Contains("assignedTo is not a project member", ex.Message);
            Assert.Contains("Nothing was written", ex.Message);
            Assert.Single(cloud.Requests, q => q.Method == "POST");
        }

        [Fact]
        public void Issues_list_reads_issues_types_and_root_causes_and_the_key()
        {
            var cloud = new FakeCloud();
            cloud.Json("GET", Scan, Page(Issue("iss-1", "Duct vs beam", "open", "x\n\n[horizun-key:clash-42]")));
            cloud.Json("GET", Types, IssueTypes());
            cloud.Json("GET", Base + "/issue-root-cause-categories?include=rootcauses&limit=200", new JObject
            {
                ["results"] = new JArray(new JObject { ["id"] = "rc", ["title"] = "Design", ["rootCauses"] = new JArray(new JObject { ["id"] = "r1", ["title"] = "Coordination" }) })
            });
            JObject r = CdeCloudTool.Handle(new JObject { ["operation"] = "issues_list", ["provider"] = "acc", ["project_id"] = Project },
                CancellationToken.None, Env(cloud, UserToken()));
            Assert.True((bool)r["coverage_complete"]);
            Assert.Equal("clash-42", (string)r["issues"][0]["external_key"]);
            Assert.Equal("sub-1", (string)r["issue_types"][0]["subtypes"][0]["id"]);
            Assert.Equal("r1", (string)r["root_cause_categories"][0]["root_causes"][0]["id"]);
            Assert.All(cloud.Requests, q => Assert.Equal("GET", q.Method));
        }

        // ---- review round 1 -----------------------------------------------------------------

        [Fact]
        public void A_token_file_holding_a_read_only_token_is_refreshed_for_a_write_without_narrowing()
        {
            // The sequence the live probe runs: a read refreshed the file, and what it left is
            // data:read only. The write refreshes instead of reusing it, and the refresh asks
            // NO scope, so the original grant (with data:write) comes back.
            string readOnly = Jwt(new JObject { ["scope"] = new JArray("data:read"), ["userid"] = "U1" });
            string file = Path.Combine(_dir, "aps-token.json");
            File.WriteAllText(file, new JObject
            {
                ["access_token"] = readOnly, ["refresh_token"] = "r-1", ["expires_at"] = "2026-09-26T13:00:00Z"
            }.ToString());
            var cloud = new FakeCloud();
            cloud.Json("POST", ApsAuth.TokenUrl, new JObject
            {
                ["access_token"] = _userToken, ["refresh_token"] = "r-2", ["expires_in"] = 3600, ["token_type"] = "Bearer"
            });
            cloud.Json("GET", Types, IssueTypes());
            cloud.Json("GET", Scan, Page());
            var vars = new Dictionary<string, string> { ["HORIZUN_APS_CLIENT_ID"] = "id" };

            JObject r = CdeCloudTool.Handle(CreateArgs(true), CancellationToken.None, Env(cloud, vars));
            Assert.Equal("rehearsed", (string)r["state"]);
            Assert.True((bool)r["auth"]["token_file_refreshed"]);
            var refresh = cloud.Requests.Single(q => q.Url == ApsAuth.TokenUrl);
            Assert.Contains("grant_type=refresh_token", refresh.Body);
            Assert.DoesNotContain("scope=", refresh.Body);
            JObject stored = JObject.Parse(File.ReadAllText(file));
            Assert.Equal(_userToken, (string)stored["access_token"]);
            Assert.Equal("r-2", (string)stored["refresh_token"]);
            Assert.Equal("Bearer " + _userToken, cloud.Requests.Last().Auth);
        }

        [Fact]
        public void A_read_refresh_asks_no_scope()
        {
            string file = Path.Combine(_dir, "aps-token.json");
            File.WriteAllText(file, new JObject { ["access_token"] = "old", ["refresh_token"] = "r-1", ["expires_at"] = "2026-09-26T11:00:00Z" }.ToString());
            var cloud = new FakeCloud();
            cloud.Json("POST", ApsAuth.TokenUrl, new JObject { ["access_token"] = _userToken, ["refresh_token"] = "r-2", ["expires_in"] = 3600 });
            cloud.Json("GET", Scan, Page());
            cloud.Json("GET", Types, IssueTypes());
            cloud.Json("GET", Base + "/issue-root-cause-categories?include=rootcauses&limit=200", new JObject { ["results"] = new JArray() });
            JObject r = CdeCloudTool.Handle(new JObject { ["operation"] = "issues_list", ["provider"] = "acc", ["project_id"] = Project },
                CancellationToken.None, Env(cloud, new Dictionary<string, string> { ["HORIZUN_APS_CLIENT_ID"] = "id" }));
            Assert.True((bool)r["auth"]["token_file_refreshed"]);
            Assert.DoesNotContain("scope=", cloud.Requests.Single(q => q.Url == ApsAuth.TokenUrl).Body);
        }

        [Theory]
        [InlineData(503, "{}")]
        [InlineData(201, "")]
        public void A_lost_post_answer_is_reconciled_by_the_key(int status, string body)
        {
            var cloud = new FakeCloud();
            string landed = "level: L2\nelement_a: 123456\n\n[horizun-key:clash-42]";
            JObject issue = Issue("iss-9", "Duct vs beam", "open", landed, "U9");
            cloud.Json("GET", Types, IssueTypes());
            cloud.Json("GET", Scan, Page(), Page(), Page(issue));
            cloud.On("POST", Base + "/issues", () => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
            cloud.Json("GET", Base + "/issues/iss-9", issue);

            string token = (string)CdeCloudTool.Handle(CreateArgs(true), CancellationToken.None, Env(cloud, UserToken()))["confirmation_token"];
            JObject r = CdeCloudTool.Handle(CreateArgs(false, token), CancellationToken.None, Env(cloud, UserToken()));
            Assert.Equal("applied", (string)r["state"]);
            Assert.NotNull(r["reconciled"]);
            Assert.Equal("iss-9", (string)r["issue_id"]);
            Assert.True((bool)r["host_verified"]);
            Assert.Single(cloud.Requests, q => q.Method == "POST" && q.Url == Base + "/issues");
        }

        [Fact]
        public void A_description_patch_whose_read_back_keeps_the_old_text_is_not_verified()
        {
            var cloud = new FakeCloud();
            JObject before = Issue("iss-1", "Duct vs beam", "open", "Duct vs beam at L2 grid C4\n\n[horizun-key:k]");
            cloud.Json("GET", Base + "/issues/iss-1", before);
            cloud.Json("PATCH", Base + "/issues/iss-1", before);
            Func<bool, string, JObject> args = (dry, token) => new JObject
            {
                ["operation"] = "issue_update", ["provider"] = "acc", ["project_id"] = Project, ["issue_id"] = "iss-1",
                ["dry_run"] = dry, ["confirmation_token"] = token, ["issue"] = new JObject { ["description"] = "Duct vs beam" }
            };
            JObject rehearsal = CdeCloudTool.Handle(args(true, null), CancellationToken.None, Env(cloud, UserToken()));
            Assert.Equal("Duct vs beam\n\n[horizun-key:k]", (string)rehearsal["plan"]["body"]["description"]);
            JObject applied = CdeCloudTool.Handle(args(false, (string)rehearsal["confirmation_token"]), CancellationToken.None, Env(cloud, UserToken()));
            Assert.Equal("applied_unverified", (string)applied["state"]);
            Assert.False((bool)applied["host_verified"]);
        }

        [Fact]
        public void An_update_naming_a_key_the_issue_lacks_writes_the_marker()
        {
            var cloud = new FakeCloud();
            cloud.Json("GET", Base + "/issues/iss-1", Issue("iss-1", "Duct vs beam", "open", "Found on site"));
            JObject r = CdeCloudTool.Handle(new JObject
            {
                ["operation"] = "issue_update", ["provider"] = "acc", ["project_id"] = Project, ["issue_id"] = "iss-1",
                ["external_key"] = "k7", ["issue"] = new JObject { ["status"] = "closed" }
            }, CancellationToken.None, Env(cloud, UserToken()));
            Assert.Equal("rehearsed", (string)r["state"]);
            Assert.Equal("Found on site\n\n[horizun-key:k7]", (string)r["plan"]["body"]["description"]);
        }

        [Fact]
        public void A_call_budget_without_room_for_the_post_withholds_the_token()
        {
            var cloud = new FakeCloud();
            cloud.Json("GET", Types, IssueTypes());
            cloud.Json("GET", Scan, Page());
            JObject a = CreateArgs(true);
            a["max_calls"] = 3;   // types + one scan page leave one call: the POST and its read-back need two
            JObject r = CdeCloudTool.Handle(a, CancellationToken.None, Env(cloud, UserToken()));
            Assert.Null(r["confirmation_token"]);
            Assert.Contains("max_calls", (string)r["apply_blocked"]);
        }

        // The columns of this repo's own coordination ledger, exactly as CoordinationRules.CsvHeader
        // and CoordinationLedger.ToJson write them (pinned on the Core side by CoordinationLedgerColumnsTests).
        private static readonly string[] LedgerCsvHeader =
        {
            "finding_id", "status", "assignee", "note", "category_a", "category_b",
            "side_a", "side_b", "point_mm", "first_seen_utc", "last_seen_utc",
            "resolved_utc", "times_seen", "regression",
            "scope", "external_source", "external_issue_id", "priority", "responsible", "immovable_discipline"
        };

        [Fact]
        public void A_ledger_csv_row_and_json_row_make_an_issue()
        {
            string[] cells =
            {
                "f-0001", "open", "", "Reroute above the beam", "Ducts", "Structural Framing",
                "host|101|u1", "host|202|u2", "1000.0 2000.0 3000.0", "2026-09-01T00:00:00Z", "2026-09-20T00:00:00Z",
                "", "3", "false", "hvac-vs-structure", "", "", "high", "Mechanical", ""
            };
            var csv = new JObject();
            for (int i = 0; i < LedgerCsvHeader.Length; i++) csv[LedgerCsvHeader[i]] = cells[i];
            var json = new JObject
            {
                ["scope"] = "hvac-vs-structure", ["status"] = "open", ["side_a"] = "host|101|u1", ["side_b"] = "host|202|u2",
                ["category_a"] = "Ducts", ["category_b"] = "Structural Framing", ["first_seen_utc"] = "2026-09-01T00:00:00Z",
                ["last_seen_utc"] = "2026-09-20T00:00:00Z", ["times_seen"] = 3, ["regression"] = false,
                ["note"] = "Reroute above the beam", ["point_mm"] = new JArray(1000.0, 2000.0, 3000.0),
                ["priority"] = "high", ["responsible"] = "Mechanical", ["suggested_action"] = "move the duct", ["finding_id"] = "f-0001"
            };
            foreach (JObject row in new[] { csv, json })
            {
                var cloud = new FakeCloud();
                cloud.Json("GET", Types, IssueTypes());
                cloud.Json("GET", Scan, Page());
                JObject r = CdeCloudTool.Handle(new JObject
                {
                    ["operation"] = "issue_create", ["provider"] = "acc", ["project_id"] = Project, ["finding"] = row,
                    ["issue"] = new JObject { ["issue_type_id"] = "sub-1" }
                }, CancellationToken.None, Env(cloud, UserToken()));
                JObject body = (JObject)r["plan"]["body"];
                Assert.Equal("Clash: Ducts vs Structural Framing", (string)body["title"]);
                string d = (string)body["description"];
                Assert.StartsWith("Reroute above the beam", d);
                Assert.Contains("side_a: host|101|u1", d);
                Assert.Contains("side_b: host|202|u2", d);
                Assert.Contains("point_mm: ", d);
                Assert.Contains("responsible: Mechanical", d);
                Assert.Contains("priority: high", d);
                Assert.EndsWith("[horizun-key:f-0001]", d);
            }
        }
    }
}
