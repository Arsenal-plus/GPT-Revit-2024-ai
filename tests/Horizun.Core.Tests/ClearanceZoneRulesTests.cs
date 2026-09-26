using System.Collections.Generic;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;
using K = Horizun.Revit.Core.SpatialCoherenceRules.Kind;

namespace Horizun.Core.Tests
{
    public class ClearanceZoneRulesTests
    {
        private static ClearanceZoneRules.Rule Rule(string category = "OST_ElectricalEquipment", string face = "front",
            double depthMm = 900, double widthExtraMm = 0, double heightMm = 2000, string family = null, string type = null) =>
            new ClearanceZoneRules.Rule { Category = category, Face = face, DepthMm = depthMm, WidthExtraMm = widthExtraMm,
                                          HeightMm = heightMm, FamilyContains = family, TypeContains = type };

        // ---- Parse --------------------------------------------------------------------

        [Fact]
        public void Parse_accepts_a_well_formed_rule_and_defaults_height_and_width_extra()
        {
            var raw = JArray.Parse("[{\"category\":\"OST_ElectricalEquipment\",\"face\":\"front\",\"depth_mm\":900}]");
            var errors = new List<string>();
            List<ClearanceZoneRules.Rule> rules = ClearanceZoneRules.Parse(raw, errors);
            Assert.Empty(errors);
            Assert.Single(rules);
            Assert.Equal("OST_ElectricalEquipment", rules[0].Category);
            Assert.Equal("front", rules[0].Face);
            Assert.Equal(900, rules[0].DepthMm);
            Assert.Equal(0, rules[0].WidthExtraMm);
            Assert.Equal(ClearanceZoneRules.DefaultHeightMm, rules[0].HeightMm);
        }

        [Fact]
        public void Parse_rejects_a_category_that_is_not_a_BuiltInCategory_token()
        {
            var raw = JArray.Parse("[{\"category\":\"ElectricalEquipment\",\"depth_mm\":900}]");
            var errors = new List<string>();
            List<ClearanceZoneRules.Rule> rules = ClearanceZoneRules.Parse(raw, errors);
            Assert.Empty(rules);
            Assert.Single(errors);
            Assert.Contains("[0]", errors[0]);
        }

        [Fact]
        public void Parse_rejects_a_missing_or_non_positive_depth_and_a_bad_face()
        {
            var raw = JArray.Parse(@"[
                {""category"":""OST_ElectricalEquipment"",""depth_mm"":0},
                {""category"":""OST_ElectricalEquipment"",""depth_mm"":900,""face"":""sideways""},
                {""category"":""OST_ElectricalEquipment"",""depth_mm"":900,""width_extra_mm"":-5}
            ]");
            var errors = new List<string>();
            List<ClearanceZoneRules.Rule> rules = ClearanceZoneRules.Parse(raw, errors);
            Assert.Empty(rules);
            Assert.Equal(3, errors.Count);
        }

        [Fact]
        public void Parse_skips_only_the_malformed_entry_never_the_well_formed_ones_around_it()
        {
            var raw = JArray.Parse(@"[
                {""category"":""OST_ElectricalEquipment"",""depth_mm"":900},
                {""category"":""bad""},
                {""category"":""OST_MechanicalEquipment"",""depth_mm"":600,""face"":""all""}
            ]");
            var errors = new List<string>();
            List<ClearanceZoneRules.Rule> rules = ClearanceZoneRules.Parse(raw, errors);
            Assert.Equal(2, rules.Count);
            Assert.Single(errors);
            Assert.Contains("[1]", errors[0]);
        }

        // ---- Matches / FirstMatch --------------------------------------------------------

        [Fact]
        public void Matches_requires_the_category_and_optionally_a_case_insensitive_family_and_type_substring()
        {
            var rule = Rule(family: "panel", type: "100 A");
            Assert.True(ClearanceZoneRules.Matches(rule, "OST_ElectricalEquipment", "Lighting and Appliance PANELboard", "208V MLO: 100 A"));
            Assert.False(ClearanceZoneRules.Matches(rule, "OST_MechanicalEquipment", "Lighting and Appliance Panelboard", "100 A"));
            Assert.False(ClearanceZoneRules.Matches(rule, "OST_ElectricalEquipment", "Disconnect Switch", "100 A"));
            Assert.False(ClearanceZoneRules.Matches(rule, "OST_ElectricalEquipment", "Panelboard", "60 A"));
        }

        [Fact]
        public void FirstMatch_returns_the_first_satisfied_rule_in_declaration_order_or_null()
        {
            var rules = new List<ClearanceZoneRules.Rule> { Rule(family: "AHU"), Rule(family: "panel") };
            Assert.Same(rules[1], ClearanceZoneRules.FirstMatch(rules, "OST_ElectricalEquipment", "panelboard", null));
            Assert.Null(ClearanceZoneRules.FirstMatch(rules, "OST_ElectricalEquipment", "disconnect", null));
        }

        // ---- Project (facing/hand projection) ---------------------------------------------

        [Fact]
        public void Project_puts_the_front_face_of_an_axis_aligned_instance_at_MaxF()
        {
            // 400mm(X) x 200mm(Y) x 300mm(Z) box centred on the origin in feet, facing +X, hand +Y.
            var corners = new List<(double X, double Y, double Z)>
            {
                (-0.65, -0.33, 0), (0.65, -0.33, 0), (0.65, 0.33, 0), (-0.65, 0.33, 0),
                (-0.65, -0.33, 1.0), (0.65, -0.33, 1.0), (0.65, 0.33, 1.0), (-0.65, 0.33, 1.0)
            };
            var ext = ClearanceZoneRules.Project(0, 0, 1, 0, 0, 1, corners);
            Assert.Equal(0.65, ext.MaxF, 6);
            Assert.Equal(-0.65, ext.MinF, 6);
            Assert.Equal(0.33, ext.MaxH, 6);
            Assert.Equal(0, ext.MinZ, 6);
            Assert.Equal(1.0, ext.MaxZ, 6);
        }

        [Fact]
        public void Project_gives_the_SAME_extents_for_a_rotated_instance_in_its_own_frame()
        {
            // Same box as above, but the instance faces +Y with hand -X (rotated 90deg): the
            // corners are rotated accordingly, and Project must read the SAME F/H/Z regardless.
            var corners = new List<(double X, double Y, double Z)>
            {
                (0.33, -0.65, 0), (0.33, 0.65, 0), (-0.33, 0.65, 0), (-0.33, -0.65, 0),
                (0.33, -0.65, 1.0), (0.33, 0.65, 1.0), (-0.33, 0.65, 1.0), (-0.33, -0.65, 1.0)
            };
            var ext = ClearanceZoneRules.Project(0, 0, 0, 1, -1, 0, corners);
            Assert.Equal(0.65, ext.MaxF, 6);
            Assert.Equal(-0.65, ext.MinF, 6);
            Assert.Equal(0.33, ext.MaxH, 6);
            Assert.Equal(-0.33, ext.MinH, 6);
        }

        [Fact]
        public void Project_throws_on_a_zero_length_facing_or_hand_vector_or_no_corners()
        {
            var corners = new List<(double X, double Y, double Z)> { (0, 0, 0) };
            Assert.Throws<System.ArgumentException>(() => ClearanceZoneRules.Project(0, 0, 0, 0, 1, 0, corners));
            Assert.Throws<System.ArgumentException>(() => ClearanceZoneRules.Project(0, 0, 1, 0, 0, 0, corners));
            Assert.Throws<System.ArgumentException>(() => ClearanceZoneRules.Project(0, 0, 1, 0, 0, 1, new List<(double, double, double)>()));
        }

        // ---- Footprints ------------------------------------------------------------------

        [Fact]
        public void Footprints_front_face_starts_at_MaxF_and_is_depth_deep_widened_by_width_extra_on_each_side()
        {
            var ext = new ClearanceZoneRules.Extents(-0.65, 0.65, -0.33, 0.33, 0, 1.0);
            double depthFt = ClearanceZoneRules.FeetFromMm(900);
            double extraFt = ClearanceZoneRules.FeetFromMm(100);
            List<ClearanceZoneRules.ZoneFootprint> fps = ClearanceZoneRules.Footprints(Rule(depthMm: 900, widthExtraMm: 100), ext);
            Assert.Single(fps);
            var fp = fps[0];
            Assert.Equal("front", fp.Side);
            Assert.Equal(0.65, fp.MinF, 6);
            Assert.Equal(0.65 + depthFt, fp.MaxF, 6);
            Assert.Equal(-0.33 - extraFt, fp.MinH, 6);
            Assert.Equal(0.33 + extraFt, fp.MaxH, 6);
        }

        [Fact]
        public void Footprints_top_sits_above_MaxZ_over_the_full_footprint()
        {
            var ext = new ClearanceZoneRules.Extents(-0.65, 0.65, -0.33, 0.33, 0, 1.0);
            var fps = ClearanceZoneRules.Footprints(Rule(face: "top", depthMm: 600), ext);
            Assert.Single(fps);
            Assert.Equal("top", fps[0].Side);
            Assert.Equal(1.0, fps[0].MinZ, 6);
            Assert.Equal(1.0 + ClearanceZoneRules.FeetFromMm(600), fps[0].MaxZ, 6);
        }

        [Fact]
        public void Footprints_all_returns_the_four_sides()
        {
            var ext = new ClearanceZoneRules.Extents(-0.65, 0.65, -0.33, 0.33, 0, 1.0);
            var fps = ClearanceZoneRules.Footprints(Rule(face: "all", depthMm: 600), ext);
            Assert.Equal(4, fps.Count);
            Assert.Contains(fps, f => f.Side == "front");
            Assert.Contains(fps, f => f.Side == "back");
            Assert.Contains(fps, f => f.Side == "left");
            Assert.Contains(fps, f => f.Side == "right");
        }

        // ---- Classify ----------------------------------------------------------------

        [Fact]
        public void Classify_a_wall_invading_the_zone_is_always_an_error()
        {
            var v = ClearanceZoneRules.Classify("OST_ElectricalEquipment", new HashSet<string>(), "OST_Walls", isHost: false, sharedVolumeFt3: 0.5);
            Assert.Equal(K.Conflict, v.Kind);
            Assert.Equal("error", v.Severity);
            Assert.Contains("clearance zone", v.Reason);
        }

        [Fact]
        public void Classify_the_equipments_own_host_is_never_a_finding()
        {
            var v = ClearanceZoneRules.Classify("OST_ElectricalEquipment", new HashSet<string>(), "OST_Walls", isHost: true, sharedVolumeFt3: 5.0);
            Assert.Equal(K.None, v.Kind);
        }

        [Fact]
        public void Classify_a_small_intrusion_below_the_10L_threshold_is_ignored_like_door_clearance()
        {
            var v = ClearanceZoneRules.Classify("OST_ElectricalEquipment", new HashSet<string>(), "OST_Furniture", isHost: false,
                sharedVolumeFt3: SpatialCoherenceRules.ClearanceMinFt3 / 2);
            Assert.Equal(K.None, v.Kind);
        }

        [Fact]
        public void Classify_furniture_above_the_threshold_is_a_warning_not_an_error()
        {
            var v = ClearanceZoneRules.Classify("OST_ElectricalEquipment", new HashSet<string>(), "OST_Furniture", isHost: false,
                sharedVolumeFt3: SpatialCoherenceRules.ClearanceMinFt3 * 3);
            Assert.Equal(K.Conflict, v.Kind);
            Assert.Equal("warning", v.Severity);
        }

        [Fact]
        public void Classify_another_ruled_equipment_category_is_an_error_like_a_wall()
        {
            var ruleCats = new HashSet<string> { "OST_ElectricalEquipment", "OST_MechanicalEquipment" };
            var v = ClearanceZoneRules.Classify("OST_ElectricalEquipment", ruleCats, "OST_MechanicalEquipment", isHost: false, sharedVolumeFt3: 1.0);
            Assert.Equal("error", v.Severity);
        }

        [Fact]
        public void Classify_floors_ceilings_and_railings_are_never_an_obstacle()
        {
            Assert.Equal(K.None, ClearanceZoneRules.Classify("OST_ElectricalEquipment", new HashSet<string>(), "OST_Floors", false, 5.0).Kind);
            Assert.Equal(K.None, ClearanceZoneRules.Classify("OST_ElectricalEquipment", new HashSet<string>(), "OST_Ceilings", false, 5.0).Kind);
            Assert.Equal(K.None, ClearanceZoneRules.Classify("OST_ElectricalEquipment", new HashSet<string>(), "OST_Railings", false, 5.0).Kind);
        }

        [Fact]
        public void Classify_a_category_the_spatial_census_does_not_consider_is_never_a_finding()
        {
            Assert.Equal(K.None, ClearanceZoneRules.Classify("OST_ElectricalEquipment", new HashSet<string>(), "OST_Cameras", false, 5.0).Kind);
        }
    }
}
