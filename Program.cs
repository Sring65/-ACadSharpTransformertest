// dwgReadWrite -- ACadSharp coordinate transform + all-version DWG save + per-class clone + various read/write modes
//
// This program is a "read/write all modes + per-class clone" experiment bench for sample.dwg (test project, not committed to git).
//
// Default (no-args) pipeline:
//   1) Read sample.dwg;
//   2) Apply one coordinate transform (scale + rotate about the origin + translate) to all model-space entities;
//   3) Save as "all supported DWG versions" (AC1014~AC1032, 7 in total);
//   4) Read back and verify the entity count of each version;
//   5) Full clone into a brand-new DWG (single file);
//   6) Per-class clone: for each entity type in model space, clone that type into its own brand-new blank DWG, one file each
//      (keep associations: Layer/Block/Style/TableStyle are cloned and re-registered together);
//   7) Association-drop verification: clone a single Insert alone (Block set to null) to verify "cloning one entity alone drops its associations by default".
//
// Command-line modes (restored from the deleted CadTransformer experiment code):
//   dwgReadWrite <mode> [args...]
//     empty <version> [out.dwg]            Write an empty document of the given version
//     rt <in.dwg> <out.dwg> [version]       Pure round-trip (no coordinate transform)
//     stripvp <NEW|in.dwg> <out.dwg> [version]  Strip all viewports, then write out
//     dump <file.dwg>                   Object histogram (including viewport/Insert details)
//     classes <file.dwg>                Class table dump
//     objinfo <file.dwg|dxf> <0x..> [..|all]  Look up objects by handle
//     layoutdump <file.dwg|dxf>         Layout/viewport/HANDSEED in-memory state histogram
//     dimstyles <file.dwg>              Dimension styles and dimension references
//     inspect <file.dwg> [0x..]         Table/Insert/ATT state histogram + lookup by handle
//     inspecthatch <file.dwg>           HATCH gradient/XDATA details
//     inspectdim <file.dwg>             Dimension association (DIMASSOC) chain check
//     iobench                          IO performance benchmark (streaming read/write / incremental save / parallel decode / lazy load / progress events; step (9) of the default pipeline)
//     readbench [in.dwg] [N] [noid]    DWG read phase-breakdown benchmark (decode vs build sub-phases; default io_bench/bench_io.dwg x5)
//   (With no mode argument the default pipeline above runs; if the first argument is a file path, the default pipeline also runs)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Security.Cryptography;
using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Extensions;
using ACadSharp.IO;
using ACadSharp.IO.DWG;
using ACadSharp.Objects;
using ACadSharp.Tables;
using ACadSharp.Exceptions;
using ACadSharp.XData;
using CSMath;

internal static class Program
{
    // ---------- Coordinate transform parameters (scale / rotate / translate) ----------
    private static readonly XYZ  TransformScale     = new XYZ(1.5, 1.5, 1.5);
    private static readonly double TransformAngleDeg = 30.0;
    private static readonly XYZ  TransformTranslate = new XYZ(1000.0, 500.0, 0);

    // ---------- Supported DWG write versions (the set DwgWriter can actually write: AC1014~AC1032, 7 in total) ----------
    private static readonly (string Name, ACadVersion Version)[] SupportedVersions =
    {
        ("R14/AC1014",   ACadVersion.AC1014),
        ("R2000/AC1015", ACadVersion.AC1015),
        ("R2004/AC1018", ACadVersion.AC1018),
        ("R2007/AC1021", ACadVersion.AC1021),
        ("R2010/AC1024", ACadVersion.AC1024),
        ("R2013/AC1027", ACadVersion.AC1027),
        ("R2018/AC1032", ACadVersion.AC1032),
    };

    private static readonly List<MethodResult> _results = new List<MethodResult>();

    private static void Main(string[] args)
    {
        EnsureUtf8();
        GCSettings.LatencyMode = GCLatencyMode.Batch;

        string baseDir = AppContext.BaseDirectory;
        string samplePath = Path.Combine(baseDir, "sample.dwg");

        // ---------- Mode dispatch (restored "various modes" read/write) ----------
        if (args.Length > 0)
        {
            string mode = args[0];
            if (TryRunMode(mode, args, baseDir, samplePath))
                return;
            // No mode matched (e.g. the first argument is a file path) -> run the default pipeline
        }

        RunDefaultPipeline(samplePath, baseDir);
        PrintReport();
        Console.WriteLine("\nDone.");
    }

    // ================= Default pipeline: transform + all-version save + full clone + per-class clone =================
    private static void RunDefaultPipeline(string samplePath, string baseDir)
    {
        Console.WriteLine("==========================================================");
        Console.WriteLine("  ACadSharp coordinate transform + all-version DWG save + per-class clone");
        Console.WriteLine($"  Transform = scale {TransformScale.X}x{TransformScale.Y}x{TransformScale.Z}" +
                          $"  rotate {TransformAngleDeg} (about Z)  translate ({TransformTranslate.X},{TransformTranslate.Y},{TransformTranslate.Z})");
        Console.WriteLine($"  Supported versions = {string.Join(", ", SupportedVersions.Select(v => v.Version))}");
        Console.WriteLine($"  Run time = {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine("==========================================================\n");

        if (!File.Exists(samplePath))
        {
            Console.Error.WriteLine($"❌ sample.dwg not found: {samplePath}");
            return;
        }

        // ---------- (1) Read sample.dwg ----------
        var readSw = Stopwatch.StartNew();
        long readWsStart = WsMB();
        CadDocument doc;
        Console.WriteLine("▶ [Read] sample.dwg");
        try
        {
            var reader = new DwgReader(samplePath)
            {
                Configuration = new DwgReaderConfiguration { ReadSummaryInfo = true }
            };
            reader.OnProgress += (s, e) => ProgressBar("Reading", e.Completed, e.Total);
            doc = reader.Read();
            readSw.Stop();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ Read failed: {ex}");
            return;
        }
        long readWsEnd = WsMB();
        int modelCount = doc.ModelSpace.Entities.Count;
        Console.WriteLine();
        Console.WriteLine($"  ✅ Read done: version={doc.Header.Version}, model-space entities={modelCount:N0}, blocks={doc.BlockRecords.Count}, layers={doc.Layers.Count}");
        Console.WriteLine($"  Read elapsed={readSw.ElapsedMilliseconds:N0} ms, memory {readWsStart:N0}->{readWsEnd:N0} MB\n");
        _results.Add(new MethodResult("Read sample.dwg", readSw.ElapsedMilliseconds, readWsStart, readWsEnd, 0, "OK"));

        // ---------- (2) Per-class clone (before the transform: "keep associations" clone of the original entities, one file each) ----------
        RunPerClassClone(doc, baseDir);

        // ---------- (3) Association-drop verification (Insert with Block set to null) ----------
        RunDropAssociationTest(doc, baseDir);

        // ---------- (4) Coordinate transform ----------
        var tfSw = Stopwatch.StartNew();
        long tfWsStart = WsMB();
        double angleRad = TransformAngleDeg * Math.PI / 180.0;
        Transform t = new Transform(TransformTranslate, TransformScale, new XYZ(0, 0, angleRad));
        foreach (Entity e in doc.ModelSpace.Entities)
            e.ApplyTransform(t);
        tfSw.Stop();
        long tfWsEnd = WsMB();
        Console.WriteLine($"▶ [Transform] Apply the coordinate transform to {modelCount:N0} model-space entities");
        Console.WriteLine($"  ✅ Transform done: elapsed={tfSw.ElapsedMilliseconds:N0} ms, memory {tfWsStart:N0}->{tfWsEnd:N0} MB\n");
        _results.Add(new MethodResult("Coordinate transform", tfSw.ElapsedMilliseconds, tfWsStart, tfWsEnd, 0, "OK"));

        // ---------- (5) Recompute the model space extents ----------
        SetModelExtentsFromEntities(doc);

        // ---------- (6) Save as all supported DWG versions ----------
        Console.WriteLine($"▶ [Save] Write {SupportedVersions.Length} DWGs, one per version (the same transformed document)");
        foreach (var v in SupportedVersions)
            SaveVersion(v.Name, v.Version, doc, baseDir);
        Console.WriteLine();

        // ---------- (7) Read back and verify the entity count of each version ----------
        Console.WriteLine("▶ [Readback check] Entity counts of the per-version outputs (should be ~ the pre-transform entity count)");
        ValidateOutputs(baseDir, modelCount);

        // ---------- (8) Full clone into a brand-new DWG (single file) ----------
        RunFullCloneTest(samplePath, baseDir);

        // ---------- (9) IO performance benchmark (streaming read/write / incremental save / parallel decode / lazy load / progress events) ----------
        RunIoBench(baseDir);
    }

    // ================= Single-version save =================
    private static void SaveVersion(string name, ACadVersion version, CadDocument doc, string baseDir)
    {
        string outPath = Path.Combine(baseDir, $"out_{name.Replace('/', '_')}.dwg");
        long ws0 = WsMB();
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"  ▶ {name,-14} -> {Path.GetFileName(outPath)}");
        try
        {
            doc.Header.Version = version;
            var writer = new DwgWriter(outPath, doc) { Configuration = new DwgWriterConfiguration() };
            // Write progress bar: a mirror of the reader-side OnProgress bar
            // (throttled by SaveProgressReportInterval; guaranteed to fire once at 100% when Completed==Total)
            writer.OnSaveProgress += (s, e) => ProgressBar("Writing", e.Completed, e.Total);
            writer.Write();
            writer.Dispose();
            sw.Stop();
            long ws1 = WsMB();
            long sz = new FileInfo(outPath).Length;
            Console.WriteLine();
            Console.WriteLine($"      ✅ {sw.ElapsedMilliseconds:N0} ms, memory {ws0:N0}->{ws1:N0} MB, output {sz / 1024.0 / 1024.0:F2} MB");
            _results.Add(new MethodResult($"Save {name}", sw.ElapsedMilliseconds, ws0, ws1, sz, "OK"));
        }
        catch (CadNotSupportedException ex)
        {
            sw.Stop();
            Console.WriteLine($"      ⚠️  Not supported: {ex.Message}");
            _results.Add(new MethodResult($"Save {name}", sw.ElapsedMilliseconds, ws0, WsMB(), 0, "UNSUPPORTED"));
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"      ❌ Failed: {ex.Message}");
            _results.Add(new MethodResult($"Save {name}", sw.ElapsedMilliseconds, ws0, WsMB(), 0, "FAIL"));
        }
    }

    // ================= Full clone (read the whole object graph -> write a new file) =================
    private static void RunFullCloneTest(string samplePath, string baseDir)
    {
        string outPath = Path.Combine(baseDir, "out_fullclone.dwg");
        Console.WriteLine("\n▶ [Clone] Full clone sample.dwg -> new file " + Path.GetFileName(outPath));
        var sw = Stopwatch.StartNew();
        long ws0 = WsMB();
        try
        {
            var reader = new DwgReader(samplePath) { Configuration = new DwgReaderConfiguration { ReadSummaryInfo = true } };
            CadDocument src = reader.Read();
            sw.Stop();
            long ws1 = WsMB();
            Console.WriteLine($"  (1) Opened: version={src.Header.Version}, entities={src.Entities.Count:N0}, blocks={src.BlockRecords.Count:N0}, layers={src.Layers.Count:N0}");

            var sw2 = Stopwatch.StartNew();
            long ws2 = WsMB();
            var writer = new DwgWriter(outPath, src) { Configuration = new DwgWriterConfiguration() };
            writer.Write();
            writer.Dispose();
            sw2.Stop();
            long ws3 = WsMB();
            long sz = new FileInfo(outPath).Length;
            Console.WriteLine($"  (2) Clone write done: elapsed={sw2.ElapsedMilliseconds:N0} ms, memory {ws2:N0}->{ws3:N0} MB, output {sz / 1024.0 / 1024.0:F2} MB");
            _results.Add(new MethodResult("Full clone (read)", sw.ElapsedMilliseconds, ws0, ws1, 0, "OK"));
            _results.Add(new MethodResult("Full clone (write)", sw2.ElapsedMilliseconds, ws2, ws3, sz, "OK"));

            CadDocument rb = new DwgReader(outPath).Read();
            bool ok = rb.Entities.Count == src.Entities.Count
                      && rb.ModelSpace.Entities.Count == src.ModelSpace.Entities.Count
                      && rb.BlockRecords.Count == src.BlockRecords.Count
                      && rb.Layers.Count == src.Layers.Count;
            Console.WriteLine($"  (3) Readback check: entities={rb.Entities.Count:N0} (source {src.Entities.Count:N0}), blocks={rb.BlockRecords.Count:N0} (source {src.BlockRecords.Count:N0}) {(ok ? "✅ match" : "⚠️ mismatch")}");
            if (!ok) _results.Add(new MethodResult("Full clone (check)", 0, 0, 0, 0, "MISMATCH"));
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.Error.WriteLine($"  ❌ Clone failed: {ex}");
            _results.Add(new MethodResult("Full clone", sw.ElapsedMilliseconds, ws0, WsMB(), 0, "FAIL"));
            Console.WriteLine();
        }
    }

    // ================= Per-class clone: each entity type -> a brand-new blank DWG (keep associations) =================
    /// <summary>
    /// Clone each entity type in model space into its own brand-new blank DWG, one file each.
    /// Association preservation: the entity's Layer/Block/Style/TableStyle references are cloned
    /// together and re-registered in the new document, avoiding dangling cross-document pointers
    /// (this is the "clone both data sets but keep the associations" requirement).
    /// </summary>
    private static void RunPerClassClone(CadDocument src, string baseDir)
    {
        Console.WriteLine("\n" + new string('=', 80));
        Console.WriteLine("▶ [Per-class clone] Group model space by entity type; clone each type into a brand-new blank DWG (keep associations)");
        Console.WriteLine(new string('=', 80));

        // Group by runtime type (preserve order of appearance)
        var groups = new List<(string TypeName, List<Entity> Items)>();
        var index = new Dictionary<Type, int>();
        foreach (Entity e in src.ModelSpace.Entities)
        {
            Type tt = e.GetType();
            if (!index.TryGetValue(tt, out int gi))
            {
                index[tt] = gi = groups.Count;
                groups.Add((tt.Name, new List<Entity> { e }));
            }
            else
            {
                groups[gi].Items.Add(e);
            }
        }

        string outDir = Path.Combine(baseDir, "per_class");
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"  {groups.Count} entity types in total, output dir: {outDir}\n");

        int okCount = 0, failCount = 0;
        foreach (var (typeName, items) in groups)
        {
            string safe = SafeFile(typeName);
            string outPath = Path.Combine(outDir, $"{typeName}.dwg");
            var sw = Stopwatch.StartNew();
            try
            {
                CadDocument dst = new CadDocument();
                dst.Header.Version = src.Header.Version;   // Same version as the source, for easy comparison
                // [FIX] DWGs of R2004 and earlier write strings using the document code page;
                // without copying CodePage the new document defaults to ANSI_1252 and all
                // Chinese (text, linetype text fields, etc.) becomes '?'.
                dst.Header.CodePage = src.Header.CodePage;

                // [PATCH] Preseed the named tables with "source content wins" before cloning the entities
                // (fixes default entries whose names collide and swallow the source content)
                PreseedTables(src, dst);

                int added = 0;
                foreach (Entity e in items)
                {
                    if (CloneWithDeps(src, dst, e) != null) added++;
                }
                sw.Stop();

                var writer = new DwgWriter(outPath, dst) { Configuration = new DwgWriterConfiguration() };
                writer.Write();
                writer.Dispose();
                long sz = new FileInfo(outPath).Length;

                // Readback check
                string rbStatus;
                int rbModel = -1, rbBlocks = -1;
                try
                {
                    CadDocument rb = new DwgReader(outPath).Read();
                    rbModel = rb.ModelSpace.Entities.Count;
                    rbBlocks = rb.BlockRecords.Count;
                    rbStatus = rbModel == items.Count ? "OK" : $"MISMATCH(rb={rbModel})";
                }
                catch (Exception rex)
                {
                    rbStatus = $"READFAIL:{Trunc(rex.Message, 40)}";
                }

                bool good = added == items.Count && rbStatus == "OK";
                if (good) okCount++; else failCount++;
                Console.WriteLine($"  [{(good ? "✅" : "⚠️")}] {typeName,-22} cloned={added}/{items.Count}  readback model={rbModel} blocks={rbBlocks}  output={sz / 1024.0:F0}KB  {sw.ElapsedMilliseconds:N0}ms  {rbStatus}");
                _results.Add(new MethodResult($"Per-class {typeName}", sw.ElapsedMilliseconds, 0, 0, sz, good ? "OK" : "CHECK"));
            }
            catch (Exception ex)
            {
                sw.Stop();
                failCount++;
                Console.WriteLine($"  [❌] {typeName,-22} failed: {Trunc(ex.Message, 60)}");
                _results.Add(new MethodResult($"Per-class {typeName}", sw.ElapsedMilliseconds, 0, 0, 0, "FAIL"));
            }
        }

        Console.WriteLine($"\n  Per-class clone summary: success {okCount} / needs attention {failCount} ({groups.Count} types in total)");
        Console.WriteLine();
    }

    // ================= Association-drop verification: Insert with Block set to null =================
    /// <summary>
    /// Verify that "cloning one entity alone drops its associations by default": clone a single
    /// Insert into a new blank document, use reflection to null out its Block (Insert.Block has an
    /// internal setter), then write it out and read it back to observe the output state after the
    /// block reference has been dropped.
    /// </summary>
    private static void RunDropAssociationTest(CadDocument src, string baseDir)
    {
        Console.WriteLine(new string('=', 80));
        Console.WriteLine("▶ [Association drop] Clone a single Insert and null out its Block (clone one alone -> associations dropped by default)");
        Console.WriteLine(new string('=', 80));

        Insert firstInsert = null;
        foreach (Entity e in src.ModelSpace.Entities)
            if (e is Insert ins && !(e is TableEntity)) { firstInsert = ins; break; }

        if (firstInsert == null)
        {
            Console.WriteLine("  (No plain Insert in the sample; skipping)");
            return;
        }
        string outPath = Path.Combine(baseDir, "drop_insert_block.dwg");
        try
        {
            string srcBlockName = firstInsert.Block?.Name ?? "<null>";
            CadDocument dst = new CadDocument();
            dst.Header.Version = src.Header.Version;

            Entity clone = (Entity)firstInsert.Clone();
            SetInsertBlock(clone, null);           // Reflection: null out Block -> drop the association
            dst.ModelSpace.Entities.Add(clone);    // Triggers AssignDocument (block re-registration is skipped when Block=null)

            var writer = new DwgWriter(outPath, dst) { Configuration = new DwgWriterConfiguration() };
            writer.Write();
            writer.Dispose();
            long sz = new FileInfo(outPath).Length;

            int rbModel = -1; string rbBlock = "?";
            try
            {
                CadDocument rb = new DwgReader(outPath).Read();
                rbModel = rb.ModelSpace.Entities.Count;
                var ri = rb.ModelSpace.Entities.OfType<Insert>().FirstOrDefault();
                rbBlock = ri?.Block?.Name ?? "<null>";
            }
            catch (Exception rex) { rbModel = -1; rbBlock = "READFAIL"; }

            Console.WriteLine($"  Source Insert block={srcBlockName} -> after nulling, wrote {sz / 1024.0:F0}KB, readback model={rbModel} block ref={rbBlock}");
            _results.Add(new MethodResult("Association drop (Insert)", 0, 0, 0, sz, "OK"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ Association-drop test failed: {Trunc(ex.Message, 60)}");
            _results.Add(new MethodResult("Association drop (Insert)", 0, 0, 0, 0, "FAIL"));
        }
        Console.WriteLine();
    }

    // ================= Dependency re-registration (the core of keeping associations) =================
    /// <summary>
    /// Clone one entity from the src document into the dst document, keeping its associations.
    /// How it works: Clone() has already detached Layer/LineType/Block/Style automatically (fresh
    /// clones, Document=null); adding the entity to dst triggers AssignDocument, which re-registers
    /// every reference by name (Layer/LineType/Block/Style/TableStyle) automatically, so no manual
    /// per-dependency cloning is needed.
    /// Returns the entity added to dst.
    /// </summary>
    private static Entity CloneWithDeps(CadDocument src, CadDocument dst, Entity srcEntity)
    {
        // Key: clone the source entity first -> Owner=null, Document=null, and Layer/LineType/Block/Style
        // are all detached copies
        Entity e = (Entity)srcEntity.Clone();

        // Add to the dst model space -> triggers onAdd -> AddCadObject -> AssignDocument(dst)
        // The AssignDocument chain handles it automatically:
        //   Entity.AssignDocument  -> updateTableEntry(Layer, LineType, dst) registers by name
        //   Insert.AssignDocument  -> BlockRecords.TryGetValue/Add reuses or adds by name
        //   Dimension.AssignDocument -> updateTableEntry(Style, Block, dst) registers by name
        //   TableEntity.AssignDocument -> updateCollectionEntry(Content.Style, dst.TableStyles)
        //   BlockRecord.AssignDocument -> RegisterCollection(Entities) -> AssignDocument each entity in the block
        dst.ModelSpace.Entities.Add(e);
        return e;
    }

    /// <summary>
    /// [PATCH] Before cloning the entities, clone the entries of each named table from the source
    /// document into the target document (when names collide, source content wins, overriding the
    /// default placeholder entries auto-generated when the target document was constructed, e.g.
    /// TextStyle "Standard" and MLineStyle "Standard").
    /// Rule (confirmed by the user): when the original document has a name collision, the original
    /// document's content takes priority.
    /// Must clone, not move: the same src is cloned into multiple dsts (per-class benchmark); moving
    /// would corrupt the source document.
    /// Order: referenced entries enter the tables first (TextStyle/LineType/Layer/DimensionStyle),
    /// the referencing entries enter later (TableStyle/MLineStyle/MLeaderStyle); the latter's
    /// AssignDocument re-registers their internal references by name.
    /// </summary>
    private static void PreseedTables(CadDocument src, CadDocument dst)
    {
        void preTable<T>(ACadSharp.Tables.Collections.Table<T> s, ACadSharp.Tables.Collections.Table<T> d) where T : TableEntry
        {
            foreach (var e in s.ToList())
                d.AddOrUpdate((T)e.Clone());
        }
        void preDict<T>(ACadSharp.Objects.Collections.ObjectDictionaryCollection<T> s, ACadSharp.Objects.Collections.ObjectDictionaryCollection<T> d) where T : NonGraphicalObject
        {
            foreach (var e in s.ToList())
                d.AddOrUpdate((T)e.Clone());
        }

        preTable(src.TextStyles, dst.TextStyles);
        preTable(src.LineTypes, dst.LineTypes);
        preTable(src.Layers, dst.Layers);
        preTable(src.DimensionStyles, dst.DimensionStyles);
        preDict(src.TableStyles, dst.TableStyles);
        preDict(src.MLineStyles, dst.MLineStyles);
        preDict(src.MLeaderStyles, dst.MLeaderStyles);
    }

    /// <summary>Set Insert.Block via reflection (internal setter); used for the "drop association" test.</summary>
    private static void SetInsertBlock(Entity ins, BlockRecord block)
    {
        var prop = typeof(Insert).GetProperty("Block", BindingFlags.Public | BindingFlags.Instance);
        prop?.SetValue(ins, block);
    }

    private static string SafeFile(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    private static string Trunc(string s, int n)
    {
        if (s == null) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    // ================= Recompute model extents from the entities' true bounding boxes =================
    private static void SetModelExtentsFromEntities(CadDocument doc)
    {
        try
        {
            double minx = double.MaxValue, miny = double.MaxValue, minz = double.MaxValue;
            double maxx = double.MinValue, maxy = double.MinValue, maxz = double.MinValue;
            bool any = false;
            foreach (Entity e in doc.ModelSpace.Entities)
            {
                BoundingBox b = e.GetBoundingBox();
                if (b.Extent == BoundingBoxExtent.Finite)
                {
                    any = true;
                    minx = Math.Min(minx, b.Min.X); miny = Math.Min(miny, b.Min.Y); minz = Math.Min(minz, b.Min.Z);
                    maxx = Math.Max(maxx, b.Max.X); maxy = Math.Max(maxy, b.Max.Y); maxz = Math.Max(maxz, b.Max.Z);
                }
            }
            if (any)
            {
                double padX = Math.Max(10, (maxx - minx) * 0.05);
                double padY = Math.Max(10, (maxy - miny) * 0.05);
                double padZ = Math.Max(1, (maxz - minz) * 0.05);
                doc.Header.ModelSpaceExtMin = new XYZ(minx - padX, miny - padY, minz - padZ);
                doc.Header.ModelSpaceExtMax = new XYZ(maxx + padX, maxy + padY, maxz + padZ);
            }
        }
        catch { }
    }

    // ================= Readback check =================
    private static void ValidateOutputs(string baseDir, int expected)
    {
        foreach (var v in SupportedVersions)
        {
            string p = Path.Combine(baseDir, $"out_{v.Name.Replace('/', '_')}.dwg");
            if (!File.Exists(p)) { Console.WriteLine($"  ⚠️  Missing: {Path.GetFileName(p)}"); continue; }
            try
            {
                var d = new DwgReader(p).Read();
                int cnt = d.Entities.Count;
                long sz = new FileInfo(p).Length;
                bool ok = Math.Abs(cnt - expected) <= 2;
                Console.WriteLine($"  {v.Name,-14} entities={cnt:N0} (expected ~{expected:N0}) size={sz / 1024.0 / 1024.0:F2}MB  {(ok ? "✅" : "⚠️")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {v.Name,-14} Readback failed: {ex.Message}");
            }
        }
    }

    // ================= Summary report =================
    private static void PrintReport()
    {
        Console.WriteLine("\n" + new string('=', 80));
        Console.WriteLine("  Summary report (elapsed time / output size / status)");
        Console.WriteLine(new string('=', 80));
        Console.WriteLine($"  {"Item",-26} {"Elapsed (ms)",12} {"Output KB",10}  Status");
        Console.WriteLine("  " + new string('-', 60));
        foreach (var r in _results)
        {
            string szStr = r.OutputBytes > 0 ? (r.OutputBytes / 1024.0).ToString("F0") : "-";
            Console.WriteLine($"  {Trunc(r.Name, 26),-26} {r.Ms.ToString("N0"),12} {szStr,10}  {r.Status}");
        }
        Console.WriteLine(new string('=', 80));
    }

    // ================= (9) IO performance benchmark: streaming read/write / incremental save / parallel decode / lazy load / progress events =================
    // Covers the changes in recent read/write-related commits:
    //   c8701519  DWG incremental save (P0 dirty handles / P1 patch source) + parallel serialization + DWG streaming write + DWG parallel decode + lazy partial decode
    //   2001e83c  Read/write progress events (DwgWriter.OnSaveProgress / reader-side OnProgress)
    //   37adbe70  DXF streaming write + XData interning + version-gated DXF read/write
    //   715fd41d  Version-gated DWG write (R14~R2007 + AC21 native encoder)
    // sample.dwg is only 0.14 MB and not representative, so a benchmark drawing with 100k entities
    // (every entity carrying XData) is generated on the fly for all large-file tests.
    // Output dir: <baseDir>/io_bench/
    private static void RunIoBench(string baseDir)
    {
        string benchDir = Path.Combine(baseDir, "io_bench");
        Directory.CreateDirectory(benchDir);
        string benchDwg = Path.Combine(benchDir, "bench_io.dwg");
        string benchDxf = Path.Combine(benchDir, "bench_io.dxf");
        const int N = 100_000;

        Console.WriteLine(new string('=', 80));
        Console.WriteLine($"▶ [(9) IO benchmark] Generate a {N:N0}-entity benchmark drawing on the fly (all entities carry XData) -> DWG/DXF streaming read/write / incremental save / parallel decode / lazy load / progress events");
        Console.WriteLine(new string('=', 80));

        var sw = Stopwatch.StartNew();
        long ws0 = WsMB(), ws1;

        // ---------- GEN-1 Generate the large benchmark drawing ----------
        CadDocument bench = null;
        try
        {
            bench = BuildBenchDocument(N);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"  ❌ Benchmark drawing generation failed: {Trunc(ex.Message, 80)}");
            _results.Add(new MethodResult("[IOB] GEN benchmark drawing", sw.ElapsedMilliseconds, ws0, WsMB(), 0, "FAIL"));
            return;
        }
        sw.Stop(); ws1 = WsMB();
        Console.WriteLine($"  [GEN-1] Generated {N:N0} entities (Line60% / Poly2D15% / Arc10% / Text10% / Insert5%)  {sw.ElapsedMilliseconds:N0} ms  memory {ws0:N0}->{ws1:N0} MB");
        _results.Add(new MethodResult("[IOB] GEN benchmark drawing", sw.ElapsedMilliseconds, ws0, ws1, 0, "OK"));

        // ---------- GEN-2 Write the benchmark DWG (AC1018 baseline file) ----------
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        var wg = new DwgWriter(benchDwg, bench) { Configuration = new DwgWriterConfiguration() };
        wg.Write(); wg.Dispose();
        sw.Stop(); ws1 = WsMB();
        long dwgSz = new FileInfo(benchDwg).Length;
        Console.WriteLine($"  [GEN-2] Benchmark DWG(AC1018) written  {sw.ElapsedMilliseconds:N0} ms  {dwgSz / 1024.0 / 1024.0:F2} MB  memory {ws0:N0}->{ws1:N0} MB");
        _results.Add(new MethodResult("[IOB] GEN benchmark DWG write", sw.ElapsedMilliseconds, ws0, ws1, dwgSz, "OK"));

        // ---------- GEN-3 Write the benchmark DXF (AC1018 baseline file) ----------
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        var xd = new DxfWriter(benchDxf, bench) { Configuration = new DxfWriterConfiguration() };
        xd.Write(); xd.Dispose();
        sw.Stop(); ws1 = WsMB();
        long dxfSz = new FileInfo(benchDxf).Length;
        Console.WriteLine($"  [GEN-3] Benchmark DXF(AC1018) written  {sw.ElapsedMilliseconds:N0} ms  {dxfSz / 1024.0 / 1024.0:F2} MB  memory {ws0:N0}->{ws1:N0} MB");
        _results.Add(new MethodResult("[IOB] GEN benchmark DXF write", sw.ElapsedMilliseconds, ws0, ws1, dxfSz, "OK"));

        bench = null;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        // ================= DWG group =================
        Console.WriteLine(new string('-', 80));
        Console.WriteLine("▶ DWG group: read baseline / parallel decode / incremental save (P1/P0) / full fallback / parallel serialization / streaming write / lazy load / save progress events");

        CadDocument docD = null;
        int totalEnt = 0;

        // ---------- D1 DWG read baseline (with reader-side OnProgress progress events) ----------
        int readEvents = 0, readLastC = 0, readLastT = -1;
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        var rdr1 = new DwgReader(benchDwg) { Configuration = new DwgReaderConfiguration { ReadSummaryInfo = true } };
        rdr1.OnProgress += (s, e) => { readEvents++; readLastC = e.Completed; readLastT = e.Total; };
        docD = rdr1.Read();
        sw.Stop(); ws1 = WsMB();
        totalEnt = docD.ModelSpace.Entities.Count;
        Console.WriteLine($"  [D1] DWG read baseline  {sw.ElapsedMilliseconds:N0} ms  entities={totalEnt:N0}  memory {ws0:N0}->{ws1:N0} MB  progress events={readEvents:N0} (last {readLastC:N0}/{readLastT:N0})");
        _results.Add(new MethodResult("[IOB] D1 DWG read baseline", sw.ElapsedMilliseconds, ws0, ws1, 0, "OK"));

        // ---------- D2 DWG parallel-decode read ----------
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        var rdr2 = new DwgReader(benchDwg) { Configuration = new DwgReaderConfiguration { ReadSummaryInfo = true, ParallelDecode = true } };
        var doc2 = rdr2.Read();
        sw.Stop(); ws1 = WsMB();
        int cnt2 = doc2.ModelSpace.Entities.Count;
        string st2 = cnt2 == totalEnt ? "OK" : $"MISMATCH={cnt2:N0}";
        Console.WriteLine($"  [D2] DWG parallel-decode read  {sw.ElapsedMilliseconds:N0} ms  entities={cnt2:N0}  {st2}  memory {ws0:N0}->{ws1:N0} MB");
        _results.Add(new MethodResult("[IOB] D2 DWG parallel-decode read", sw.ElapsedMilliseconds, ws0, ws1, 0, st2 == "OK" ? "OK" : "CHECK"));
        doc2 = null;

        // ---------- D3 RetainPatchSource read (prepare the patch source for incremental save) ----------
        DwgObjectPatchSource patch = null;
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        var rdr3 = new DwgReader(benchDwg) { Configuration = new DwgReaderConfiguration { ReadSummaryInfo = true, RetainPatchSource = true } };
        docD = rdr3.Read();
        patch = rdr3.PatchSource;
        sw.Stop(); ws1 = WsMB();
        Console.WriteLine($"  [D3] DWG read (retain patch source)  {sw.ElapsedMilliseconds:N0} ms  patch source={(patch != null ? "ready" : "NULL")}  entities={docD.ModelSpace.Entities.Count:N0}  memory {ws0:N0}->{ws1:N0} MB");
        _results.Add(new MethodResult("[IOB] D3 DWG read + patch source", sw.ElapsedMilliseconds, ws0, ws1, 0, patch != null ? "OK" : "FAIL"));

        // ---------- D4 Incremental save (no changes, P1 patch-source strategy) ----------
        string out4 = Path.Combine(benchDir, "bench_inc_nochg.dwg");
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        string fb4 = null, st4 = "FAIL";
        long sz4 = 0;
        var n4 = new List<string>();
        try
        {
            var wr4 = new DwgWriter(out4, docD)
            {
                Configuration = new DwgWriterConfiguration { IncrementalSave = true, PatchSource = patch }
            };
            wr4.OnNotification += (s, e) =>
            {
                if (e != null && e.Message != null)
                {
                    if (n4.Count < 3) n4.Add(Trunc(e.Message, 60));
                    if (fb4 == null && e.Message.Contains("fallback")) fb4 = Trunc(e.Message, 60);
                }
            };
            wr4.Write(); wr4.Dispose();
            sz4 = new FileInfo(out4).Length;
            int rb4 = new DwgReader(out4).Read().ModelSpace.Entities.Count;
            st4 = rb4 == totalEnt ? "OK" : $"MISMATCH={rb4:N0}";
        }
        catch (Exception ex) { st4 = "FAIL:" + Trunc(ex.Message, 40); }
        sw.Stop(); ws1 = WsMB();
        string sz4s = sz4 >= 1024 * 1024 ? $"{sz4 / 1024.0 / 1024.0:F2} MB" : $"{sz4 / 1024.0:F1} KB";
        Console.WriteLine($"  [D4] Incremental save (no changes, P1)  {sw.ElapsedMilliseconds:N0} ms  output={sz4s}  readback={st4}  notifications=[{string.Join(" | ", n4)}]");
        _results.Add(new MethodResult("[IOB] D4 incremental save no changes", sw.ElapsedMilliseconds, ws0, ws1, sz4, st4 == "OK" ? "OK" : "CHECK"));

        // ---------- D5 Incremental save (5 entities modified, P0 dirty-handle strategy, phased timing: read/write/readback) ----------
        string out5 = Path.Combine(benchDir, "bench_inc_dirty.dwg");
        long t5Read = 0, t5Write = 0, t5Rb = 0;
        var n5 = new List<string>();
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        string fb5 = null, st5 = "FAIL";
        long sz5 = 0;
        try
        {
            var sw5r = Stopwatch.StartNew();
            var rdr5 = new DwgReader(benchDwg) { Configuration = new DwgReaderConfiguration { ReadSummaryInfo = true, RetainPatchSource = true } };
            var doc5 = rdr5.Read();
            var patch5 = rdr5.PatchSource;
            sw5r.Stop(); t5Read = sw5r.ElapsedMilliseconds;

            // Translate the first 5 Lines by (+123456.789, +1000): original Y in [0,60], after the move Y>=1000 is the unique marker
            var dirty = new HashSet<ulong>();
            int moved = 0;
            foreach (Entity e in doc5.ModelSpace.Entities)
            {
                if (moved >= 5) break;
                if (e is Line ln)
                {
                    ln.StartPoint = new XYZ(ln.StartPoint.X + 123456.789, ln.StartPoint.Y + 1000.0, 0);
                    ln.EndPoint = new XYZ(ln.EndPoint.X + 123456.789, ln.EndPoint.Y + 1000.0, 0);
                    dirty.Add(ln.Handle);
                    moved++;
                }
            }
            dirty.Add(doc5.ModelSpace.Handle);  // Container handle: *Model_Space (the entity list changed)

            var sw5w = Stopwatch.StartNew();
            var wr5 = new DwgWriter(out5, doc5)
            {
                Configuration = new DwgWriterConfiguration { IncrementalSave = true, PatchSource = patch5, DirtyHandles = dirty }
            };
            wr5.OnNotification += (s, e) =>
            {
                if (e != null && e.Message != null)
                {
                    if (n5.Count < 3) n5.Add(Trunc(e.Message, 60));
                    if (fb5 == null && e.Message.Contains("fallback")) fb5 = Trunc(e.Message, 60);
                }
            };
            wr5.Write(); wr5.Dispose();
            sw5w.Stop(); t5Write = sw5w.ElapsedMilliseconds;
            patch5?.Dispose();

            // Readback check: total matches and exactly 5 Lines have StartPoint.Y >= 1000
            var sw5rb = Stopwatch.StartNew();
            var rb5 = new DwgReader(out5).Read();
            sw5rb.Stop(); t5Rb = sw5rb.ElapsedMilliseconds;
            int rb5Total = rb5.ModelSpace.Entities.Count;
            int hit = 0;
            foreach (Entity e in rb5.ModelSpace.Entities)
                if (e is Line ln2 && ln2.StartPoint.Y >= 1000.0) hit++;
            st5 = (rb5Total == totalEnt && hit == 5) ? "OK" : $"CHECK(total={rb5Total:N0},moved={hit})";
        }
        catch (Exception ex) { st5 = "FAIL:" + Trunc(ex.Message, 40); }
        sw.Stop(); ws1 = WsMB();
        sz5 = File.Exists(out5) ? new FileInfo(out5).Length : 0;
        Console.WriteLine($"  [D5] Incremental save (5 entities modified, P0)  total={sw.ElapsedMilliseconds:N0} ms [read={t5Read:N0} write={t5Write:N0} readback={t5Rb:N0}]  output={sz5 / 1024.0 / 1024.0:F2} MB  check={st5}  notifications=[{string.Join(" | ", n5)}]");
        _results.Add(new MethodResult("[IOB] D5 incremental save dirty handles", sw.ElapsedMilliseconds, ws0, ws1, sz5, st5 == "OK" ? "OK" : "CHECK"));
        patch?.Dispose();

        // ---------- D6 Full save without a patch source (correct contract = silent full save, no fallback notification) ----------
        string out6 = Path.Combine(benchDir, "bench_full_fallback.dwg");
        long t6Read = 0, t6Write = 0, t6Rb = 0;
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        string fb6 = null, st6 = "FAIL";
        long sz6 = 0;
        try
        {
            var sw6r = Stopwatch.StartNew();
            var doc6 = new DwgReader(benchDwg).Read();
            sw6r.Stop(); t6Read = sw6r.ElapsedMilliseconds;
            var sw6w = Stopwatch.StartNew();
            var wr6 = new DwgWriter(out6, doc6)
            {
                Configuration = new DwgWriterConfiguration { IncrementalSave = true } // No PatchSource -> should auto-fall back to a full save
            };
            wr6.OnNotification += (s, e) => { if (e != null && e.Message != null && fb6 == null) fb6 = Trunc(e.Message, 60); };
            wr6.Write(); wr6.Dispose();
            sw6w.Stop(); t6Write = sw6w.ElapsedMilliseconds;
            sz6 = new FileInfo(out6).Length;
            var sw6rb = Stopwatch.StartNew();
            int rb6 = new DwgReader(out6).Read().ModelSpace.Entities.Count;
            sw6rb.Stop(); t6Rb = sw6rb.ElapsedMilliseconds;
            st6 = rb6 == totalEnt ? "OK" : $"MISMATCH={rb6:N0}";
        }
        catch (Exception ex) { st6 = "FAIL:" + Trunc(ex.Message, 40); }
        sw.Stop(); ws1 = WsMB();
        Console.WriteLine($"  [D6] Full fallback (no patch source)  total={sw.ElapsedMilliseconds:N0} ms [read={t6Read:N0} write={t6Write:N0} readback={t6Rb:N0}]  output={sz6 / 1024.0 / 1024.0:F2} MB  readback={st6}  notification={fb6 ?? "none (matches the silent contract)"}");
        _results.Add(new MethodResult("[IOB] D6 full fallback check", sw.ElapsedMilliseconds, ws0, ws1, sz6, st6 == "OK" ? "OK" : "CHECK"));

        // ---------- D7/D8 Full save: serial vs parallel serialization (byte-identical output expected) ----------
        string out7 = Path.Combine(benchDir, "bench_full_serial.dwg");
        string out8 = Path.Combine(benchDir, "bench_full_paral.dwg");
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        var wr7 = new DwgWriter(out7, docD) { Configuration = new DwgWriterConfiguration { IncrementalSave = false, ParallelSerialize = false } };
        wr7.Write(); wr7.Dispose();
        sw.Stop(); ws1 = WsMB();
        long sz7 = new FileInfo(out7).Length;
        Console.WriteLine($"  [D7] Full save (serial)  {sw.ElapsedMilliseconds:N0} ms  output={sz7 / 1024.0 / 1024.0:F2} MB  memory {ws0:N0}->{ws1:N0} MB");
        _results.Add(new MethodResult("[IOB] D7 full save serial", sw.ElapsedMilliseconds, ws0, ws1, sz7, "OK"));

        sw = Stopwatch.StartNew(); ws0 = WsMB();
        var wr8 = new DwgWriter(out8, docD) { Configuration = new DwgWriterConfiguration { IncrementalSave = false, ParallelSerialize = true } };
        wr8.Write(); wr8.Dispose();
        sw.Stop(); ws1 = WsMB();
        long sz8 = new FileInfo(out8).Length;
        bool same8 = Convert.ToBase64String(SHA256.HashData(File.ReadAllBytes(out7)))
                   == Convert.ToBase64String(SHA256.HashData(File.ReadAllBytes(out8)));
        string st8 = (sz8 == sz7 && same8) ? "OK+bytes identical" : $"CHECK(sz {sz7:N0}->{sz8:N0} hash={(same8 ? "same" : "diff")})";
        Console.WriteLine($"  [D8] Full save (parallel serialization)  {sw.ElapsedMilliseconds:N0} ms  output={sz8 / 1024.0 / 1024.0:F2} MB  vs D7: {st8}  memory {ws0:N0}->{ws1:N0} MB");
        _results.Add(new MethodResult("[IOB] D8 full save parallel", sw.ElapsedMilliseconds, ws0, ws1, sz8, same8 ? "OK" : "CHECK"));

        // ---------- D9/D10 DWG streaming write (internal API via reflection: BeginStreamingObjects/WriteEntityDirect/EndStreamingObjects) ----------
        RunDwgStreamingWrite(Path.Combine(benchDir, "bench_stream_ac1018.dwg"), ACadVersion.AC1018, 50_000, "[IOB] D9 DWG streaming write AC1018");
        RunDwgStreamingWrite(Path.Combine(benchDir, "bench_stream_ac1021.dwg"), ACadVersion.AC1021, 50_000, "[IOB] D10 DWG streaming write AC1021");

        // ---------- D11/D12 Lazy load: PreparePartialRead + ReadObjectsPartial ----------
        DwgReader rdr11 = null;
        DwgPartialReadContext ctx = null;
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        try
        {
            rdr11 = new DwgReader(benchDwg) { Configuration = new DwgReaderConfiguration { ReadSummaryInfo = true } };
            ctx = rdr11.PreparePartialRead();
            sw.Stop(); ws1 = WsMB();
            Console.WriteLine($"  [D11] PreparePartialRead  {sw.ElapsedMilliseconds:N0} ms  (header + handle region only, no objects read)  memory {ws0:N0}->{ws1:N0} MB");
            _results.Add(new MethodResult("[IOB] D11 PreparePartialRead", sw.ElapsedMilliseconds, ws0, ws1, 0, "OK"));
        }
        catch (Exception ex)
        {
            sw.Stop(); ws1 = WsMB();
            Console.WriteLine($"  [D11] Lazy-load preparation failed: {Trunc(ex.Message, 80)}");
            _results.Add(new MethodResult("[IOB] D11 PreparePartialRead", sw.ElapsedMilliseconds, ws0, ws1, 0, "FAIL"));
        }

        if (ctx != null)
        {
            // seeds = all header handles + the first 100 entity handles in model space;
            // noExpand = *MODEL_SPACE/*PAPER_SPACE (do not expand the whole entity list)
            var seeds = new Queue<ulong>();
            foreach (var h in ctx.HeaderHandles.GetHandles())
                if (h.HasValue && h.Value > 0) seeds.Enqueue(h.Value);
            int taken = 0;
            foreach (Entity e in docD.ModelSpace.Entities)
            {
                if (taken >= 100) break;
                if (e.Handle > 0) { seeds.Enqueue(e.Handle); taken++; }
            }
            var noExpand = new HashSet<ulong>();
            var hMs = ctx.HeaderHandles.MODEL_SPACE;
            var hPs = ctx.HeaderHandles.PAPER_SPACE;
            if (hMs.HasValue && hMs.Value > 0) noExpand.Add(hMs.Value);
            if (hPs.HasValue && hPs.Value > 0) noExpand.Add(hPs.Value);

            int fullObjs = docD.GetCadObjects<CadObject>().Count();
            sw = Stopwatch.StartNew(); ws0 = WsMB();
            string st12 = "FAIL";
            try
            {
                var doc12 = rdr11.ReadObjectsPartial(ctx, null, seeds, noExpand);
                int decCount = doc12.GetCadObjects<CadObject>().Count();
                int decModel = doc12.ModelSpace != null ? doc12.ModelSpace.Entities.Count : 0;
                st12 = (decCount > 0 && decCount < fullObjs) ? "OK" : $"CHECK(dec={decCount},full={fullObjs:N0})";
                Console.WriteLine($"  [D12] ReadObjectsPartial (seed={seeds.Count} handles, noExpand={noExpand.Count})  {sw.ElapsedMilliseconds:N0} ms  decoded={decCount:N0}/{fullObjs:N0} objects  model-space entities={decModel}  memory {ws0:N0}->{ws1:N0} MB");
                // Diagnostics: histogram of decoded object types + handle distribution of non-seed model-space entities (explains where expansion came from)
                var hist = doc12.GetCadObjects<CadObject>()
                    .GroupBy(o => o.GetType().Name)
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .Select(g => $"{g.Key}={g.Count():N0}")
                    .ToList();
                Console.WriteLine($"  [D12] Type histogram: {string.Join("  ", hist)}");
                if (doc12.ModelSpace != null)
                {
                    var seedSet = new HashSet<ulong>(seeds);
                    int extraTop = 0; ulong exMin = ulong.MaxValue, exMax = 0;
                    foreach (Entity ee in doc12.ModelSpace.Entities)
                    {
                        if (!seedSet.Contains(ee.Handle))
                        {
                            extraTop++;
                            if (ee.Handle < exMin) exMin = ee.Handle;
                            if (ee.Handle > exMax) exMax = ee.Handle;
                        }
                    }
                    Console.WriteLine($"  [D12] Non-seed model-space entities={extraTop:N0}  handle range=[{exMin:N0}..{exMax:N0}]");
                }
            }
            catch (Exception ex)
            {
                st12 = "FAIL:" + Trunc(ex.Message, 40);
                Console.WriteLine($"  [D12] Partial read failed: {Trunc(ex.Message, 80)}");
            }
            sw.Stop(); ws1 = WsMB();
            _results.Add(new MethodResult("[IOB] D12 partial read 100 seeds", sw.ElapsedMilliseconds, ws0, ws1, 0, st12 == "OK" ? "OK" : "CHECK"));
        }

        // ---------- D13 Full save + OnSaveProgress write progress events ----------
        string out13 = Path.Combine(benchDir, "bench_prog.dwg");
        int saveEvents = 0, saveLastC = 0, saveLastT = 0;
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        string st13 = "OK";
        long sz13 = 0;
        try
        {
            var wr13 = new DwgWriter(out13, docD) { Configuration = new DwgWriterConfiguration { IncrementalSave = false } };
            wr13.OnSaveProgress += (s, e) => { saveEvents++; saveLastC = e.Completed; saveLastT = e.Total; };
            wr13.Write(); wr13.Dispose();
            sz13 = new FileInfo(out13).Length;
        }
        catch (Exception ex) { st13 = "FAIL:" + Trunc(ex.Message, 40); }
        sw.Stop(); ws1 = WsMB();
        double pct13 = saveLastT > 0 ? saveLastC * 100.0 / saveLastT : 0.0;
        Console.WriteLine($"  [D13] Full save + OnSaveProgress  {sw.ElapsedMilliseconds:N0} ms  output={sz13 / 1024.0 / 1024.0:F2} MB  progress events={saveEvents:N0}  last={saveLastC:N0}/{saveLastT:N0} ({pct13:F1}%)");
        _results.Add(new MethodResult("[IOB] D13 save progress events", sw.ElapsedMilliseconds, ws0, ws1, sz13, st13));

        docD = null;
        rdr11 = null; ctx = null;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        // ================= DXF group =================
        Console.WriteLine(new string('-', 80));
        Console.WriteLine("▶ DXF group: read baseline / full write / streaming write / XData interning on/off / ReadXData / version-gated round-trip");

        CadDocument docX = null;
        int xEnt = 0;
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        try
        {
            docX = DxfReader.Read(benchDxf);
            xEnt = docX.ModelSpace.Entities.Count;
            sw.Stop(); ws1 = WsMB();
            string stX1 = xEnt == totalEnt ? "OK" : $"CHECK={xEnt:N0}";
            Console.WriteLine($"  [X1] DXF read baseline  {sw.ElapsedMilliseconds:N0} ms  entities={xEnt:N0}  memory {ws0:N0}->{ws1:N0} MB  {stX1}");
            _results.Add(new MethodResult("[IOB] X1 DXF read baseline", sw.ElapsedMilliseconds, ws0, ws1, 0, stX1 == "OK" ? "OK" : "CHECK"));
        }
        catch (Exception ex)
        {
            sw.Stop(); ws1 = WsMB();
            Console.WriteLine($"  [X1] DXF read failed: {Trunc(ex.Message, 80)}");
            _results.Add(new MethodResult("[IOB] X1 DXF read baseline", sw.ElapsedMilliseconds, ws0, ws1, 0, "FAIL"));
            return;
        }

        // ---------- X2 DXF full write (baseline) ----------
        string outX2 = Path.Combine(benchDir, "bench_dxf_full.dxf");
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        var wx2 = new DxfWriter(outX2, docX) { Configuration = new DxfWriterConfiguration() };
        wx2.Write(); wx2.Dispose();
        sw.Stop(); ws1 = WsMB();
        long szX2 = new FileInfo(outX2).Length;
        Console.WriteLine($"  [X2] DXF full write  {sw.ElapsedMilliseconds:N0} ms  output={szX2 / 1024.0 / 1024.0:F2} MB  memory {ws0:N0}->{ws1:N0} MB");
        _results.Add(new MethodResult("[IOB] X2 DXF full write", sw.ElapsedMilliseconds, ws0, ws1, szX2, "OK"));

        // ---------- X3 DXF streaming write (first 30k entities: clone + WriteEntityOne + byte concatenation) ----------
        {
            const int SX = 30_000;
            string outX3 = Path.Combine(benchDir, "bench_dxf_stream.dxf");
            sw = Stopwatch.StartNew(); ws0 = WsMB();
            string stX3 = "FAIL";
            long szX3 = 0;
            try
            {
                var dst = new CadDocument();
                dst.Header.Version = ACadVersion.AC1018;
                var wx3 = new DxfWriter(outX3, dst) { Configuration = new DxfWriterConfiguration() };
                var streamer = wx3.BeginStreamingEntities();
                int written = 0;
                foreach (Entity e in docX.ModelSpace.Entities)
                {
                    if (written >= SX) break;
                    Entity c = CloneWithDeps(docX, dst, e);
                    streamer.WriteEntityOne(c);
                    written++;
                }
                dst.Header.ModelSpaceExtMin = new XYZ(0, 0, 0);
                dst.Header.ModelSpaceExtMax = new XYZ(100_000, 1000, 0);
                streamer.Finish();
                wx3.EndStreamingEntities();
                wx3.Dispose();
                szX3 = new FileInfo(outX3).Length;

                var rbx = new DxfReader(outX3).Read();
                int cnt = rbx.ModelSpace.Entities.Count;
                stX3 = cnt == written ? "OK" : $"MISMATCH(wrote={written:N0},rb={cnt:N0})";
            }
            catch (Exception ex) { stX3 = "FAIL:" + Trunc(ex.Message, 40); }
            sw.Stop(); ws1 = WsMB();
            Console.WriteLine($"  [X3] DXF streaming write ({SX:N0} entities)  {sw.ElapsedMilliseconds:N0} ms  output={szX3 / 1024.0 / 1024.0:F2} MB  readback={stX3}  memory {ws0:N0}->{ws1:N0} MB");
            _results.Add(new MethodResult("[IOB] X3 DXF streaming write 30k", sw.ElapsedMilliseconds, ws0, ws1, szX3, stX3 == "OK" ? "OK" : "CHECK"));
        }

        // ---------- X7 Version-gated DXF round-trip (7 versions, write + readback; must run before docX is nulled out) ----------
        foreach (var v in SupportedVersions)
        {
            string outX7 = Path.Combine(benchDir, $"bench_dxf_{v.Name.Replace("/", "_")}.dxf");
            sw = Stopwatch.StartNew(); ws0 = WsMB();
            string stX7 = "FAIL";
            long szX7 = 0;
            try
            {
                docX.Header.Version = v.Version;
                var wx7 = new DxfWriter(outX7, docX) { Configuration = new DxfWriterConfiguration() };
                wx7.Write(); wx7.Dispose();
                szX7 = new FileInfo(outX7).Length;
                var rb = new DxfReader(outX7).Read();
                int cnt = rb.ModelSpace.Entities.Count;
                stX7 = cnt == xEnt ? "OK" : $"MISMATCH={cnt:N0}/{xEnt:N0}";
                rb = null;
            }
            catch (Exception ex) { stX7 = "FAIL:" + Trunc(ex.Message, 40); }
            sw.Stop(); ws1 = WsMB();
            Console.WriteLine($"  [X7] DXF version-gated {v.Version}  write+readback  {sw.ElapsedMilliseconds:N0} ms  output={szX7 / 1024.0 / 1024.0:F2} MB  {stX7}");
            _results.Add(new MethodResult($"[IOB] X7 DXF round-trip {v.Name}", sw.ElapsedMilliseconds, ws0, ws1, szX7, stX7 == "OK" ? "OK" : "CHECK"));
            GC.Collect();
        }

        // ---------- X4/X5 XData interning on/off (memory comparison) ----------
        // The XData interning table is a process-wide static -> Clear() + GC between the two rounds keeps the baseline consistent
        long wsOn = 0, wsOff = 0;
        {
            DxfXDataInterning.Clear();
            docX = null;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

            sw = Stopwatch.StartNew(); ws0 = WsMB();
            var docOn = DxfReader.Read(benchDxf, new DxfReaderConfiguration { ReadXData = true, InternXDataStrings = true });
            int cntOn = docOn.ModelSpace.Entities.Count;
            sw.Stop(); ws1 = WsMB();
            wsOn = ws1 - ws0;
            Console.WriteLine($"  [X4] DXF read (XData interning ON)  {sw.ElapsedMilliseconds:N0} ms  entities={cntOn:N0}  memory delta={wsOn:N0} MB (start {ws0:N0} -> end {ws1:N0})");
            _results.Add(new MethodResult("[IOB] X4 XData interning ON", sw.ElapsedMilliseconds, ws0, ws1, 0, cntOn == totalEnt ? "OK" : "CHECK"));
            docOn = null;
        }
        {
            DxfXDataInterning.Clear();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

            sw = Stopwatch.StartNew(); ws0 = WsMB();
            var docOff = DxfReader.Read(benchDxf, new DxfReaderConfiguration { ReadXData = true, InternXDataStrings = false });
            int cntOff = docOff.ModelSpace.Entities.Count;
            sw.Stop(); ws1 = WsMB();
            wsOff = ws1 - ws0;
            Console.WriteLine($"  [X5] DXF read (XData interning OFF)  {sw.ElapsedMilliseconds:N0} ms  entities={cntOff:N0}  memory delta={wsOff:N0} MB (start {ws0:N0} -> end {ws1:N0})  memory diff vs X4={wsOff - wsOn:N0} MB");
            _results.Add(new MethodResult("[IOB] X5 XData interning OFF", sw.ElapsedMilliseconds, ws0, ws1, 0, cntOff == totalEnt ? "OK" : "CHECK"));
            docOff = null;
        }

        // ---------- X6 ReadXData=false (skip XData) ----------
        {
            sw = Stopwatch.StartNew(); ws0 = WsMB();
            var doc6 = DxfReader.Read(benchDxf, new DxfReaderConfiguration { ReadXData = false });
            int cnt6 = doc6.ModelSpace.Entities.Count;
            sw.Stop(); ws1 = WsMB();
            long ws6 = ws1 - ws0;
            Console.WriteLine($"  [X6] DXF read (ReadXData=false)  {sw.ElapsedMilliseconds:N0} ms  entities={cnt6:N0}  memory delta={ws6:N0} MB (start {ws0:N0} -> end {ws1:N0})  memory diff vs X5={ws6 - wsOff:N0} MB");
            _results.Add(new MethodResult("[IOB] X6 no XData read", sw.ElapsedMilliseconds, ws0, ws1, 0, cnt6 == totalEnt ? "OK" : "CHECK"));
            doc6 = null;
        }

        docX = null;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine(new string('=', 80));
    }

    // ---------- iobench: generate a 100k-entity benchmark drawing (all entities carry XData) ----------
    private static CadDocument BuildBenchDocument(int n)
    {
        var doc = new CadDocument();
        doc.Header.Version = ACadVersion.AC1018;

        // Benchmark block: 1 Line + 1 Arc + 1 Text
        var blk = new BlockRecord("BENCH_BLK");
        blk.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(30, 0, 0)));
        blk.Entities.Add(new Arc(new XYZ(0, 20, 0), 10, 0, Math.PI));
        blk.Entities.Add(new TextEntity { InsertPoint = new XYZ(0, 35, 0), Height = 5, Value = "BENCH" });
        doc.BlockRecords.Add(blk);

        string[] zones = { "ZONE-A", "ZONE-B", "ZONE-C", "ZONE-D" };
        string[] attrs = { "attr-1", "attr-2", "attr-3", "attr-4", "attr-5", "attr-6", "attr-7", "attr-8" };

        for (int i = 0; i < n; i++)
        {
            int kind = i % 20;
            double x = i * 1.0;
            double y = (i % 7) * 10.0;
            Entity e;
            if (kind < 12)
                e = new Line(new XYZ(x, y, 0), new XYZ(x + 50, y + 50, 0));
            else if (kind < 15)
                e = new Polyline2D(new[]
                {
                    new Vertex2D(new XYZ(x, y, 0)),
                    new Vertex2D(new XYZ(x + 40, y + 10, 0)),
                    new Vertex2D(new XYZ(x + 80, y, 0)),
                    new Vertex2D(new XYZ(x + 120, y + 10, 0)),
                    new Vertex2D(new XYZ(x + 160, y, 0)),
                }, false);
            else if (kind < 17)
                e = new Arc(new XYZ(x, y, 0), 20, 0, 3 * Math.PI / 2);
            else if (kind < 19)
                e = new TextEntity { InsertPoint = new XYZ(x, y, 0), Height = 2.5, Value = $"T{i:D6}" };
            else
                e = new Insert(blk) { InsertPoint = new XYZ(x, y, 0) };

            // Attach XData to every entity: 2 strings (low cardinality -> interning-friendly) + 1 int + 1 real
            var xd = new ExtendedData(new ExtendedDataRecord[]
            {
                new ExtendedDataString(zones[i % 4]),
                new ExtendedDataString(attrs[i % 8]),
                new ExtendedDataInteger32(i % 1000),
                new ExtendedDataReal(Math.Sin(i) * 100.0),
            });
            xd.AddControlStrings();
            e.ExtendedData.Add("BENCH", xd);
            doc.ModelSpace.Entities.Add(e);
        }

        SetModelExtentsFromEntities(doc);
        return doc;
    }

    // ---------- iobench: DWG streaming write (internal API invoked via reflection) ----------
    private static void RunDwgStreamingWrite(string outPath, ACadVersion version, int count, string label)
    {
        var sw = Stopwatch.StartNew();
        long ws0 = WsMB();
        string status = "FAIL";
        long sz = 0;
        try
        {
            var dst = new CadDocument();
            dst.Header.Version = version;
            for (int i = 0; i < count; i++)
                dst.ModelSpace.Entities.Add(new Line(new XYZ(i, (i % 10) * 20.0, 0), new XYZ(i + 50, (i % 10) * 20.0 + 50, 0)));
            dst.Header.ModelSpaceExtMin = new XYZ(0, 0, 0);
            dst.Header.ModelSpaceExtMax = new XYZ(count, 200, 0);

            var writer = new DwgWriter(outPath, dst) { Configuration = new DwgWriterConfiguration() };
            Type wt = typeof(DwgWriter);
            var mBegin = wt.GetMethod("BeginStreamingObjects", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (mBegin == null) throw new MissingMethodException("BeginStreamingObjects not found");
            object objWriter = mBegin.Invoke(writer, null);
            var mWrite = objWriter.GetType().GetMethod("WriteEntityDirect", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(Entity) }, null);
            if (mWrite == null) throw new MissingMethodException("WriteEntityDirect not found");
            foreach (Entity e in dst.ModelSpace.Entities)
                mWrite.Invoke(objWriter, new object[] { e });
            var mEnd = wt.GetMethod("EndStreamingObjects", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(CadDocument), objWriter.GetType() }, null);
            if (mEnd == null) throw new MissingMethodException("EndStreamingObjects not found");
            mEnd.Invoke(writer, new object[] { dst, objWriter });
            writer.Dispose();
            sz = new FileInfo(outPath).Length;
        }
        catch (Exception ex)
        {
            status = "FAIL:" + Trunc(ex.InnerException?.Message ?? ex.Message, 50);
        }
        sw.Stop();
        long ws1 = WsMB();

        if (sz > 0)
        {
            try
            {
                var rbDoc = new DwgReader(outPath).Read();
                int cnt = rbDoc.ModelSpace.Entities.Count;
                status = cnt == count ? "OK" : $"CHECK(rb={cnt:N0}/{count:N0})";
            }
            catch (Exception rex) { status = "READFAIL:" + Trunc(rex.Message, 30); }
        }
        Console.WriteLine($"  [{label}]  DWG streaming write ({count:N0} Line, {version})  {sw.ElapsedMilliseconds:N0} ms  output={sz / 1024.0 / 1024.0:F2} MB  readback={status}  memory {ws0:N0}->{ws1:N0} MB");
        _results.Add(new MethodResult(label, sw.ElapsedMilliseconds, ws0, ws1, sz, status == "OK" ? "OK" : "CHECK"));
    }

    // ================= Mode dispatch (restored "various modes" read/write) =================
    private static bool TryRunMode(string mode, string[] args, string baseDir, string samplePath)
    {
        string a1 = args.Length > 1 ? args[1] : null;
        string a2 = args.Length > 2 ? args[2] : null;
        string a3 = args.Length > 3 ? args[3] : null;
        string a4 = args.Length > 4 ? args[4] : null;
        switch (mode.ToLowerInvariant())
        {
            case "empty":
            {
                if (a1 == null) { Console.Error.WriteLine("Usage: empty <version> [out.dwg]"); return true; }
                ACadVersion ver = (ACadVersion)Enum.Parse(typeof(ACadVersion), a1, true);
                string outP = a2 ?? Path.Combine(baseDir, $"empty_{a1}.dwg");
                var d = new CadDocument(); d.Header.Version = ver;
                DwgWriter.Write(outP, d, new DwgWriterConfiguration { WriteShapes = true });
                Console.WriteLine($"[EMPTY] wrote {outP} version={ver} size={new FileInfo(outP).Length}");
                return true;
            }
            case "rt":
            {
                if (a1 == null || a2 == null) { Console.Error.WriteLine("Usage: rt <in.dwg> <out.dwg> [version]"); return true; }
                string inP = ResolvePath(a1, baseDir);
                var rd = DwgReader.Read(inP);
                Console.WriteLine($"[RT] read {inP} version={rd.Header.Version} objs={rd.GetCadObjects<CadObject>().Count()}");
                if (a3 != null) rd.Header.Version = (ACadVersion)Enum.Parse(typeof(ACadVersion), a3, true);
                DwgWriter.Write(a2, rd, new DwgWriterConfiguration { WriteShapes = true });
                Console.WriteLine($"[RT] wrote {a2} size={new FileInfo(a2).Length}");
                return true;
            }
            case "mktypes":
            {
                // Synthetic linetype test: a custom linetype with the literal name "--GAS--;"
                // (with a text segment + style) + a simple dashed line
                if (a1 == null) { Console.Error.WriteLine("Usage: mktypes <out.dwg>"); return true; }
                var d = new CadDocument();
                d.Header.Version = ACadVersion.AC1018;
                d.Header.CodePage = "gb2312";
                var st = new TextStyle("HZTXT") { Filename = "simplex.shx", BigFontFilename = "hztxt.shx" };
                d.TextStyles.Add(st);
                var ltGas = new LineType("--GAS--") { Description = "Gas line ----GAS----" };
                ltGas.AddSegment(new LineType.Segment { Length = 12.7 });
                var txtSeg = new LineType.Segment { Length = -5.08, Flags = LineTypeShapeFlags.Text, Text = "GAS", Scale = 0.5, Style = st };
                ltGas.AddSegment(txtSeg);
                ltGas.AddSegment(new LineType.Segment { Length = -6.35 });
                d.LineTypes.Add(ltGas);
                var ltSimple = new LineType("GAS") { Description = "simple gas" };
                ltSimple.AddSegment(new LineType.Segment { Length = 10.0 });
                ltSimple.AddSegment(new LineType.Segment { Length = -5.0 });
                d.LineTypes.Add(ltSimple);
                var stShape = new TextStyle("GASSHP") { Filename = "gas.shx", Flags = StyleFlags.IsShape };
                d.TextStyles.Add(stShape);
                var ltShape = new LineType("--GAS-SHAPE--") { Description = "shape segment gas" };
                ltShape.AddSegment(new LineType.Segment { Length = 12.7 });
                ltShape.AddSegment(new LineType.Segment { Length = -5.08, Flags = LineTypeShapeFlags.Shape, ShapeNumber = 1, Scale = 0.5, Style = stShape });
                ltShape.AddSegment(new LineType.Segment { Length = -6.35 });
                d.LineTypes.Add(ltShape);
                var ltCn = new LineType("--GAS-CN--") { Description = "Gas line ----GAS----" };
                ltCn.AddSegment(new LineType.Segment { Length = 12.7 });
                ltCn.AddSegment(new LineType.Segment { Length = -5.08, Flags = LineTypeShapeFlags.Text, Text = "GAS", Scale = 0.5, Style = st });
                ltCn.AddSegment(new LineType.Segment { Length = -6.35 });
                d.LineTypes.Add(ltCn);
                var lay = new Layer("GASLAYER");
                d.Layers.Add(lay);
                var l1 = new Line(XYZ.Zero, new XYZ(100, 0, 0)) { Layer = lay };
                l1.LineType = ltGas;
                var l2 = new Line(new XYZ(200, 0, 0), new XYZ(300, 0, 0)) { Layer = lay };
                l2.LineType = ltSimple;
                d.ModelSpace.Entities.Add(l1);
                d.ModelSpace.Entities.Add(l2);
                var l3 = new Line(new XYZ(400, 0, 0), new XYZ(500, 0, 0)) { Layer = lay };
                l3.LineType = ltShape;
                d.ModelSpace.Entities.Add(l3);
                var l4 = new Line(new XYZ(600, 0, 0), new XYZ(700, 0, 0)) { Layer = lay };
                l4.LineType = ltCn;
                d.ModelSpace.Entities.Add(l4);
                string outP = ResolvePath(a1, baseDir);
                DwgWriter.Write(outP, d, new DwgWriterConfiguration { WriteShapes = true });
                Console.WriteLine($"[MKTYPES] wrote {outP} size={new FileInfo(outP).Length}");
                // R2013 variant: verifies the R2007+ (UTF-16 text region) path
                d.Header.Version = ACadVersion.AC1032;
                string outP2 = Path.Combine(Path.GetDirectoryName(outP) ?? ".", "linetest2013.dwg");
                DwgWriter.Write(outP2, d, new DwgWriterConfiguration { WriteShapes = true });
                Console.WriteLine($"[MKTYPES] wrote {outP2} size={new FileInfo(outP2).Length}");
                return true;
            }
            case "stripvp":
            {
                if (a1 == null || a2 == null) { Console.Error.WriteLine("Usage: stripvp <NEW|in.dwg> <out.dwg> [version]"); return true; }
                CadDocument sd = a1.Equals("NEW", StringComparison.OrdinalIgnoreCase)
                    ? new CadDocument()
                    : DwgReader.Read(ResolvePath(a1, baseDir));
                int removed = 0;
                foreach (var blk in sd.BlockRecords.ToList())
                    foreach (var vp in blk.Entities.OfType<Viewport>().ToList())
                    { blk.Entities.Remove(vp); removed++; }
                if (a3 != null) sd.Header.Version = (ACadVersion)Enum.Parse(typeof(ACadVersion), a3, true);
                DwgWriter.Write(a2, sd, new DwgWriterConfiguration { WriteShapes = true });
                Console.WriteLine($"[STRIPVP] removed={removed} wrote {a2} size={new FileInfo(a2).Length}");
                return true;
            }
            case "dump":
            {
                if (a1 == null) { Console.Error.WriteLine("Usage: dump <file.dwg>"); return true; }
                string inP = ResolvePath(a1, baseDir);
                var dd = DwgReader.Read(inP, new DwgReaderConfiguration { Failsafe = false });
                Console.WriteLine($"[DUMP] {inP} version={dd.Header.Version} classes={dd.Classes.Count}");
                var hist = new Dictionary<string, int>();
                foreach (var o in dd.GetCadObjects<CadObject>())
                { string k = o.ObjectType.ToString(); hist[k] = hist.TryGetValue(k, out int c) ? c + 1 : 1; }
                foreach (var kv in hist.OrderByDescending(k => k.Value).ThenBy(k => k.Key))
                    Console.WriteLine($"[DUMP]   {kv.Value,5}  {kv.Key}");
                Console.WriteLine($"[DUMP] total={hist.Values.Sum()} distinct={hist.Count}");
                foreach (var vp in dd.GetCadObjects<Viewport>())
                    Console.WriteLine($"[VP] handle=0x{vp.Handle:X} ctr={vp.Center} w={vp.Width} h={vp.Height} dir={vp.ViewDirection} target={vp.ViewTarget}");
                foreach (var ins in dd.GetCadObjects<Insert>())
                    Console.WriteLine($"[INS] {(ins.IsMultiple ? "MINSERT" : "INSERT")} block={ins.Block?.Name} scale=({ins.XScale},{ins.YScale},{ins.ZScale}) rot={ins.Rotation} hasAtt={ins.HasAttributes}");
                return true;
            }
            case "classes":
            {
                if (a1 == null) { Console.Error.WriteLine("Usage: classes <file.dwg>"); return true; }
                var cdoc = DwgReader.Read(ResolvePath(a1, baseDir));
                Console.WriteLine($"[CLS] version={cdoc.Header.Version} classes={cdoc.Classes.Count}");
                foreach (var c in cdoc.Classes.OrderBy(c => c.ClassNumber))
                    Console.WriteLine($"[CLS] num={c.ClassNumber} dxf={c.DxfName} cpp={c.CppClassName} count={c.InstanceCount} proxy={c.ProxyFlags} zombie={c.WasZombie}");
                return true;
            }
            case "objinfo":
            {
                if (a1 == null) { Console.Error.WriteLine("Usage: objinfo <file.dwg|dxf> <0x..> [..|all]"); return true; }
                string inP = ResolvePath(a1, baseDir);
                CadDocument odoc = inP.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase) ? DxfReader.Read(inP) : DwgReader.Read(inP);
                var map = new Dictionary<ulong, CadObject>();
                foreach (var o in odoc.GetCadObjects<CadObject>()) map[o.Handle] = o;
                if (args.Skip(2).Any(x => x.Equals("all", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var o in map.Values.OrderBy(o => o.Handle))
                        Console.WriteLine($"[OI] ALL 0x{o.Handle:X} {o.GetType().Name}");
                    return true;
                }
                foreach (var ah in args.Skip(2))
                {
                    if (!ulong.TryParse(ah.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out ulong h))
                    { Console.WriteLine($"[OI] {ah}: not a hexadecimal handle"); continue; }
                    if (!map.TryGetValue(h, out var o)) { Console.WriteLine($"[OI] 0x{h:X}: <not found>"); continue; }
                    string extra = "";
                    if (o is TableEntry te) extra = $" name=\"{te.Name}\"";
                    else if (o is Entity ent) extra = $" layer={ent.Layer?.Name}";
                    Console.WriteLine($"[OI] 0x{h:X}: {o.GetType().Name} (OT={o.ObjectType}){extra}");
                }
                return true;
            }
            case "layoutdump":
            {
                if (a1 == null) { Console.Error.WriteLine("Usage: layoutdump <file.dwg|dxf>"); return true; }
                string inP = ResolvePath(a1, baseDir);
                CadDocument ldoc = inP.EndsWith(".dxf", StringComparison.OrdinalIgnoreCase) ? DxfReader.Read(inP) : DwgReader.Read(inP);
                Console.WriteLine($"[LD] version={ldoc.Header.Version}");
                foreach (var lay in ldoc.GetCadObjects<Layout>())
                    Console.WriteLine($"[LD] LAYOUT {lay.Name}#0x{lay.Handle:X} min=({lay.MinLimits}) max=({lay.MaxLimits}) assocBlock={(lay.AssociatedBlock?.Name ?? "null")}");
                foreach (var vp in ldoc.GetCadObjects<Viewport>())
                    Console.WriteLine($"[LD] VIEWPORT 0x{vp.Handle:X} dir={vp.ViewDirection} target={vp.ViewTarget} status={(int)vp.Status:X}");
                var allObjs = ldoc.GetCadObjects<CadObject>().ToList();
                ulong maxHandle = allObjs.Count > 0 ? allObjs.Max(o => o.Handle) : 0;
                Console.WriteLine($"[LD] HANDSEED=0x{ldoc.Header.HandleSeed:X} maxHandle=0x{maxHandle:X} totalObjects={allObjs.Count}");
                return true;
            }
            case "dimstyles":
            {
                if (a1 == null) { Console.Error.WriteLine("Usage: dimstyles <file.dwg>"); return true; }
                var ddoc = DwgReader.Read(ResolvePath(a1, baseDir));
                foreach (var ds in ddoc.DimensionStyles)
                    Console.WriteLine($"[DIMSTYLE] handle=0x{ds.Handle:X} name={ds.Name}");
                foreach (var de in ddoc.ModelSpace.Entities)
                    if (de is Dimension dim)
                        Console.WriteLine($"[DIM] handle=0x{dim.Handle:X} type={dim.GetType().Name} style={dim.Style?.Name}");
                return true;
            }
            case "inspect":
            {
                if (a1 == null) { Console.Error.WriteLine("Usage: inspect <file.dwg> [0x..]"); return true; }
                var doc = DwgReader.Read(ResolvePath(a1, baseDir));
                foreach (var ins in doc.ModelSpace.Entities.OfType<Insert>())
                    Console.WriteLine($"[ATT] insert=0x{ins.Handle:X} block={ins.Block?.Name} hasAttrs={ins.HasAttributes} count={ins.Attributes.Count}");
                foreach (var t in doc.ModelSpace.Entities.OfType<TableEntity>())
                {
                    Console.WriteLine($"[INSPECT] table=0x{t.Handle:X} block={(t.Block?.Name ?? "null")} rows={t.Rows.Count} cols={t.Columns.Count} style={t.Style?.Name}");
                    foreach (var br in t.Rows)
                        foreach (var cell in br.Cells)
                            Console.WriteLine($"[CELL] type={cell.Type} contents={cell.Contents.Count}");
                }
                foreach (var b in doc.BlockRecords.Where(b => b.Name.StartsWith("*T")))
                    Console.WriteLine($"[INSPECT] block {b.Name}#0x{b.Handle:X} entities={b.Entities.Count()}");
                Console.WriteLine($"[HDR] HandleSeed=0x{doc.Header.HandleSeed:X} objCount={doc.GetCadObjects<CadObject>().Count()}");
                return true;
            }
            case "inspecthatch":
            {
                if (a1 == null) { Console.Error.WriteLine("Usage: inspecthatch <file.dwg>"); return true; }
                var doc = DwgReader.Read(ResolvePath(a1, baseDir));
                foreach (var h in doc.ModelSpace.Entities.OfType<Hatch>())
                {
                    Console.WriteLine($"[HATCH] handle=0x{h.Handle:X} pattern={h.Pattern?.Name} isSolid={h.IsSolid}");
                    var g = h.GradientColor;
                    if (g != null)
                        Console.WriteLine($"  gradient.enabled={g.Enabled} name={g.Name} nColors={(g.Colors?.Count ?? 0)}");
                }
                return true;
            }
            case "inspectdim":
            {
                if (a1 == null) { Console.Error.WriteLine("Usage: inspectdim <file.dwg>"); return true; }
                var doc = DwgReader.Read(ResolvePath(a1, baseDir));
                int dimCount = 0, withBlock = 0, withXdict = 0;
                foreach (var d in doc.ModelSpace.Entities.OfType<Dimension>().OrderBy(x => x.Handle))
                {
                    dimCount++;
                    if (d.Block != null) withBlock++;
                    if (d.XDictionary != null) withXdict++;
                    string blockStr = d.Block == null ? "null" : d.Block.Name + "#0x" + d.Block.Handle.ToString("X");
                    Console.WriteLine($"[DIM] 0x{d.Handle:X} {d.GetType().Name} style={d.Style?.Name} block={blockStr} xdict={(d.XDictionary != null ? "yes" : "no")}");
                }
                Console.WriteLine($"[SUM] dims={dimCount} withBlock={withBlock} withXdict={withXdict}");
                foreach (var b in doc.BlockRecords.Where(b => b.Name.StartsWith("*D")).OrderBy(b => b.Name))
                    Console.WriteLine($"[D-BLOCK] {b.Name}#0x{b.Handle:X} entities={b.Entities.Count()}");
                return true;
            }
            case "tablestyle":
            {
                // Diagnostics: compare each cell style field of the "default TableStyle (new CadDocument)"
                // against the "TableStyle read from the source file" to pin down which field(s)
                // cause the AcDbTableStyle(2E) difference.
                string tag = a1 ?? "sample";
                CadDocument srcDoc = a1 == null ? new CadDocument() : DwgReader.Read(ResolvePath(a1, baseDir));
                var styles = new List<TableStyle>();
                foreach (var ts in srcDoc.GetCadObjects<TableStyle>()) styles.Add(ts);
                Console.WriteLine($"[TS] source={tag} TableStyle count={styles.Count}");
                foreach (var ts in styles)
                {
                    Console.WriteLine($"[TS] --- TableStyle \"{ts.Name}\" handle=0x{ts.Handle:X} desc=\"{ts.Description}\" flags=0x{(int)ts.Flags:X} flow={ts.FlowDirection}");
                    Console.WriteLine($"[TS]     horizMargin={ts.HorizontalCellMargin} vertMargin={ts.VerticalCellMargin} suppressTitle={ts.SuppressTitle} suppressHeader={ts.SuppressHeaderRow}");
                    var named = new List<TableStyle.CellStyle>();
                    if (ts.TableCellStyle != null) named.Add(ts.TableCellStyle);
                    if (ts.TitleCellStyle != null) named.Add(ts.TitleCellStyle);
                    if (ts.HeaderCellStyle != null) named.Add(ts.HeaderCellStyle);
                    if (ts.DataCellStyle != null) named.Add(ts.DataCellStyle);
                    foreach (var cs in ts.CellStyles) named.Add(cs);
                    foreach (var cs in named)
                    {
                        var tsStyle = cs.TextStyle;
                        object csId = cs.GetType().GetProperty("Id", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)?.GetValue(cs);
                        Console.WriteLine($"[TS]     CS id={csId} class={(int)cs.StyleClass} name=\"{cs.Name}\" hasData={cs.HasData} type={(int)cs.Type}");
                        Console.WriteLine($"[TS]        propOverride=0x{(int)cs.PropertyOverrideFlags:X} merge=0x{(int)cs.TableCellStylePropertyFlags:X} bg={cs.BackgroundColor} contentLayout=0x{(int)cs.ContentLayoutFlags:X} marginOverride=0x{(int)cs.MarginOverrideFlags:X}");
                        Console.WriteLine($"[TS]        valDataType={cs.ValueDataType} valUnitType={cs.ValueUnitType} valFmt=\"{cs.ValueFormatString}\" align={cs.Alignment} rotation={cs.Rotation} scale={cs.Scale}");
                        Console.WriteLine($"[TS]        textHeight={cs.TextHeight} color={cs.Color} propFlags=0x{(int)cs.PropertyFlags:X}");
                        // R2004 row-cell subset (the fields writeRowCellStyle actually writes)
                        Console.WriteLine($"[TS]        [R2004] cellAlign={cs.CellAlignment} textColor={cs.TextColor} fillColorOn={cs.IsFillColorOn}");
                        Console.WriteLine($"[TS]        [R2004] borders T={cs.TopBorder.ApplyBorder}/{(int)cs.TopBorder.Type} H={cs.HorizontalInsideBorder.ApplyBorder} B={cs.BottomBorder.ApplyBorder}/{(int)cs.BottomBorder.Type} L={cs.LeftBorder.ApplyBorder} V={cs.VerticalInsideBorder.ApplyBorder} R={cs.RightBorder.ApplyBorder}");
                        ulong tsHandle = tsStyle?.Handle ?? 0UL;
                        string tsDoc = tsStyle?.Document == null ? "null" : tsStyle.Document.GetType().Name;
                        Console.WriteLine($"[TS]        textStyle name=\"{tsStyle?.Name}\" handle=0x{tsHandle:X} doc={tsDoc}");
                    }
                    // The document's text style table
                    int tsc = 0;
                    foreach (var tstyle in srcDoc.TextStyles) { Console.WriteLine($"[TS]     TEXTSTYLE \"{tstyle.Name}\" handle=0x{tstyle.Handle:X}"); tsc++; }
                    Console.WriteLine($"[TS]     document text style count={tsc}");
                }
                return true;
            }
            case "clonedump":
            {
                // Clone-fidelity diagnostics: dump 5 categories of key data (text styles / linetypes /
                // multilinetype styles / hatch gradients / table fonts) for source-file vs clone-output comparison.
                // Usage: clonedump <file.dwg> [--clone]   --clone: instead of reading the file output,
                // clone the source document's entities in memory into a new document and dump that
                // (localizes loss at clone time vs write time)
                if (a1 == null) { Console.Error.WriteLine("Usage: clonedump <file.dwg> [--clone]"); return true; }
                bool inMemClone = a2 == "--clone";
                string inP = ResolvePath(a1, baseDir);
                var srcDoc = DwgReader.Read(inP, new DwgReaderConfiguration { Failsafe = false });
                CadDocument doc = srcDoc;
                if (inMemClone)
                {
                    doc = new CadDocument();
                    doc.Header.Version = srcDoc.Header.Version;
                    doc.Header.CodePage = srcDoc.Header.CodePage;
                    PreseedTables(srcDoc, doc);
                    foreach (Entity e in srcDoc.ModelSpace.Entities)
                        CloneWithDeps(srcDoc, doc, e);
                    Console.WriteLine("[CD] *** IN-MEMORY CLONE (before write) ***");
                }
                Console.WriteLine($"[CD] {inP} version={doc.Header.Version} codepage={doc.Header.CodePage}");

                Console.WriteLine("[CD] === TEXTSTYLE ===");
                foreach (var st in doc.TextStyles.OrderBy(s => s.Name))
                    Console.WriteLine($"[STYLE] \"{st.Name}\" font=\"{st.Filename}\" bigfont=\"{st.BigFontFilename}\" width={st.Width} tt=0x{(int)st.TrueType:X} flags=0x{(int)st.Flags:X} h={st.Height}");

                Console.WriteLine("[CD] === LTYPE ===");
                foreach (var lt in doc.LineTypes.OrderBy(l => l.Name))
                {
                    string pat = string.Join(",", lt.Segments.Select(s => s.Length.ToString("0.####")));
                    Console.WriteLine($"[LTYPE] \"{lt.Name}\" complex={lt.IsComplex} nseg={lt.Segments.Count()} desc=\"{Trunc(lt.Description, 30)}\" pattern=[{pat}]");
                    foreach (var seg in lt.Segments)
                    {
                        if (seg.Flags == 0 && string.IsNullOrEmpty(seg.Text)) continue;
                        Console.WriteLine($"[LT-S]   \"-{seg.Length:0.###}-\" flags=0x{(int)seg.Flags:X} text=\"{seg.Text}\" style=\"{seg.Style?.Name}\" scale={seg.Scale:0.##} rot={seg.Rotation:0.##}");
                    }
                }

                Console.WriteLine("[CD] === MLSTYLE ===");
                foreach (var ms in doc.MLineStyles.OrderBy(m => m.Name))
                {
                    Console.WriteLine($"[MLSTYLE] \"{ms.Name}\" flags=0x{(int)ms.Flags:X} nElem={ms.Elements.Count()}");
                    foreach (var el in ms.Elements)
                        Console.WriteLine($"[MS-E]   offset={el.Offset:0.###} ltype=\"{el.LineType?.Name}\" color={el.Color}");
                }

                Console.WriteLine("[CD] === HATCH ===");
                foreach (var h in doc.ModelSpace.Entities.OfType<Hatch>().OrderBy(x => x.Handle))
                {
                    var g = h.GradientColor;
                    string ginfo = g == null ? "null" :
                        $"enabled={g.Enabled} name=\"{g.Name}\" single={g.IsSingleColorGradient} angle={g.Angle:0.####} shift={g.Shift:0.####} tint={g.ColorTint:0.##} nColors={g.Colors?.Count ?? 0}";
                    string gcol = (g?.Colors == null || g.Colors.Count == 0) ? "" :
                        "[" + string.Join("|", g.Colors.Select(c => $"{c.Value:0.##}:0x{c.StoredColor:X8}")) + "]";
                    Console.WriteLine($"[HATCH] 0x{h.Handle:X} pat=\"{h.Pattern?.Name}\" solid={h.IsSolid} grad({ginfo}) colors {gcol}");
                }

                Console.WriteLine("[CD] === MLINE ===");
                foreach (var m in doc.ModelSpace.Entities.OfType<MLine>().OrderBy(x => x.Handle))
                {
                    string vinfo = m.Vertices == null ? "null" :
                        string.Join(";", m.Vertices.Take(4).Select(v => $"nSeg={v.Segments?.Count ?? 0}"));
                    Console.WriteLine($"[MLINE] 0x{m.Handle:X} style=\"{m.Style?.Name}\" nVtx={m.Vertices?.Count ?? 0} scale={m.ScaleFactor:0.###} ltype=\"{m.LineType?.Name}\" flags={m.Flags} vtx[{vinfo}]");
                }

                Console.WriteLine("[CD] === TABLE ===");
                foreach (var t in doc.ModelSpace.Entities.OfType<TableEntity>().OrderBy(x => x.Handle))
                {
                    Console.WriteLine($"[TABLE] 0x{t.Handle:X} contentStyle=\"{t.Style?.Name}\" styleOv={(t.CellStyleOverride == null ? "null" : t.CellStyleOverride.Name)} rows={t.Rows.Count} cols={t.Columns.Count}");
                    int shown = 0;
                    for (int r = 0; r < t.Rows.Count && shown < 20; r++)
                    {
                        var cells = t.Rows[r].Cells;
                        for (int c = 0; c < cells.Count && shown < 20; c++, shown++)
                        {
                            var cell = cells[c];
                            if (cell.Contents == null || cell.Contents.Count == 0)
                                Console.WriteLine($"  cell[{r},{c}] type={cell.Type} <empty>");
                            foreach (var cc in cell.Contents)
                            {
                                string txt = cc.CadValue?.Value as string ?? "";
                                txt = Trunc(txt, 26);
                                Console.WriteLine($"  cell[{r},{c}] type={cell.Type} ct={cc.ContentType} fmtHasData={cc.Format.HasData} fmtStyle=\"{cc.Format.TextStyle?.Name}\" txt=\"{txt}\"");
                            }
                        }
                    }
                }

                Console.WriteLine("[CD] === TSTYLE ===");
                foreach (var ts in doc.TableStyles.OrderBy(t => t.Name))
                {
                    string cfs = string.Join("|", new[] { ts.DataCellStyle, ts.HeaderCellStyle, ts.TableCellStyle, ts.TitleCellStyle }
                        .Select(cs => cs?.TextStyle?.Name ?? "<null>"));
                    Console.WriteLine($"[TSTYLE] \"{ts.Name}\" cellFont(data|header|table|title)=[{cfs}] nCellStyles={ts.CellStyles.Count}");
                    foreach (var cs in ts.CellStyles)
                        Console.WriteLine($"[TS-S]   \"{cs.Name}\" font=\"{cs.TextStyle?.Name}\"");
                }

                Console.WriteLine("[CD] === TEXT ===");
                foreach (var mt in doc.ModelSpace.Entities.OfType<MText>().OrderBy(x => x.Handle))
                    Console.WriteLine($"[MTEXT] 0x{mt.Handle:X} style=\"{mt.Style?.Name}\" layer={mt.Layer?.Name} txt=\"{Trunc(mt.Value, 40)}\"");
                foreach (var te in doc.ModelSpace.Entities.OfType<TextEntity>().OrderBy(x => x.Handle))
                    Console.WriteLine($"[TEXT ] 0x{te.Handle:X} style=\"{te.Style?.Name}\" layer={te.Layer?.Name} txt=\"{Trunc(te.Value, 40)}\"");

                Console.WriteLine("[CD] === LTYPE USAGE (model-space entities with linetypes other than Continuous/By*) ===");
                foreach (var e in doc.ModelSpace.Entities.OfType<Entity>().OrderBy(x => x.Handle))
                    if (e.LineType != null && !string.IsNullOrEmpty(e.LineType.Name)
                        && !e.LineType.Name.StartsWith("Continuous", StringComparison.OrdinalIgnoreCase)
                        && !e.LineType.Name.StartsWith("By", StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine($"[ENT] 0x{e.Handle:X} {e.GetType().Name} ltype=\"{e.LineType.Name}\"");
                return true;
            }
            case "perclass":
            {
                // Lightweight per-class clone: read the source -> group by entity type -> clone each
                // type into a brand-new blank DWG (keep associations).
                // Equivalent to RunPerClassClone in the default pipeline, but without the coordinate
                // transform / multi-version save, so AUDIT verification can be done in isolation.
                string srcP = ResolvePath(a1 ?? "sample.dwg", baseDir);
                var src = DwgReader.Read(srcP);
                RunPerClassClone(src, baseDir);
                return true;
            }
            case "clonert":
            {
                // Whole-document model-space clone round-trip: read the source -> brand-new dst
                // (copy Version+CodePage) -> CloneWithDeps all entities -> write out
                if (a1 == null || a2 == null) { Console.Error.WriteLine("Usage: clonert <in.dwg> <out.dwg> [nocodepage]"); return true; }
                string inP = ResolvePath(a1, baseDir);
                var srcDoc = DwgReader.Read(inP);
                var dstDoc = new CadDocument();
                dstDoc.Header.Version = srcDoc.Header.Version;
                // Copy CodePage by default; passing nocodepage reproduces the old pipeline behavior of "copying Version only"
                if (a3 == null || !a3.Equals("nocodepage", StringComparison.OrdinalIgnoreCase))
                    dstDoc.Header.CodePage = srcDoc.Header.CodePage;
                PreseedTables(srcDoc, dstDoc);
                int n = 0;
                foreach (var e in srcDoc.ModelSpace.Entities.ToList())
                {
                    if (CloneWithDeps(srcDoc, dstDoc, e) != null) n++;
                }
                string outP = ResolvePath(a2, baseDir);
                DwgWriter.Write(outP, dstDoc, new DwgWriterConfiguration { WriteShapes = true });
                Console.WriteLine($"[CLONERT] {inP} -> {outP} entities={n} size={new FileInfo(outP).Length}");
                return true;
            }
            case "iobench":
            {
                // IO performance benchmark: run standalone (no dependency on sample.dwg, generates its own 100k-entity benchmark drawing)
                RunIoBench(baseDir);
                PrintReport();
                return true;
            }
            case "readbench":
            {
                // DWG read phase-breakdown benchmark ([PATCH] DwgReadDiagnostics):
                //   readbench [in.dwg] [iteration count] [noid] [notype]
                //   default io_bench/bench_io.dwg, 5 iterations; noid = do not read SummaryInfo; notype = turn off per-type timing
                RunReadBench(baseDir, a1, a2, a3, a4);
                return true;
            }
            default:
                return false;   // No mode matched -> hand off to the default pipeline
        }
    }

    // ================= readbench: DWG read phase-breakdown benchmark (decode vs build sub-phases) =================
    // Depends on the [PATCH] diagnostics class ACadSharp.IO.DwgReadDiagnostics inside the library (internal, hence read via reflection).
    private static readonly Lazy<Type> _readDiagType = new(() => typeof(CadDocument).Assembly.GetType("ACadSharp.IO.DwgReadDiagnostics"));

    private static void DiagReset()
    {
        var t = _readDiagType.Value;
        t?.GetMethod("Reset", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
    }

    private static long DiagMs(string prop)
    {
        var t = _readDiagType.Value;
        var p = t?.GetProperty(prop, BindingFlags.Public | BindingFlags.Static);
        return p == null ? -1 : (long)p.GetValue(null);
    }

    private static int DiagObjCount()
    {
        var t = _readDiagType.Value;
        var p = t?.GetProperty("ObjectCount", BindingFlags.Public | BindingFlags.Static);
        return p == null ? 0 : (int)Convert.ChangeType(p.GetValue(null), typeof(int));
    }

    private static void DiagEnableTypeTimers()
    {
        var t = _readDiagType.Value;
        t?.GetMethod("EnableTypeTimers", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
    }

    private static string DiagTop(string method, int n)
    {
        var t = _readDiagType.Value;
        var m = t?.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
        return m == null ? null : m.Invoke(null, new object[] { n }) as string;
    }

    private static void RunReadBench(string baseDir, string a1, string a2, string a3, string a4)
    {
        // Position-independent flags: noid / notype may appear at any argument position (PowerShell omits empty arguments, so fixed positions are unreliable)
        bool readId = true, typeTimers = true;
        var pos = new List<string>();
        foreach (var a in new[] { a1, a2, a3, a4 })
        {
            if (a == null) continue;
            if (a.Equals("noid", StringComparison.OrdinalIgnoreCase)) { readId = false; continue; }
            if (a.Equals("notype", StringComparison.OrdinalIgnoreCase)) { typeTimers = false; continue; }
            pos.Add(a);
        }
        a1 = pos.Count > 0 ? pos[0] : null;
        a2 = pos.Count > 1 ? pos[1] : null;
        a3 = pos.Count > 2 ? pos[2] : null;

        string path = a1 != null ? ResolvePath(a1, baseDir) : Path.Combine(baseDir, "io_bench", "bench_io.dwg");
        int iter = a2 != null && int.TryParse(a2, out int n) && n > 0 ? n : 5;

        if (!File.Exists(path)) { Console.Error.WriteLine($"[READBENCH] Benchmark file not found: {path} (run iobench first to generate it)"); return; }
        long fileSize = new FileInfo(path).Length;

        Console.WriteLine("==========================================================");
        Console.WriteLine($"  DWG read phase-breakdown benchmark (readbench)");
        Console.WriteLine($"  File = {path}  ({fileSize / 1024.0 / 1024.0:F2} MB)");
        Console.WriteLine($"  Iterations = {iter}, ReadSummaryInfo={readId}");
        Console.WriteLine($"  Run time = {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine("==========================================================\n");

        if (_readDiagType.Value == null)
            Console.WriteLine("  ⚠️ DwgReadDiagnostics not found (the library lacks the [PATCH] diagnostics) - only the total elapsed time and the progress-event breakdown will be output.\n");

        var totals = new List<long>();
        var preDecode = new List<long>();
        var decodes = new List<long>();
        var tables = new List<long>();
        var dicts = new List<long>();
        var tblEntry = new List<long>();
        var objBuild = new List<long>();
        var post = new List<long>();
        var progDecodeSpan = new List<long>();
        var progBuildSpan = new List<long>();
        int objCount = 0, entCount = 0;
        var histogram = new Dictionary<string, int>();

        for (int i = 1; i <= iter; i++)
        {
            DiagReset();
            if (typeTimers) DiagEnableTypeTimers();   // [PERF] Per-type timing (notype = off, measures the pure P1/P2 effect)
            long ws0 = WsMB();
            var sw = Stopwatch.StartNew();
            long? firstEv = null, lastReadEv = null, lastBuildEv = null;
            int evCount = 0;
            CadDocument doc = null;
            try
            {
                var reader = new DwgReader(path)
                {
                    Configuration = new DwgReaderConfiguration { ReadSummaryInfo = readId }
                };
                reader.OnProgress += (s, e) =>
                {
                    long now = sw.ElapsedMilliseconds;
                    if (firstEv == null) firstEv = now;
                    if (e.Stage == ReadStage.Read) lastReadEv = now; else lastBuildEv = now;
                    evCount++;
                };
                doc = reader.Read();
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.Error.WriteLine($"[READBENCH] Read #{i} failed: {ex.Message}");
                return;
            }
            sw.Stop();
            long ws1 = WsMB();

            long total = sw.ElapsedMilliseconds;
            long dPre = DiagMs("PreDecodeMs"), dDec = DiagMs("DecodeMs"), dTbl = DiagMs("TablesMs");
            long dDict = DiagMs("DictionariesMs"), dTblEnt = DiagMs("TableEntryBuildMs"), dObj = DiagMs("ObjectBuildMs"), dPost = DiagMs("PostBuildMs");
            objCount = DiagObjCount();

            totals.Add(total); preDecode.Add(dPre); decodes.Add(dDec); tables.Add(dTbl);
            dicts.Add(dDict); tblEntry.Add(dTblEnt); objBuild.Add(dObj); post.Add(dPost);
            progDecodeSpan.Add((lastReadEv ?? 0) - (firstEv ?? 0));
            progBuildSpan.Add((lastBuildEv ?? 0) - (firstEv ?? 0));

            entCount = doc.ModelSpace.Entities.Count;
            histogram.Clear();
            foreach (var g in doc.ModelSpace.Entities.GroupBy(e => e.GetType().Name).OrderByDescending(g => g.Count()))
            {
                if (!histogram.ContainsKey(g.Key)) histogram[g.Key] = 0;
                histogram[g.Key] += g.Count();
            }

            Console.WriteLine($"  Iter {i}: total={total,6} ms | pre={dPre,5} dec={dDec,6} tables={dTbl,5} dict={dDict,4} tblEnt={dTblEnt,4} objBuild={dObj,6} post={dPost,4}" +
                              $" | progress events dec~{(lastReadEv ?? 0)-(firstEv ?? 0),5} build~{(lastBuildEv ?? 0)-(firstEv ?? 0),6} ev={evCount,7} | memory {ws0:N0}->{ws1:N0} MB");
            if (typeTimers)
            {
                string decTop3 = DiagTop("DecodeTop", 3), buildTop3 = DiagTop("BuildTop", 3);
                if (decTop3 != null) Console.WriteLine($"      Decode Top 3: {decTop3}");
                if (buildTop3 != null) Console.WriteLine($"      Build Top 3: {buildTop3}");
            }
            doc = null;
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        }

        long Median(List<long> l) { var s = l.OrderBy(x => x).ToList(); return s[s.Count / 2]; }

        Console.WriteLine("\n  ---------- Median ----------");
        Console.WriteLine($"  Total elapsed   : {Median(totals),7} ms");
        Console.WriteLine($"  Pre-decode (file/header) : {Median(preDecode),7} ms  ({100.0 * Median(preDecode) / Median(totals):F1}%)");
        Console.WriteLine($"  Object decode   : {Median(decodes),7} ms  ({100.0 * Median(decodes) / Median(totals):F1}%)");
        Console.WriteLine($"  Build-tables    : {Median(tables),7} ms  ({100.0 * Median(tables) / Median(totals):F1}%)");
        Console.WriteLine($"  Build-dictionaries : {Median(dicts),7} ms  ({100.0 * Median(dicts) / Median(totals):F1}%)");
        Console.WriteLine($"  Build-table entries : {Median(tblEntry),7} ms  ({100.0 * Median(tblEntry) / Median(totals):F1}%)");
        Console.WriteLine($"  Build-objects   : {Median(objBuild),7} ms  ({100.0 * Median(objBuild) / Median(totals):F1}%)  <- hotspot");
        Console.WriteLine($"  Finalize (storage/gradient) : {Median(post),7} ms  ({100.0 * Median(post) / Median(totals):F1}%)");
        Console.WriteLine($"  (progress-event basis: decode~{Median(progDecodeSpan)} ms, build~{Median(progBuildSpan)} ms)");
        Console.WriteLine($"  Decoded objects : {objCount:N0}, model-space entities = {entCount:N0}");
        Console.WriteLine("  Entity type histogram : " + string.Join(", ", histogram.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}x{kv.Value:N0}")));

        if (typeTimers)
        {
            string decTop10 = DiagTop("DecodeTop", 10), buildTop10 = DiagTop("BuildTop", 10);
            if (decTop10 != null) Console.WriteLine("  Decode Top 10 by type: " + decTop10);
            if (buildTop10 != null) Console.WriteLine("  Build Top 10 by type: " + buildTop10);
            Console.WriteLine("  (Per-type timing is taken from the last iteration)");
        }
    }

    private static string ResolvePath(string p, string baseDir)
        => Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p);

    // ================= Utility methods =================
    private static long WsMB()
    {
        using var p = Process.GetCurrentProcess();
        return p.WorkingSet64 / (1024 * 1024);
    }

    private static void ProgressBar(string label, int completed, int total)
    {
        int width = 30;
        if (total <= 0)
        {
            int indetFilled = completed % (width + 1);
            string indetBar = new string('#', Math.Min(indetFilled, width)) + new string('-', Math.Max(0, width - indetFilled));
            Console.Write($"\r  [{label,-10}] {indetBar}  ····· {completed,8:N0}");
            return;
        }
        double pct = Math.Min(100.0, (double)completed / total * 100.0);
        int filled = (int)(pct / 100.0 * width);
        string bar = new string('#', filled) + new string('-', width - filled);
        Console.Write($"\r  [{label,-10}] {bar} {pct,5:F1}% {completed,8:N0}/{total,8:N0}");
    }

    private static void EnsureUtf8()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
        try { System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); } catch { }
    }
}

/// <summary>A single item's result.</summary>
internal sealed class MethodResult
{
    public string Name { get; }
    public long Ms { get; }
    public long WsStartMB { get; }
    public long WsEndMB { get; }
    public long OutputBytes { get; }
    public string Status { get; }
    public MethodResult(string name, long ms, long wsStart, long wsEnd, long outBytes, string status)
    {
        Name = name; Ms = ms; WsStartMB = wsStart; WsEndMB = wsEnd; OutputBytes = outBytes; Status = status;
    }
}
