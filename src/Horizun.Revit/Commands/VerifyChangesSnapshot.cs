// -----------------------------------------------------------------------------
// Horizun MCP - original Horizun code.
//
// horizun_verify_changes operation=snapshot / operation=compare_to - visual
// diff before/after, on top of the SAME temporary-view capture path the
// default operation=check already uses (a TransactionGroup around a 3D view,
// always rolled back - nothing survives in the model either way).
//
// snapshot: frames a camera (around element_ids with the check's own Frame() +
// orientation, or from a seed 3D view's own section/crop box), exports it, and
// records the PNG plus the camera Revit ACTUALLY applied (orientation, section
// box, crop box - read back from the temporary view after the commit, not the
// values we asked for) under
// %USERPROFILE%\.horizun\verify\baselines\<doc-key>\<name>.{png,json}.
//
// compare_to: rebuilds a temporary view from the STORED camera (not from
// whatever the live view looks like now), exports at the same pixel size, and
// diffs the two PNGs with Core/ImageDiff. PNG decode/encode uses WPF imaging
// (PresentationCore), which the add-in references on net48, net8 and net10
// alike - System.Drawing is NOT referenced by Horizun.Revit.csproj on net8/10
// (UseWPF only), which is why CaptureViewCalibration reads PNGs the same way.
//
// Region-to-model mapping is DELIBERATELY marked approximate: pixel boxes map
// onto the crop box's own plane through Core/CropPixelMap (a 2D projection, not
// a solid-aware unprojection). The element attribution runs the other way,
// which is what a person needs: each element changed since the baseline
// (ChangeLedger) has its bounding box projected into pixel space, and a region
// lists the ids whose projected box it touches.
// -----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using Horizun.Revit.Core;

namespace Horizun.Revit.Commands
{
    public sealed partial class VerifyChangesCommand
    {
        private const int MaxChangedElementsReported = 500;
        private const int MaxRegionsReported = 50;
        /// <summary>Pixels of slack when attributing an element box to a changed region (edges, antialiasing, the dilation itself).</summary>
        private const int RegionAttributionMarginPx = 6;

        private sealed class Camera
        {
            public ViewOrientation3D Orientation;   // exact camera; null = use OrientName
            public string OrientName = "isometric";
            public BoundingBoxXYZ Section, Crop;
            public bool FitCropToSection;           // the check's own crop framing (Picture())
        }

        private static string BaselinesDir(Document doc)
            => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".horizun", "verify", "baselines", QualityHistory.ProjectKey(doc?.Title ?? "default"));

        // ---------------------------------------------------------------- snapshot

        private static CommandResult RunSnapshot(UIApplication app, Document doc, JObject request)
        {
            string rawName = (request.Value<string>("snapshot_name") ?? "").Trim();
            if (rawName.Length == 0) return CommandResult.Fail("snapshot_name is required for operation=snapshot.");
            string name = QualityHistory.ProjectKey(rawName);
            int pixel = request.Value<int?>("pixel_size") ?? 1400;
            if (pixel < 256 || pixel > 4096) return CommandResult.Fail("pixel_size must be between 256 and 4096.");

            var cam = new Camera();
            long? seedId = null;
            JArray idsJson = request["element_ids"] as JArray;
            if (idsJson != null && idsJson.Count > 0)
            {
                // Framed around elements: the same Frame() + Orient() + crop fit the check uses.
                var framed = new List<Element>();
                foreach (JToken t in idsJson)
                {
                    long id = t.Value<long>();
                    Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                    if (e == null) return CommandResult.Fail("element_ids: " + id + " does not resolve to an element in this document.");
                    framed.Add(e);
                }
                try { cam.Section = Frame(framed); }
                catch (Exception ex) { return CommandResult.Fail("Cannot frame element_ids: " + ex.Message); }
                cam.OrientName = request.Value<string>("orientation") ?? "isometric";
                cam.FitCropToSection = true;
            }
            else
            {
                View3D seed = ResolveSeedView3D(app, doc, request, out CommandResult viewError);
                if (viewError != null) return viewError;
                seedId = Rid.Value(seed.Id);
                cam.Orientation = seed.GetOrientation();
                cam.Crop = seed.CropBoxActive ? seed.CropBox : null;
                cam.Section = seed.IsSectionBoxActive ? seed.GetSectionBox() : null;
                if (cam.Crop == null && cam.Section == null)
                    return CommandResult.Fail("The seed view has no active crop region and no active section box, so the same " +
                        "frame cannot be reproduced later (Revit would refit to the model's extents). Pass element_ids to frame " +
                        "around, or activate a crop/section box on the view.");
                cam.FitCropToSection = cam.Crop == null;
            }

            string exportedPng, rollback; Camera applied;
            try { exportedPng = ExportCamera(doc, cam, pixel, out rollback, out applied); }
            catch (Exception ex) { return CommandResult.Fail("Could not capture the snapshot: " + ex.Message); }
            if (exportedPng == null) return CommandResult.Fail("ExportImage produced no file for the snapshot.");
            if (applied.Crop == null && applied.Section == null)
                return CommandResult.Fail("Revit applied neither a crop nor a section box to the snapshot view; it could not be reproduced.");

            var camera = new JObject
            {
                ["seed_view_id"] = seedId,
                ["pixel_size"] = pixel,
                ["captured_at_utc"] = DateTime.UtcNow.ToString("o"),
                ["eye"] = PointJson(applied.Orientation.EyePosition),
                ["up"] = PointJson(applied.Orientation.UpDirection),
                ["forward"] = PointJson(applied.Orientation.ForwardDirection),
                ["section_box"] = applied.Section == null ? null : BoxJson(applied.Section),
                ["crop_box"] = applied.Crop == null ? null : BoxJson(applied.Crop)
            };

            string dir = BaselinesDir(doc);
            string pngPath = Path.Combine(dir, name + ".png");
            string jsonPath = Path.Combine(dir, name + ".json");
            try
            {
                Directory.CreateDirectory(dir);
                File.Copy(exportedPng, pngPath, true);
                File.WriteAllText(jsonPath, camera.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch (Exception ex) { return CommandResult.Fail("Could not save the baseline: " + ex.Message); }

            // FileArtifactReread: re-read what was written - the PNG must decode and the
            // camera must parse back - never trust the copy that "did not throw".
            int bw = 0, bh = 0; string reread = null;
            try { DecodePng(pngPath, out _, out bw, out bh); JObject.Parse(File.ReadAllText(jsonPath)); }
            catch (Exception ex) { reread = ex.Message; }
            var result = new JObject
            {
                ["status"] = reread == null ? "ok" : "failed",
                ["operation"] = "snapshot",
                ["snapshot_name"] = name,
                ["baseline_png"] = pngPath,
                ["baseline_camera"] = jsonPath,
                ["framed_from"] = seedId.HasValue ? "seed_view" : "element_ids",
                ["seed_view_id"] = seedId,
                ["pixel_size"] = pixel,
                ["width"] = bw,
                ["height"] = bh,
                ["temporary_view_rollback"] = rollback,
                ["read_only"] = false
            };
            if (reread != null)
                return CommandResult.FailWithDetail("The snapshot did not verify after writing: " + reread,
                    new JObject { ["code"] = "snapshot_not_verified", ["result"] = result });
            result["next"] = "After modelling, call operation=compare_to with snapshot_name='" + name + "' to see what changed.";
            return CommandResult.Ok(result);
        }

        // ---------------------------------------------------------------- compare_to

        private static CommandResult RunCompareTo(UIApplication app, Document doc, JObject request)
        {
            string rawName = (request.Value<string>("snapshot_name") ?? "").Trim();
            if (rawName.Length == 0) return CommandResult.Fail("snapshot_name is required for operation=compare_to.");
            string name = QualityHistory.ProjectKey(rawName);
            string dir = BaselinesDir(doc);
            string pngPath = Path.Combine(dir, name + ".png");
            string jsonPath = Path.Combine(dir, name + ".json");
            if (!File.Exists(pngPath) || !File.Exists(jsonPath))
                return CommandResult.Fail("No baseline named '" + name + "' for this document. Call operation=snapshot first.");

            JObject camera;
            try { camera = JObject.Parse(File.ReadAllText(jsonPath)); }
            catch (Exception ex) { return CommandResult.Fail("Could not read the baseline camera (" + jsonPath + "): " + ex.Message); }

            int pixel = camera.Value<int?>("pixel_size") ?? 1400;
            var cam = new Camera();
            try
            {
                cam.Orientation = new ViewOrientation3D(PointFromJson(camera["eye"]), PointFromJson(camera["up"]), PointFromJson(camera["forward"]));
                cam.Crop = BoxFromJson(camera["crop_box"] as JObject);
                cam.Section = BoxFromJson(camera["section_box"] as JObject);
            }
            catch (Exception ex) { return CommandResult.Fail("The baseline camera is corrupt: " + ex.Message); }
            if (cam.Crop == null && cam.Section == null)
                return CommandResult.Fail("The baseline camera has neither a crop region nor a section box; it cannot be reproduced.");

            string afterPng, rollback; Camera applied;
            try { afterPng = ExportCamera(doc, cam, pixel, out rollback, out applied); }
            catch (Exception ex) { return CommandResult.Fail("Could not recapture the baseline's camera: " + ex.Message); }
            if (afterPng == null) return CommandResult.Fail("ExportImage produced no file for the recapture.");

            int[] beforePixels, afterPixels; int bw, bh, aw, ah;
            try { DecodePng(pngPath, out beforePixels, out bw, out bh); }
            catch (Exception ex) { return CommandResult.Fail("Could not decode the baseline PNG: " + ex.Message); }
            try { DecodePng(afterPng, out afterPixels, out aw, out ah); }
            catch (Exception ex) { return CommandResult.Fail("Could not decode the recaptured PNG: " + ex.Message); }
            if (bw != aw || bh != ah)
                return CommandResult.FailWithDetail("The recapture is " + aw + "x" + ah + " but the baseline is " + bw + "x" + bh +
                    ": the same camera did not reproduce the same frame, so a pixel diff would be meaningless.",
                    new JObject { ["code"] = "frame_not_reproduced", ["after_path"] = afterPng, ["before_path"] = pngPath });

            ImageDiffResult diff = ImageDiff.Compare(beforePixels, afterPixels, aw, ah);
            int[] overlaid = ImageDiff.Overlay(afterPixels, diff.Mask, aw, ah);

            string outDir = Path.Combine(Path.GetTempPath(), "Horizun", "verify", "diff", Guid.NewGuid().ToString("N"));
            string beforeOut = Path.Combine(outDir, "before.png"), afterOut = Path.Combine(outDir, "after.png"), diffOut = Path.Combine(outDir, "diff.png");
            try
            {
                Directory.CreateDirectory(outDir);
                File.Copy(pngPath, beforeOut, true);
                File.Copy(afterPng, afterOut, true);
                EncodePng(overlaid, aw, ah, diffOut);
            }
            catch (Exception ex) { return CommandResult.Fail("Could not write the diff images: " + ex.Message); }

            // Elements changed since the baseline (ChangeLedger: Horizun writes only).
            var changedIds = new List<long>();
            if (DateTime.TryParse(camera.Value<string>("captured_at_utc"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime sinceUtc))
                foreach (ChangeLedger.Entry h in ChangeLedger.HistoryFor(doc).Where(h => h.AtUtc >= sinceUtc))
                    changedIds.AddRange(h.Added.Concat(h.Modified));
            changedIds = changedIds.Distinct().ToList();

            // Pixel <-> crop plane map, from the crop Revit applied to THIS recapture.
            CropPixelMap map = null; Transform cropT = null; BoundingBoxXYZ crop = applied.Crop ?? cam.Crop;
            if (crop != null)
                try { map = new CropPixelMap(crop.Min.X, crop.Max.X, crop.Max.Y, aw, ah); cropT = crop.Transform ?? Transform.Identity; } catch { map = null; }

            var elementBoxes = new Dictionary<long, int[]>();
            if (map != null)
            {
                Transform toLocal = cropT.Inverse;
                foreach (long id in changedIds.Take(MaxChangedElementsReported))
                {
                    Element e = Rid.CanRepresent(id) ? doc.GetElement(Rid.Make(id)) : null;
                    BoundingBoxXYZ b = null;
                    try { b = e?.get_BoundingBox(null); } catch { }
                    if (b == null) continue;
                    var pts = new List<double[]>();
                    for (int c = 0; c < 8; c++)
                    {
                        XYZ p = toLocal.OfPoint(new XYZ((c & 1) == 0 ? b.Min.X : b.Max.X, (c & 2) == 0 ? b.Min.Y : b.Max.Y, (c & 4) == 0 ? b.Min.Z : b.Max.Z));
                        pts.Add(new[] { p.X, p.Y });
                    }
                    int[] box = map.PixelBox(pts);
                    if (box != null) elementBoxes[id] = box;
                }
            }

            var regions = new JArray();
            foreach (ImageDiffRegion r in diff.Regions.Take(MaxRegionsReported))
            {
                var rj = new JObject { ["pixel_bbox"] = new JArray(r.MinX, r.MinY, r.MaxX, r.MaxY), ["pixel_count"] = r.PixelCount };
                if (map != null)
                {
                    map.ToLocal(r.MinX, r.MinY, out double lx0, out double ly1);
                    map.ToLocal(r.MaxX + 1, r.MaxY + 1, out double lx1, out double ly0);
                    double lz = crop.Min.Z;
                    XYZ[] corners = { cropT.OfPoint(new XYZ(lx0, ly0, lz)), cropT.OfPoint(new XYZ(lx1, ly0, lz)), cropT.OfPoint(new XYZ(lx0, ly1, lz)), cropT.OfPoint(new XYZ(lx1, ly1, lz)) };
                    rj["model_bbox_approx"] = new JObject
                    {
                        ["min"] = new JArray(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z)),
                        ["max"] = new JArray(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z)),
                        ["units"] = "feet",
                        ["note"] = "projection onto the crop plane, not a 3D unprojection"
                    };
                    rj["element_ids"] = new JArray(elementBoxes.Where(kv => CropPixelMap.Overlaps(r, kv.Value, RegionAttributionMarginPx)).Select(kv => kv.Key));
                }
                regions.Add(rj);
            }

            var result = new JObject
            {
                ["status"] = "ok",
                ["operation"] = "compare_to",
                ["snapshot_name"] = name,
                ["before_path"] = beforeOut,
                ["after_path"] = afterOut,
                ["diff_path"] = diffOut,
                ["changed_pixel_count"] = diff.ChangedPixelCount,
                ["changed_pixel_ratio"] = diff.ChangedPixelRatio,
                ["width"] = aw,
                ["height"] = ah,
                ["regions"] = regions,
                ["regions_truncated"] = diff.Regions.Count > MaxRegionsReported,
                ["elements_changed_since_baseline"] = new JArray(changedIds.Take(MaxChangedElementsReported)),
                ["elements_changed_since_baseline_truncated"] = changedIds.Count > MaxChangedElementsReported,
                ["elements_changed_scope"] = "Horizun writes recorded since the baseline in this Revit session (manual edits are not in the ledger)",
                ["temporary_view_rollback"] = rollback,
                ["read_only"] = false
            };
            result["next"] = diff.ChangedPixelRatio > 0
                ? "changed_pixel_ratio > 0: open diff_path (changed pixels in red over the after image) and after_path."
                : "changed_pixel_ratio is 0: nothing visible changed at this camera.";
            return CommandResult.Ok(result);
        }

        // ---------------------------------------------------------------- shared: view resolution, capture, camera json

        private static View3D ResolveSeedView3D(UIApplication app, Document doc, JObject request, out CommandResult error)
        {
            error = null;
            long? viewId = request.Value<long?>("view_id");
            if (viewId.HasValue)
            {
                View3D v = Rid.CanRepresent(viewId.Value) ? doc.GetElement(Rid.Make(viewId.Value)) as View3D : null;
                if (v == null || v.IsTemplate) { error = CommandResult.Fail("view_id does not resolve to a 3D view in this document."); return null; }
                return v;
            }
            try
            {
                if (app?.ActiveUIDocument?.Document != null && app.ActiveUIDocument.Document.Equals(doc) && app.ActiveUIDocument.ActiveView is View3D active)
                    return active;
            }
            catch { }
            error = CommandResult.Fail("operation=snapshot needs element_ids to frame around, a view_id of a 3D view, or an active 3D view.");
            return null;
        }

        /// <summary>
        /// The capture path of operation=check's Picture(): a temporary View3D inside a
        /// TransactionGroup that is ALWAYS rolled back. The camera is either given exactly
        /// (compare_to, seed view) or framed like Picture() (element_ids); either way the
        /// camera Revit actually applied is read back so it can be stored and reproduced.
        /// </summary>
        private static string ExportCamera(Document doc, Camera cam, int pixel, out string rollback, out Camera applied)
        {
            rollback = "not_attempted";
            applied = new Camera();
            string dir = Path.Combine(Path.GetTempPath(), "Horizun", "verify", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            using (var group = new TransactionGroup(doc, "Horizun: verify changes (temporary view)"))
            {
                try
                {
                    if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("the temporary transaction group did not start");
                    View3D view;
                    using (var tx = new Transaction(doc, "Horizun: fixed-camera verification view"))
                    {
                        tx.Start();
                        ViewFamilyType vft = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                            .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional)
                            ?? throw new InvalidOperationException("the model has no 3D view type");
                        view = View3D.CreateIsometric(doc, vft.Id);
                        try { view.DisplayStyle = DisplayStyle.ShadingWithEdges; } catch { }
                        try { view.DetailLevel = ViewDetailLevel.Fine; } catch { }
                        if (cam.Orientation != null) view.SetOrientation(cam.Orientation); else Orient(view, cam.OrientName);
                        if (cam.Section != null) { view.SetSectionBox(cam.Section); view.IsSectionBoxActive = true; }
                        doc.Regenerate();
                        if (cam.Crop != null) { view.CropBox = cam.Crop; view.CropBoxActive = true; view.CropBoxVisible = false; }
                        else if (cam.FitCropToSection && cam.Section != null)
                        {
                            // Same crop fit as Picture(): the section box corners in view coordinates.
                            BoundingBoxXYZ crop = view.CropBox;
                            Transform toView = crop.Transform.Inverse;
                            Transform secT = cam.Section.Transform ?? Transform.Identity;
                            var pts = new List<XYZ>();
                            for (int c = 0; c < 8; c++)
                                pts.Add(toView.OfPoint(secT.OfPoint(new XYZ((c & 1) == 0 ? cam.Section.Min.X : cam.Section.Max.X,
                                    (c & 2) == 0 ? cam.Section.Min.Y : cam.Section.Max.Y, (c & 4) == 0 ? cam.Section.Min.Z : cam.Section.Max.Z))));
                            double pad = 0.5;
                            crop.Min = new XYZ(pts.Min(q => q.X) - pad, pts.Min(q => q.Y) - pad, crop.Min.Z);
                            crop.Max = new XYZ(pts.Max(q => q.X) + pad, pts.Max(q => q.Y) + pad, crop.Max.Z);
                            view.CropBox = crop; view.CropBoxActive = true; view.CropBoxVisible = false;
                        }
                        foreach (Category c in doc.Settings.Categories)
                            if (c.CategoryType == CategoryType.Annotation && view.CanCategoryBeHidden(c.Id))
                                try { view.SetCategoryHidden(c.Id, true); } catch { }
                        tx.Commit();
                    }
                    // Read back what Revit applied: this, not the request, is the camera to store.
                    applied.Orientation = view.GetOrientation();
                    applied.Section = view.IsSectionBoxActive ? view.GetSectionBox() : null;
                    applied.Crop = view.CropBoxActive ? view.CropBox : null;
                    var opts = new ImageExportOptions
                    {
                        ExportRange = ExportRange.SetOfViews, FilePath = Path.Combine(dir, "verify"),
                        HLRandWFViewsFileType = ImageFileType.PNG, ShadowViewsFileType = ImageFileType.PNG,
                        ZoomType = ZoomFitType.FitToPage, FitDirection = FitDirectionType.Horizontal,
                        ImageResolution = ImageResolution.DPI_150
                    };
                    try { opts.PixelSize = pixel; } catch { }
                    opts.SetViewsAndSheets(new List<ElementId> { view.Id });
                    doc.ExportImage(opts);
                }
                finally
                {
                    try { if (group.GetStatus() == TransactionStatus.Started) rollback = Guard.RollBack(group).StatusName; }
                    catch (Exception ex) { rollback = "failed: " + ex.Message; }
                }
            }
            return Directory.GetFiles(dir, "*.png").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }

        private static JArray PointJson(XYZ p) => new JArray(p.X, p.Y, p.Z);
        private static XYZ PointFromJson(JToken t) => t is JArray a && a.Count == 3 ? new XYZ(a[0].Value<double>(), a[1].Value<double>(), a[2].Value<double>()) : throw new InvalidOperationException("expected a 3-element point array");

        private static JObject BoxJson(BoundingBoxXYZ b)
        {
            Transform t = b.Transform ?? Transform.Identity;
            return new JObject
            {
                ["min"] = PointJson(b.Min), ["max"] = PointJson(b.Max),
                ["origin"] = PointJson(t.Origin), ["basis_x"] = PointJson(t.BasisX),
                ["basis_y"] = PointJson(t.BasisY), ["basis_z"] = PointJson(t.BasisZ)
            };
        }

        private static BoundingBoxXYZ BoxFromJson(JObject j)
        {
            if (j == null) return null;
            var b = new BoundingBoxXYZ { Min = PointFromJson(j["min"]), Max = PointFromJson(j["max"]) };
            Transform t = Transform.CreateTranslation(XYZ.Zero);
            t.Origin = PointFromJson(j["origin"]);
            t.BasisX = PointFromJson(j["basis_x"]);
            t.BasisY = PointFromJson(j["basis_y"]);
            t.BasisZ = PointFromJson(j["basis_z"]);
            b.Transform = t;
            return b;
        }

        // ---------------------------------------------------------------- PNG <-> ARGB (WPF imaging; add-in side only)

        /// <summary>Bgra32 bytes read as little-endian ints are 0xAARRGGBB - exactly what Core/ImageDiff expects.</summary>
        private static void DecodePng(string path, out int[] pixels, out int width, out int height)
        {
            using (var stream = File.OpenRead(path))
            {
                BitmapFrame frame = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                width = frame.PixelWidth; height = frame.PixelHeight;
                var bgra = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
                pixels = new int[checked(width * height)];
                bgra.CopyPixels(pixels, width * 4, 0);
            }
        }

        private static void EncodePng(int[] pixels, int width, int height, string path)
        {
            BitmapSource src = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(src));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
    }
}
