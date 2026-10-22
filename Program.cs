// dwgReadWrite —— ACadSharp 坐标系变换 + 全版本 DWG 保存 + 逐类克隆 + 各种模式读写
//
// 本程序是 sample.dwg 的"读写全模式 + 逐类克隆"实验台（测试工程，不纳入 git 提交）。
//
// 默认（无参数）流程：
//   1) 读取 sample.dwg；
//   2) 对模型空间所有实体做一次坐标系变换（缩放 + 绕原点旋转 + 平移）；
//   3) 保存为「所有支持的 DWG 版本」（AC1014~AC1032，共 7 个）；
//   4) 回读校验各版本实体数；
//   5) 全量克隆到全新 DWG（单文件）；
//   6) 逐类克隆：模型空间每种实体类型各自 Clone 到一个全新空白 DWG，各存一个文件
//      （保持关联：Layer/Block/Style/TableStyle 一并克隆重注册）；
//   7) 关联丢弃验证：Insert 单独克隆（置空 Block）验证"只克隆一个默认丢关联"。
//
// 命令行模式（恢复自被删的 CadTransformer 实验代码）：
//   dwgReadWrite <模式> [参数...]
//     empty <版本> [out.dwg]            写指定版本空文档
//     rt <in.dwg> <out.dwg> [版本]       纯往返（不做坐标变换）
//     stripvp <NEW|in.dwg> <out.dwg> [版本]  剥离所有视口后写出
//     dump <file.dwg>                   对象清单直方图（含视口/Insert 详情）
//     classes <file.dwg>                类表转储
//     objinfo <file.dwg|dxf> <0x..> [..|all]  按句柄查对象
//     layoutdump <file.dwg|dxf>         布局/视口/HANDSEED 内存状态直方图
//     dimstyles <file.dwg>              标注样式与标注引用
//     inspect <file.dwg> [0x..]         Table/Insert/ATT 状态直方图 + 按句柄查
//     inspecthatch <file.dwg>           HATCH 渐变/XDATA 明细
//     inspectdim <file.dwg>             标注关联（DIMASSOC）链路检查
//     iobench                          IO 性能基准（流式读写/增量保存/并行解码/惰性加载/进度事件，默认流程第⑨步）
//   （不带模式参数则执行上面的默认流程；首个参数若为文件路径也走默认流程）

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
    // ---------- 坐标系变换参数（缩放 / 旋转 / 平移） ----------
    private static readonly XYZ  TransformScale     = new XYZ(1.5, 1.5, 1.5);
    private static readonly double TransformAngleDeg = 30.0;
    private static readonly XYZ  TransformTranslate = new XYZ(1000.0, 500.0, 0);

    // ---------- 支持的 DWG 写版本（DwgWriter 实际可写集：AC1014~AC1032 共 7 个） ----------
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

        // ---------- 模式分派（恢复的"各种模式"读写） ----------
        if (args.Length > 0)
        {
            string mode = args[0];
            if (TryRunMode(mode, args, baseDir, samplePath))
                return;
            // 模式未命中（例如首个参数是文件路径）→ 走默认流程
        }

        RunDefaultPipeline(samplePath, baseDir);
        PrintReport();
        Console.WriteLine("\n完成。");
    }

    // ================= 默认流程：变换 + 全版本保存 + 全量克隆 + 逐类克隆 =================
    private static void RunDefaultPipeline(string samplePath, string baseDir)
    {
        Console.WriteLine("==========================================================");
        Console.WriteLine("  ACadSharp 坐标系变换 + 全版本 DWG 保存 + 逐类克隆");
        Console.WriteLine($"  变换 = 缩放 {TransformScale.X}×{TransformScale.Y}×{TransformScale.Z}" +
                          $"  旋转 {TransformAngleDeg}°(绕Z)  平移 ({TransformTranslate.X},{TransformTranslate.Y},{TransformTranslate.Z})");
        Console.WriteLine($"  支持版本 = {string.Join(", ", SupportedVersions.Select(v => v.Version))}");
        Console.WriteLine($"  运行时间 = {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine("==========================================================\n");

        if (!File.Exists(samplePath))
        {
            Console.Error.WriteLine($"❌ 找不到 sample.dwg：{samplePath}");
            return;
        }

        // ---------- ① 读取 sample.dwg ----------
        var readSw = Stopwatch.StartNew();
        long readWsStart = WsMB();
        CadDocument doc;
        Console.WriteLine("▶ [读取] sample.dwg");
        try
        {
            var reader = new DwgReader(samplePath)
            {
                Configuration = new DwgReaderConfiguration { ReadSummaryInfo = true }
            };
            reader.OnProgress += (s, e) => ProgressBar("读取", e.Completed, e.Total);
            doc = reader.Read();
            readSw.Stop();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"❌ 读取失败：{ex}");
            return;
        }
        long readWsEnd = WsMB();
        int modelCount = doc.ModelSpace.Entities.Count;
        Console.WriteLine();
        Console.WriteLine($"  ✅ 读取完成：版本={doc.Header.Version}，模型空间实体={modelCount:N0}，块={doc.BlockRecords.Count}，图层={doc.Layers.Count}");
        Console.WriteLine($"  读取耗时={readSw.ElapsedMilliseconds:N0} ms，内存 {readWsStart:N0}→{readWsEnd:N0} MB\n");
        _results.Add(new MethodResult("读取 sample.dwg", readSw.ElapsedMilliseconds, readWsStart, readWsEnd, 0, "OK"));

        // ---------- ② 逐类克隆（变换前，针对原始实体做"保持关联"克隆，各存一文件） ----------
        RunPerClassClone(doc, baseDir);

        // ---------- ③ 关联丢弃验证（Insert 置空 Block） ----------
        RunDropAssociationTest(doc, baseDir);

        // ---------- ④ 坐标系变换 ----------
        var tfSw = Stopwatch.StartNew();
        long tfWsStart = WsMB();
        double angleRad = TransformAngleDeg * Math.PI / 180.0;
        Transform t = new Transform(TransformTranslate, TransformScale, new XYZ(0, 0, angleRad));
        foreach (Entity e in doc.ModelSpace.Entities)
            e.ApplyTransform(t);
        tfSw.Stop();
        long tfWsEnd = WsMB();
        Console.WriteLine($"▶ [变换] 对 {modelCount:N0} 个模型空间实体施加坐标系变换");
        Console.WriteLine($"  ✅ 变换完成：耗时={tfSw.ElapsedMilliseconds:N0} ms，内存 {tfWsStart:N0}→{tfWsEnd:N0} MB\n");
        _results.Add(new MethodResult("坐标系变换", tfSw.ElapsedMilliseconds, tfWsStart, tfWsEnd, 0, "OK"));

        // ---------- ⑤ 重算模型空间范围 ----------
        SetModelExtentsFromEntities(doc);

        // ---------- ⑥ 保存为所有支持的 DWG 版本 ----------
        Console.WriteLine($"▶ [保存] 逐版本写出 {SupportedVersions.Length} 个 DWG（同一份已变换文档）");
        foreach (var v in SupportedVersions)
            SaveVersion(v.Name, v.Version, doc, baseDir);
        Console.WriteLine();

        // ---------- ⑦ 回读校验各版本实体数 ----------
        Console.WriteLine("▶ [回读校验] 各版本产物实体数（应≈变换前实体数）");
        ValidateOutputs(baseDir, modelCount);

        // ---------- ⑧ 全量克隆到全新 DWG（单文件） ----------
        RunFullCloneTest(samplePath, baseDir);

        // ---------- ⑨ IO 性能基准（流式读写 / 增量保存 / 并行解码 / 惰性加载 / 进度事件） ----------
        RunIoBench(baseDir);
    }

    // ================= 单版本保存 =================
    private static void SaveVersion(string name, ACadVersion version, CadDocument doc, string baseDir)
    {
        string outPath = Path.Combine(baseDir, $"out_{name.Replace('/', '_')}.dwg");
        long ws0 = WsMB();
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"  ▶ {name,-14} → {Path.GetFileName(outPath)}");
        try
        {
            doc.Header.Version = version;
            var writer = new DwgWriter(outPath, doc) { Configuration = new DwgWriterConfiguration() };
            // 写进度条：与读端 OnProgress 进度条镜像（按 SaveProgressReportInterval 节流触发，Completed==Total 时保证触发一次 100%）
            writer.OnSaveProgress += (s, e) => ProgressBar("写入", e.Completed, e.Total);
            writer.Write();
            writer.Dispose();
            sw.Stop();
            long ws1 = WsMB();
            long sz = new FileInfo(outPath).Length;
            Console.WriteLine();
            Console.WriteLine($"      ✅ {sw.ElapsedMilliseconds:N0} ms，内存 {ws0:N0}→{ws1:N0} MB，产物 {sz / 1024.0 / 1024.0:F2} MB");
            _results.Add(new MethodResult($"保存 {name}", sw.ElapsedMilliseconds, ws0, ws1, sz, "OK"));
        }
        catch (CadNotSupportedException ex)
        {
            sw.Stop();
            Console.WriteLine($"      ⚠️  不支持：{ex.Message}");
            _results.Add(new MethodResult($"保存 {name}", sw.ElapsedMilliseconds, ws0, WsMB(), 0, "UNSUPPORTED"));
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"      ❌ 失败：{ex.Message}");
            _results.Add(new MethodResult($"保存 {name}", sw.ElapsedMilliseconds, ws0, WsMB(), 0, "FAIL"));
        }
    }

    // ================= 全量克隆（读整图对象图 → 写新文件） =================
    private static void RunFullCloneTest(string samplePath, string baseDir)
    {
        string outPath = Path.Combine(baseDir, "out_fullclone.dwg");
        Console.WriteLine("\n▶ [克隆] 全量克隆 sample.dwg → 新文件 " + Path.GetFileName(outPath));
        var sw = Stopwatch.StartNew();
        long ws0 = WsMB();
        try
        {
            var reader = new DwgReader(samplePath) { Configuration = new DwgReaderConfiguration { ReadSummaryInfo = true } };
            CadDocument src = reader.Read();
            sw.Stop();
            long ws1 = WsMB();
            Console.WriteLine($"  ① 打开完成：版本={src.Header.Version}，实体={src.Entities.Count:N0}，块={src.BlockRecords.Count:N0}，图层={src.Layers.Count:N0}");

            var sw2 = Stopwatch.StartNew();
            long ws2 = WsMB();
            var writer = new DwgWriter(outPath, src) { Configuration = new DwgWriterConfiguration() };
            writer.Write();
            writer.Dispose();
            sw2.Stop();
            long ws3 = WsMB();
            long sz = new FileInfo(outPath).Length;
            Console.WriteLine($"  ② 克隆写出完成：耗时={sw2.ElapsedMilliseconds:N0} ms，内存 {ws2:N0}→{ws3:N0} MB，产物 {sz / 1024.0 / 1024.0:F2} MB");
            _results.Add(new MethodResult("全量克隆(读)", sw.ElapsedMilliseconds, ws0, ws1, 0, "OK"));
            _results.Add(new MethodResult("全量克隆(写)", sw2.ElapsedMilliseconds, ws2, ws3, sz, "OK"));

            CadDocument rb = new DwgReader(outPath).Read();
            bool ok = rb.Entities.Count == src.Entities.Count
                      && rb.ModelSpace.Entities.Count == src.ModelSpace.Entities.Count
                      && rb.BlockRecords.Count == src.BlockRecords.Count
                      && rb.Layers.Count == src.Layers.Count;
            Console.WriteLine($"  ③ 回读校验：实体={rb.Entities.Count:N0}（源 {src.Entities.Count:N0}） 块={rb.BlockRecords.Count:N0}（源 {src.BlockRecords.Count:N0}） {(ok ? "✅ 一致" : "⚠️ 不一致")}");
            if (!ok) _results.Add(new MethodResult("全量克隆(校验)", 0, 0, 0, 0, "MISMATCH"));
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.Error.WriteLine($"  ❌ 克隆失败：{ex}");
            _results.Add(new MethodResult("全量克隆", sw.ElapsedMilliseconds, ws0, WsMB(), 0, "FAIL"));
            Console.WriteLine();
        }
    }

    // ================= 逐类克隆：每种实体类型 → 一个全新空白 DWG（保持关联） =================
    /// <summary>
    /// 把模型空间的每种实体类型分别 Clone 到一个全新空白 DWG，各存一个文件。
    /// 关联保持：实体的 Layer/Block/Style/TableStyle 等引用一并克隆并重注册进新文档，
    /// 避免跨文档悬空指针（这是"两个数据都克隆要保持关联"的需求）。
    /// </summary>
    private static void RunPerClassClone(CadDocument src, string baseDir)
    {
        Console.WriteLine("\n" + new string('=', 80));
        Console.WriteLine("▶ [逐类克隆] 模型空间按实体类型分组，每类克隆到一个全新空白 DWG（保持关联）");
        Console.WriteLine(new string('=', 80));

        // 按运行时类型分组（保持出现顺序）
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
        Console.WriteLine($"  共 {groups.Count} 种实体类型，输出目录：{outDir}\n");

        int okCount = 0, failCount = 0;
        foreach (var (typeName, items) in groups)
        {
            string safe = SafeFile(typeName);
            string outPath = Path.Combine(outDir, $"{typeName}.dwg");
            var sw = Stopwatch.StartNew();
            try
            {
                CadDocument dst = new CadDocument();
                dst.Header.Version = src.Header.Version;   // 与源同版本，便于对比
                // [FIX] R2004 及更早版本的 DWG 按文档代码页写字符串；不复制 CodePage 时
                // 新文档默认 ANSI_1252，中文（文本、线型文字段等）全部变成 '?'。
                dst.Header.CodePage = src.Header.CodePage;

                // [PATCH] 先按"源内容优先"预填命名表，再克隆实体（解决默认条目名称冲突吞掉源内容）
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

                // 回读校验
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
                Console.WriteLine($"  [{(good ? "✅" : "⚠️")}] {typeName,-22} 克隆={added}/{items.Count}  回读模型={rbModel} 块={rbBlocks}  产物={sz / 1024.0:F0}KB  {sw.ElapsedMilliseconds:N0}ms  {rbStatus}");
                _results.Add(new MethodResult($"逐类 {typeName}", sw.ElapsedMilliseconds, 0, 0, sz, good ? "OK" : "CHECK"));
            }
            catch (Exception ex)
            {
                sw.Stop();
                failCount++;
                Console.WriteLine($"  [❌] {typeName,-22} 失败：{Trunc(ex.Message, 60)}");
                _results.Add(new MethodResult($"逐类 {typeName}", sw.ElapsedMilliseconds, 0, 0, 0, "FAIL"));
            }
        }

        Console.WriteLine($"\n  逐类克隆小结：成功 {okCount} / 需关注 {failCount}（共 {groups.Count} 类）");
        Console.WriteLine();
    }

    // ================= 关联丢弃验证：Insert 置空 Block =================
    /// <summary>
    /// 验证"只克隆一个时默认丢掉关联"：把 Insert 单独克隆到新空白文档，
    /// 通过反射置空其 Block（Insert.Block 为 internal setter），再写出回读，
    /// 观察块引用被丢弃后的产物状态。
    /// </summary>
    private static void RunDropAssociationTest(CadDocument src, string baseDir)
    {
        Console.WriteLine(new string('=', 80));
        Console.WriteLine("▶ [关联丢弃] Insert 单独克隆并置空 Block（只克隆一个 → 默认丢关联）");
        Console.WriteLine(new string('=', 80));

        Insert firstInsert = null;
        foreach (Entity e in src.ModelSpace.Entities)
            if (e is Insert ins && !(e is TableEntity)) { firstInsert = ins; break; }

        if (firstInsert == null)
        {
            Console.WriteLine("  （样本中无普通 Insert，跳过）");
            return;
        }
        string outPath = Path.Combine(baseDir, "drop_insert_block.dwg");
        try
        {
            string srcBlockName = firstInsert.Block?.Name ?? "<null>";
            CadDocument dst = new CadDocument();
            dst.Header.Version = src.Header.Version;

            Entity clone = (Entity)firstInsert.Clone();
            SetInsertBlock(clone, null);           // 反射置空 Block → 丢关联
            dst.ModelSpace.Entities.Add(clone);    // 触发 AssignDocument（Block=null 时跳过块重注册）

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

            Console.WriteLine($"  源 Insert block={srcBlockName} → 置空后写出 {sz / 1024.0:F0}KB，回读模型={rbModel} 块引用={rbBlock}");
            _results.Add(new MethodResult("关联丢弃(Insert)", 0, 0, 0, sz, "OK"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ 关联丢弃测试失败：{Trunc(ex.Message, 60)}");
            _results.Add(new MethodResult("关联丢弃(Insert)", 0, 0, 0, 0, "FAIL"));
        }
        Console.WriteLine();
    }

    // ================= 依赖重注册（保持关联的核心） =================
    /// <summary>
    /// 把 src 文档中的一个实体 Clone 到 dst 文档，保持关联。
    /// 原理：Clone() 已自动 detach Layer/LineType/Block/Style（fresh clones, Document=null）；
    /// 加入 dst 触发 AssignDocument 时自动按名重注册全部引用（Layer/LineType/Block/Style/TableStyle），
    /// 无需手动逐个克隆依赖。
    /// 返回加入 dst 的实体。
    /// </summary>
    private static Entity CloneWithDeps(CadDocument src, CadDocument dst, Entity srcEntity)
    {
        // 关键：先克隆源实体 → Owner=null, Document=null, Layer/LineType/Block/Style 均为 detached 副本
        Entity e = (Entity)srcEntity.Clone();

        // 加入 dst 模型空间 → 触发 onAdd → AddCadObject → AssignDocument(dst)
        // AssignDocument 链自动处理：
        //   Entity.AssignDocument  → updateTableEntry(Layer, LineType, dst) 按名注册
        //   Insert.AssignDocument  → BlockRecords.TryGetValue/Add 按名复用或新增
        //   Dimension.AssignDocument → updateTableEntry(Style, Block, dst) 按名注册
        //   TableEntity.AssignDocument → updateCollectionEntry(Content.Style, dst.TableStyles)
        //   BlockRecord.AssignDocument → RegisterCollection(Entities) → 块内实体逐一 AssignDocument
        dst.ModelSpace.Entities.Add(e);
        return e;
    }

    /// <summary>
    /// [PATCH] 克隆实体之前，把源文档各命名表的条目克隆进目标文档（同名冲突时源内容优先，
    /// 覆盖目标文档构造时自动生成的默认占位条目，如 TextStyle "Standard"、MLineStyle "Standard"）。
    /// 规则（用户确认）：原文档存在名称冲突时，以原文档内容优先级高。
    /// 必须克隆而不是移动：同一 src 还会被克隆出多个 dst（逐类基准），移动会破坏源文档。
    /// 顺序：被引用者先入表（TextStyle/LineType/Layer/DimensionStyle），引用者后入表
    /// （TableStyle/MLineStyle/MLeaderStyle），后者的 AssignDocument 按名重新注册内部引用。
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

    /// <summary>反射设置 Insert.Block（internal setter），用于"丢关联"测试。</summary>
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

    // ================= 用实体真实包围盒重算模型范围 =================
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

    // ================= 回读校验 =================
    private static void ValidateOutputs(string baseDir, int expected)
    {
        foreach (var v in SupportedVersions)
        {
            string p = Path.Combine(baseDir, $"out_{v.Name.Replace('/', '_')}.dwg");
            if (!File.Exists(p)) { Console.WriteLine($"  ⚠️  缺失：{Path.GetFileName(p)}"); continue; }
            try
            {
                var d = new DwgReader(p).Read();
                int cnt = d.Entities.Count;
                long sz = new FileInfo(p).Length;
                bool ok = Math.Abs(cnt - expected) <= 2;
                Console.WriteLine($"  {v.Name,-14} 实体={cnt:N0}（期望≈{expected:N0}） 大小={sz / 1024.0 / 1024.0:F2}MB  {(ok ? "✅" : "⚠️")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {v.Name,-14} 回读失败：{ex.Message}");
            }
        }
    }

    // ================= 汇总报告 =================
    private static void PrintReport()
    {
        Console.WriteLine("\n" + new string('=', 80));
        Console.WriteLine("  汇总报告（时间 / 产物大小 / 状态）");
        Console.WriteLine(new string('=', 80));
        Console.WriteLine($"  {"项目",-26} {"耗时(ms)",12} {"产物KB",10}  状态");
        Console.WriteLine("  " + new string('-', 60));
        foreach (var r in _results)
        {
            string szStr = r.OutputBytes > 0 ? (r.OutputBytes / 1024.0).ToString("F0") : "-";
            Console.WriteLine($"  {Trunc(r.Name, 26),-26} {r.Ms.ToString("N0"),12} {szStr,10}  {r.Status}");
        }
        Console.WriteLine(new string('=', 80));
    }

    // ================= ⑨ IO 性能基准：流式读写 / 增量保存 / 并行解码 / 惰性加载 / 进度事件 =================
    // 覆盖近期读写相关提交的改动：
    //   c8701519  DWG 增量保存（P0 脏句柄 / P1 patch-source）+ 并行序列化 + DWG 流式写 + DWG 并行解码 + 惰性部分解码
    //   2001e83c  读/写进度事件（DwgWriter.OnSaveProgress / 读取端 OnProgress）
    //   37adbe70  DXF 流式写 + XData 驻留 + 版本门控 DXF 读写
    //   715fd41d  版本门控 DWG 写（R14~R2007 + AC21 原生编码器）
    // sample.dwg 仅 0.14MB 不具代表性，故现场生成 10 万实体 + 全实体带 XData 的基准图用于所有大文件测试。
    // 产物目录：<baseDir>/io_bench/
    private static void RunIoBench(string baseDir)
    {
        string benchDir = Path.Combine(baseDir, "io_bench");
        Directory.CreateDirectory(benchDir);
        string benchDwg = Path.Combine(benchDir, "bench_io.dwg");
        string benchDxf = Path.Combine(benchDir, "bench_io.dxf");
        const int N = 100_000;

        Console.WriteLine(new string('=', 80));
        Console.WriteLine($"▶ [⑨ IO 性能基准] 现场生成 {N:N0} 实体基准图（全实体带 XData）→ DWG/DXF 流式读写 / 增量保存 / 并行解码 / 惰性加载 / 进度事件");
        Console.WriteLine(new string('=', 80));

        var sw = Stopwatch.StartNew();
        long ws0 = WsMB(), ws1;

        // ---------- GEN-1 生成基准大图 ----------
        CadDocument bench = null;
        try
        {
            bench = BuildBenchDocument(N);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"  ❌ 基准图生成失败: {Trunc(ex.Message, 80)}");
            _results.Add(new MethodResult("[IOB] GEN 基准图生成", sw.ElapsedMilliseconds, ws0, WsMB(), 0, "FAIL"));
            return;
        }
        sw.Stop(); ws1 = WsMB();
        Console.WriteLine($"  [GEN-1] 基准图生成 {N:N0} 实体 (Line60% / Poly2D15% / Arc10% / Text10% / Insert5%)  {sw.ElapsedMilliseconds:N0} ms  内存 {ws0:N0}→{ws1:N0} MB");
        _results.Add(new MethodResult("[IOB] GEN 基准图生成", sw.ElapsedMilliseconds, ws0, ws1, 0, "OK"));

        // ---------- GEN-2 写出基准 DWG（AC1018 基线文件） ----------
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        var wg = new DwgWriter(benchDwg, bench) { Configuration = new DwgWriterConfiguration() };
        wg.Write(); wg.Dispose();
        sw.Stop(); ws1 = WsMB();
        long dwgSz = new FileInfo(benchDwg).Length;
        Console.WriteLine($"  [GEN-2] 基准 DWG(AC1018) 写出  {sw.ElapsedMilliseconds:N0} ms  {dwgSz / 1024.0 / 1024.0:F2} MB  内存 {ws0:N0}→{ws1:N0} MB");
        _results.Add(new MethodResult("[IOB] GEN 基准DWG写出", sw.ElapsedMilliseconds, ws0, ws1, dwgSz, "OK"));

        // ---------- GEN-3 写出基准 DXF（AC1018 基线文件） ----------
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        var xd = new DxfWriter(benchDxf, bench) { Configuration = new DxfWriterConfiguration() };
        xd.Write(); xd.Dispose();
        sw.Stop(); ws1 = WsMB();
        long dxfSz = new FileInfo(benchDxf).Length;
        Console.WriteLine($"  [GEN-3] 基准 DXF(AC1018) 写出  {sw.ElapsedMilliseconds:N0} ms  {dxfSz / 1024.0 / 1024.0:F2} MB  内存 {ws0:N0}→{ws1:N0} MB");
        _results.Add(new MethodResult("[IOB] GEN 基准DXF写出", sw.ElapsedMilliseconds, ws0, ws1, dxfSz, "OK"));

        bench = null;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        // ================= DWG 组 =================
        Console.WriteLine(new string('-', 80));
        Console.WriteLine("▶ DWG 组：读基线 / 并行解码 / 增量保存(P1/P0) / 全量回退 / 并行序列化 / 流式写 / 惰性加载 / 保存进度事件");

        CadDocument docD = null;
        int totalEnt = 0;

        // ---------- D1 DWG 读基线（带读取端 OnProgress 进度事件） ----------
        int readEvents = 0, readLastC = 0, readLastT = -1;
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        var rdr1 = new DwgReader(benchDwg) { Configuration = new DwgReaderConfiguration { ReadSummaryInfo = true } };
        rdr1.OnProgress += (s, e) => { readEvents++; readLastC = e.Completed; readLastT = e.Total; };
        docD = rdr1.Read();
        sw.Stop(); ws1 = WsMB();
        totalEnt = docD.ModelSpace.Entities.Count;
        Console.WriteLine($"  [D1] DWG 读基线  {sw.ElapsedMilliseconds:N0} ms  实体={totalEnt:N0}  内存 {ws0:N0}→{ws1:N0} MB  进度事件={readEvents:N0} (末次 {readLastC:N0}/{readLastT:N0})");
        _results.Add(new MethodResult("[IOB] D1 DWG读基线", sw.ElapsedMilliseconds, ws0, ws1, 0, "OK"));

        // ---------- D2 DWG 并行解码读 ----------
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        var rdr2 = new DwgReader(benchDwg) { Configuration = new DwgReaderConfiguration { ReadSummaryInfo = true, ParallelDecode = true } };
        var doc2 = rdr2.Read();
        sw.Stop(); ws1 = WsMB();
        int cnt2 = doc2.ModelSpace.Entities.Count;
        string st2 = cnt2 == totalEnt ? "OK" : $"MISMATCH={cnt2:N0}";
        Console.WriteLine($"  [D2] DWG 并行解码读  {sw.ElapsedMilliseconds:N0} ms  实体={cnt2:N0}  {st2}  内存 {ws0:N0}→{ws1:N0} MB");
        _results.Add(new MethodResult("[IOB] D2 DWG并行解码读", sw.ElapsedMilliseconds, ws0, ws1, 0, st2 == "OK" ? "OK" : "CHECK"));
        doc2 = null;

        // ---------- D3 RetainPatchSource 读（为增量保存准备 patch 源） ----------
        DwgObjectPatchSource patch = null;
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        var rdr3 = new DwgReader(benchDwg) { Configuration = new DwgReaderConfiguration { ReadSummaryInfo = true, RetainPatchSource = true } };
        docD = rdr3.Read();
        patch = rdr3.PatchSource;
        sw.Stop(); ws1 = WsMB();
        Console.WriteLine($"  [D3] DWG 读(保留patch源)  {sw.ElapsedMilliseconds:N0} ms  patch源={(patch != null ? "就绪" : "NULL")}  实体={docD.ModelSpace.Entities.Count:N0}  内存 {ws0:N0}→{ws1:N0} MB");
        _results.Add(new MethodResult("[IOB] D3 DWG读+patch源", sw.ElapsedMilliseconds, ws0, ws1, 0, patch != null ? "OK" : "FAIL"));

        // ---------- D4 增量保存（无修改，P1 patch-source 策略） ----------
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
                    if (fb4 == null && e.Message.Contains("回退")) fb4 = Trunc(e.Message, 60);
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
        Console.WriteLine($"  [D4] 增量保存(无修改,P1)  {sw.ElapsedMilliseconds:N0} ms  产物={sz4s}  回读={st4}  通知=[{string.Join(" | ", n4)}]");
        _results.Add(new MethodResult("[IOB] D4 增量保存无修改", sw.ElapsedMilliseconds, ws0, ws1, sz4, st4 == "OK" ? "OK" : "CHECK"));

        // ---------- D5 增量保存（5 实体修改，P0 脏句柄策略，分阶段计时：读/写/回读） ----------
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

            // 前 5 条 Line 平移 (+123456.789, +1000)：原始 Y∈[0,60]，移动后 Y≥1000 为唯一标识
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
            dirty.Add(doc5.ModelSpace.Handle);  // 容器句柄：*Model_Space（实体列表有变）

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
                    if (fb5 == null && e.Message.Contains("回退")) fb5 = Trunc(e.Message, 60);
                }
            };
            wr5.Write(); wr5.Dispose();
            sw5w.Stop(); t5Write = sw5w.ElapsedMilliseconds;
            patch5?.Dispose();

            // 回读校验：总数一致 且 恰好 5 条 Line 的 StartPoint.Y≥1000
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
        Console.WriteLine($"  [D5] 增量保存(5实体修改,P0)  总={sw.ElapsedMilliseconds:N0} ms [读={t5Read:N0} 写={t5Write:N0} 回读={t5Rb:N0}]  产物={sz5 / 1024.0 / 1024.0:F2} MB  校验={st5}  通知=[{string.Join(" | ", n5)}]");
        _results.Add(new MethodResult("[IOB] D5 增量保存脏句柄", sw.ElapsedMilliseconds, ws0, ws1, sz5, st5 == "OK" ? "OK" : "CHECK"));
        patch?.Dispose();

        // ---------- D6 无 patch 源的全量保存（正确契约=静默全量保存，无回退通知） ----------
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
                Configuration = new DwgWriterConfiguration { IncrementalSave = true } // 无 PatchSource → 应自动回退全量
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
        Console.WriteLine($"  [D6] 全量回退(无patch源)  总={sw.ElapsedMilliseconds:N0} ms [读={t6Read:N0} 写={t6Write:N0} 回读={t6Rb:N0}]  产物={sz6 / 1024.0 / 1024.0:F2} MB  回读={st6}  通知={fb6 ?? "无(符合静默契约)"}");
        _results.Add(new MethodResult("[IOB] D6 全量回退验证", sw.ElapsedMilliseconds, ws0, ws1, sz6, st6 == "OK" ? "OK" : "CHECK"));

        // ---------- D7/D8 全量保存：串行 vs 并行序列化（期望字节一致） ----------
        string out7 = Path.Combine(benchDir, "bench_full_serial.dwg");
        string out8 = Path.Combine(benchDir, "bench_full_paral.dwg");
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        var wr7 = new DwgWriter(out7, docD) { Configuration = new DwgWriterConfiguration { IncrementalSave = false, ParallelSerialize = false } };
        wr7.Write(); wr7.Dispose();
        sw.Stop(); ws1 = WsMB();
        long sz7 = new FileInfo(out7).Length;
        Console.WriteLine($"  [D7] 全量保存(串行)  {sw.ElapsedMilliseconds:N0} ms  产物={sz7 / 1024.0 / 1024.0:F2} MB  内存 {ws0:N0}→{ws1:N0} MB");
        _results.Add(new MethodResult("[IOB] D7 全量保存串行", sw.ElapsedMilliseconds, ws0, ws1, sz7, "OK"));

        sw = Stopwatch.StartNew(); ws0 = WsMB();
        var wr8 = new DwgWriter(out8, docD) { Configuration = new DwgWriterConfiguration { IncrementalSave = false, ParallelSerialize = true } };
        wr8.Write(); wr8.Dispose();
        sw.Stop(); ws1 = WsMB();
        long sz8 = new FileInfo(out8).Length;
        bool same8 = Convert.ToBase64String(SHA256.HashData(File.ReadAllBytes(out7)))
                   == Convert.ToBase64String(SHA256.HashData(File.ReadAllBytes(out8)));
        string st8 = (sz8 == sz7 && same8) ? "OK+字节一致" : $"CHECK(sz {sz7:N0}→{sz8:N0} hash={(same8 ? "同" : "异")})";
        Console.WriteLine($"  [D8] 全量保存(并行序列化)  {sw.ElapsedMilliseconds:N0} ms  产物={sz8 / 1024.0 / 1024.0:F2} MB  对比D7: {st8}  内存 {ws0:N0}→{ws1:N0} MB");
        _results.Add(new MethodResult("[IOB] D8 全量保存并行", sw.ElapsedMilliseconds, ws0, ws1, sz8, same8 ? "OK" : "CHECK"));

        // ---------- D9/D10 DWG 流式写（internal API 经反射：BeginStreamingObjects/WriteEntityDirect/EndStreamingObjects） ----------
        RunDwgStreamingWrite(Path.Combine(benchDir, "bench_stream_ac1018.dwg"), ACadVersion.AC1018, 50_000, "[IOB] D9 DWG流式写AC1018");
        RunDwgStreamingWrite(Path.Combine(benchDir, "bench_stream_ac1021.dwg"), ACadVersion.AC1021, 50_000, "[IOB] D10 DWG流式写AC1021");

        // ---------- D11/D12 惰性加载：PreparePartialRead + ReadObjectsPartial ----------
        DwgReader rdr11 = null;
        DwgPartialReadContext ctx = null;
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        try
        {
            rdr11 = new DwgReader(benchDwg) { Configuration = new DwgReaderConfiguration { ReadSummaryInfo = true } };
            ctx = rdr11.PreparePartialRead();
            sw.Stop(); ws1 = WsMB();
            Console.WriteLine($"  [D11] PreparePartialRead  {sw.ElapsedMilliseconds:N0} ms  (仅表头+句柄区，未读对象)  内存 {ws0:N0}→{ws1:N0} MB");
            _results.Add(new MethodResult("[IOB] D11 PreparePartialRead", sw.ElapsedMilliseconds, ws0, ws1, 0, "OK"));
        }
        catch (Exception ex)
        {
            sw.Stop(); ws1 = WsMB();
            Console.WriteLine($"  [D11] 惰性加载准备失败: {Trunc(ex.Message, 80)}");
            _results.Add(new MethodResult("[IOB] D11 PreparePartialRead", sw.ElapsedMilliseconds, ws0, ws1, 0, "FAIL"));
        }

        if (ctx != null)
        {
            // seed = 全部表头句柄 + 模型空间前 100 个实体句柄；noExpand = *MODEL_SPACE/*PAPER_SPACE（不展开整张实体清单）
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
                Console.WriteLine($"  [D12] ReadObjectsPartial (seed={seeds.Count}句柄, noExpand={noExpand.Count})  {sw.ElapsedMilliseconds:N0} ms  解码={decCount:N0}/{fullObjs:N0}对象  模型空间实体={decModel}  内存 {ws0:N0}→{ws1:N0} MB");
                // 诊断：解码对象类型直方图 + 非种子模型空间实体的句柄分布（解释展开来源）
                var hist = doc12.GetCadObjects<CadObject>()
                    .GroupBy(o => o.GetType().Name)
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .Select(g => $"{g.Key}={g.Count():N0}")
                    .ToList();
                Console.WriteLine($"  [D12] 类型直方图: {string.Join("  ", hist)}");
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
                    Console.WriteLine($"  [D12] 非种子模型空间实体={extraTop:N0}  句柄范围=[{exMin:N0}..{exMax:N0}]");
                }
            }
            catch (Exception ex)
            {
                st12 = "FAIL:" + Trunc(ex.Message, 40);
                Console.WriteLine($"  [D12] 部分读取失败: {Trunc(ex.Message, 80)}");
            }
            sw.Stop(); ws1 = WsMB();
            _results.Add(new MethodResult("[IOB] D12 部分读100种子", sw.ElapsedMilliseconds, ws0, ws1, 0, st12 == "OK" ? "OK" : "CHECK"));
        }

        // ---------- D13 全量保存 + OnSaveProgress 写进度事件 ----------
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
        Console.WriteLine($"  [D13] 全量保存+OnSaveProgress  {sw.ElapsedMilliseconds:N0} ms  产物={sz13 / 1024.0 / 1024.0:F2} MB  进度事件={saveEvents:N0}  末次={saveLastC:N0}/{saveLastT:N0} ({pct13:F1}%)");
        _results.Add(new MethodResult("[IOB] D13 保存进度事件", sw.ElapsedMilliseconds, ws0, ws1, sz13, st13));

        docD = null;
        rdr11 = null; ctx = null;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();

        // ================= DXF 组 =================
        Console.WriteLine(new string('-', 80));
        Console.WriteLine("▶ DXF 组：读基线 / 全量写 / 流式写 / XData驻留开关 / ReadXData / 版本门控往返");

        CadDocument docX = null;
        int xEnt = 0;
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        try
        {
            docX = DxfReader.Read(benchDxf);
            xEnt = docX.ModelSpace.Entities.Count;
            sw.Stop(); ws1 = WsMB();
            string stX1 = xEnt == totalEnt ? "OK" : $"CHECK={xEnt:N0}";
            Console.WriteLine($"  [X1] DXF 读基线  {sw.ElapsedMilliseconds:N0} ms  实体={xEnt:N0}  内存 {ws0:N0}→{ws1:N0} MB  {stX1}");
            _results.Add(new MethodResult("[IOB] X1 DXF读基线", sw.ElapsedMilliseconds, ws0, ws1, 0, stX1 == "OK" ? "OK" : "CHECK"));
        }
        catch (Exception ex)
        {
            sw.Stop(); ws1 = WsMB();
            Console.WriteLine($"  [X1] DXF 读取失败: {Trunc(ex.Message, 80)}");
            _results.Add(new MethodResult("[IOB] X1 DXF读基线", sw.ElapsedMilliseconds, ws0, ws1, 0, "FAIL"));
            return;
        }

        // ---------- X2 DXF 全量写（基线） ----------
        string outX2 = Path.Combine(benchDir, "bench_dxf_full.dxf");
        sw = Stopwatch.StartNew(); ws0 = WsMB();
        var wx2 = new DxfWriter(outX2, docX) { Configuration = new DxfWriterConfiguration() };
        wx2.Write(); wx2.Dispose();
        sw.Stop(); ws1 = WsMB();
        long szX2 = new FileInfo(outX2).Length;
        Console.WriteLine($"  [X2] DXF 全量写  {sw.ElapsedMilliseconds:N0} ms  产物={szX2 / 1024.0 / 1024.0:F2} MB  内存 {ws0:N0}→{ws1:N0} MB");
        _results.Add(new MethodResult("[IOB] X2 DXF全量写", sw.ElapsedMilliseconds, ws0, ws1, szX2, "OK"));

        // ---------- X3 DXF 流式写（前 30k 实体：克隆 + WriteEntityOne + 字节拼接） ----------
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
            Console.WriteLine($"  [X3] DXF 流式写({SX:N0}实体)  {sw.ElapsedMilliseconds:N0} ms  产物={szX3 / 1024.0 / 1024.0:F2} MB  回读={stX3}  内存 {ws0:N0}→{ws1:N0} MB");
            _results.Add(new MethodResult("[IOB] X3 DXF流式写30k", sw.ElapsedMilliseconds, ws0, ws1, szX3, stX3 == "OK" ? "OK" : "CHECK"));
        }

        // ---------- X7 版本门控 DXF 往返（7 版本 写+回读；须在 docX 置空之前执行） ----------
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
            Console.WriteLine($"  [X7] DXF 版本门控 {v.Version}  写+回读  {sw.ElapsedMilliseconds:N0} ms  产物={szX7 / 1024.0 / 1024.0:F2} MB  {stX7}");
            _results.Add(new MethodResult($"[IOB] X7 DXF往返{v.Name}", sw.ElapsedMilliseconds, ws0, ws1, szX7, stX7 == "OK" ? "OK" : "CHECK"));
            GC.Collect();
        }

        // ---------- X4/X5 XData 驻留 开/关（对比内存） ----------
        // XData 驻留表为进程级静态 → 两轮之间 Clear() + GC，保证基线一致
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
            Console.WriteLine($"  [X4] DXF 读(XData驻留开)  {sw.ElapsedMilliseconds:N0} ms  实体={cntOn:N0}  内存增量={wsOn:N0} MB (起{ws0:N0}→终{ws1:N0})");
            _results.Add(new MethodResult("[IOB] X4 XData驻留开", sw.ElapsedMilliseconds, ws0, ws1, 0, cntOn == totalEnt ? "OK" : "CHECK"));
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
            Console.WriteLine($"  [X5] DXF 读(XData驻留关)  {sw.ElapsedMilliseconds:N0} ms  实体={cntOff:N0}  内存增量={wsOff:N0} MB (起{ws0:N0}→终{ws1:N0})  较X4内存差={wsOff - wsOn:N0} MB");
            _results.Add(new MethodResult("[IOB] X5 XData驻留关", sw.ElapsedMilliseconds, ws0, ws1, 0, cntOff == totalEnt ? "OK" : "CHECK"));
            docOff = null;
        }

        // ---------- X6 ReadXData=false（跳过 XData） ----------
        {
            sw = Stopwatch.StartNew(); ws0 = WsMB();
            var doc6 = DxfReader.Read(benchDxf, new DxfReaderConfiguration { ReadXData = false });
            int cnt6 = doc6.ModelSpace.Entities.Count;
            sw.Stop(); ws1 = WsMB();
            long ws6 = ws1 - ws0;
            Console.WriteLine($"  [X6] DXF 读(ReadXData=false)  {sw.ElapsedMilliseconds:N0} ms  实体={cnt6:N0}  内存增量={ws6:N0} MB (起{ws0:N0}→终{ws1:N0})  较X5内存差={ws6 - wsOff:N0} MB");
            _results.Add(new MethodResult("[IOB] X6 不读XData", sw.ElapsedMilliseconds, ws0, ws1, 0, cnt6 == totalEnt ? "OK" : "CHECK"));
            doc6 = null;
        }

        docX = null;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Console.WriteLine(new string('=', 80));
    }

    // ---------- iobench: 生成 10 万实体基准图（全实体带 XData） ----------
    private static CadDocument BuildBenchDocument(int n)
    {
        var doc = new CadDocument();
        doc.Header.Version = ACadVersion.AC1018;

        // 基准块：1 Line + 1 Arc + 1 Text
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

            // 全实体挂 XData：2 字符串（低基数→驻留友好）+ 1 整型 + 1 实型
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

    // ---------- iobench: DWG 流式写（internal API 经反射调用） ----------
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
            if (mBegin == null) throw new MissingMethodException("BeginStreamingObjects 未找到");
            object objWriter = mBegin.Invoke(writer, null);
            var mWrite = objWriter.GetType().GetMethod("WriteEntityDirect", BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(Entity) }, null);
            if (mWrite == null) throw new MissingMethodException("WriteEntityDirect 未找到");
            foreach (Entity e in dst.ModelSpace.Entities)
                mWrite.Invoke(objWriter, new object[] { e });
            var mEnd = wt.GetMethod("EndStreamingObjects", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(CadDocument), objWriter.GetType() }, null);
            if (mEnd == null) throw new MissingMethodException("EndStreamingObjects 未找到");
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
        Console.WriteLine($"  [{label}]  DWG流式写({count:N0} Line, {version})  {sw.ElapsedMilliseconds:N0} ms  产物={sz / 1024.0 / 1024.0:F2} MB  回读={status}  内存 {ws0:N0}→{ws1:N0} MB");
        _results.Add(new MethodResult(label, sw.ElapsedMilliseconds, ws0, ws1, sz, status == "OK" ? "OK" : "CHECK"));
    }

    // ================= 模式分派（恢复的"各种模式"读写） =================
    private static bool TryRunMode(string mode, string[] args, string baseDir, string samplePath)
    {
        string a1 = args.Length > 1 ? args[1] : null;
        string a2 = args.Length > 2 ? args[2] : null;
        string a3 = args.Length > 3 ? args[3] : null;
        switch (mode.ToLowerInvariant())
        {
            case "empty":
            {
                if (a1 == null) { Console.Error.WriteLine("用法: empty <版本> [out.dwg]"); return true; }
                ACadVersion ver = (ACadVersion)Enum.Parse(typeof(ACadVersion), a1, true);
                string outP = a2 ?? Path.Combine(baseDir, $"empty_{a1}.dwg");
                var d = new CadDocument(); d.Header.Version = ver;
                DwgWriter.Write(outP, d, new DwgWriterConfiguration { WriteShapes = true });
                Console.WriteLine($"[EMPTY] wrote {outP} version={ver} size={new FileInfo(outP).Length}");
                return true;
            }
            case "rt":
            {
                if (a1 == null || a2 == null) { Console.Error.WriteLine("用法: rt <in.dwg> <out.dwg> [版本]"); return true; }
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
                // 合成线型测试: 字面名 "--GAS--;" 的自定义线型(带文字段+样式) + 简单虚线
                if (a1 == null) { Console.Error.WriteLine("用法: mktypes <out.dwg>"); return true; }
                var d = new CadDocument();
                d.Header.Version = ACadVersion.AC1018;
                d.Header.CodePage = "gb2312";
                var st = new TextStyle("HZTXT") { Filename = "simplex.shx", BigFontFilename = "hztxt.shx" };
                d.TextStyles.Add(st);
                var ltGas = new LineType("--GAS--;") { Description = "Gas line ----GAS----" };
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
                var ltShape = new LineType("--GAS-SHAPE--;") { Description = "shape segment gas" };
                ltShape.AddSegment(new LineType.Segment { Length = 12.7 });
                ltShape.AddSegment(new LineType.Segment { Length = -5.08, Flags = LineTypeShapeFlags.Shape, ShapeNumber = 1, Scale = 0.5, Style = stShape });
                ltShape.AddSegment(new LineType.Segment { Length = -6.35 });
                d.LineTypes.Add(ltShape);
                var ltCn = new LineType("--GAS-CN--;") { Description = "煤气线 ----煤气----" };
                ltCn.AddSegment(new LineType.Segment { Length = 12.7 });
                ltCn.AddSegment(new LineType.Segment { Length = -5.08, Flags = LineTypeShapeFlags.Text, Text = "煤气", Scale = 0.5, Style = st });
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
                // R2013 变体: 验证 R2007+ (UTF-16 文本区) 路径
                d.Header.Version = ACadVersion.AC1032;
                string outP2 = Path.Combine(Path.GetDirectoryName(outP) ?? ".", "linetest2013.dwg");
                DwgWriter.Write(outP2, d, new DwgWriterConfiguration { WriteShapes = true });
                Console.WriteLine($"[MKTYPES] wrote {outP2} size={new FileInfo(outP2).Length}");
                return true;
            }
            case "stripvp":
            {
                if (a1 == null || a2 == null) { Console.Error.WriteLine("用法: stripvp <NEW|in.dwg> <out.dwg> [版本]"); return true; }
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
                if (a1 == null) { Console.Error.WriteLine("用法: dump <file.dwg>"); return true; }
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
                if (a1 == null) { Console.Error.WriteLine("用法: classes <file.dwg>"); return true; }
                var cdoc = DwgReader.Read(ResolvePath(a1, baseDir));
                Console.WriteLine($"[CLS] version={cdoc.Header.Version} classes={cdoc.Classes.Count}");
                foreach (var c in cdoc.Classes.OrderBy(c => c.ClassNumber))
                    Console.WriteLine($"[CLS] num={c.ClassNumber} dxf={c.DxfName} cpp={c.CppClassName} count={c.InstanceCount} proxy={c.ProxyFlags} zombie={c.WasZombie}");
                return true;
            }
            case "objinfo":
            {
                if (a1 == null) { Console.Error.WriteLine("用法: objinfo <file.dwg|dxf> <0x..> [..|all]"); return true; }
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
                    { Console.WriteLine($"[OI] {ah}: 非十六进制句柄"); continue; }
                    if (!map.TryGetValue(h, out var o)) { Console.WriteLine($"[OI] 0x{h:X}: <不存在>"); continue; }
                    string extra = "";
                    if (o is TableEntry te) extra = $" name=\"{te.Name}\"";
                    else if (o is Entity ent) extra = $" layer={ent.Layer?.Name}";
                    Console.WriteLine($"[OI] 0x{h:X}: {o.GetType().Name} (OT={o.ObjectType}){extra}");
                }
                return true;
            }
            case "layoutdump":
            {
                if (a1 == null) { Console.Error.WriteLine("用法: layoutdump <file.dwg|dxf>"); return true; }
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
                if (a1 == null) { Console.Error.WriteLine("用法: dimstyles <file.dwg>"); return true; }
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
                if (a1 == null) { Console.Error.WriteLine("用法: inspect <file.dwg> [0x..]"); return true; }
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
                if (a1 == null) { Console.Error.WriteLine("用法: inspecthatch <file.dwg>"); return true; }
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
                if (a1 == null) { Console.Error.WriteLine("用法: inspectdim <file.dwg>"); return true; }
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
                // 诊断：对比「默认 TableStyle（new CadDocument）」与「读自源文件的 TableStyle」的
                // 每个 cell style 字段，定位导致 AcDbTableStyle(2E) 的差异字段。
                string tag = a1 ?? "sample";
                CadDocument srcDoc = a1 == null ? new CadDocument() : DwgReader.Read(ResolvePath(a1, baseDir));
                var styles = new List<TableStyle>();
                foreach (var ts in srcDoc.GetCadObjects<TableStyle>()) styles.Add(ts);
                Console.WriteLine($"[TS] 来源={tag} TableStyle 数量={styles.Count}");
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
                        // R2004 row-cell 子集（writeRowCellStyle 实际写出的字段）
                        Console.WriteLine($"[TS]        [R2004] cellAlign={cs.CellAlignment} textColor={cs.TextColor} fillColorOn={cs.IsFillColorOn}");
                        Console.WriteLine($"[TS]        [R2004] borders T={cs.TopBorder.ApplyBorder}/{(int)cs.TopBorder.Type} H={cs.HorizontalInsideBorder.ApplyBorder} B={cs.BottomBorder.ApplyBorder}/{(int)cs.BottomBorder.Type} L={cs.LeftBorder.ApplyBorder} V={cs.VerticalInsideBorder.ApplyBorder} R={cs.RightBorder.ApplyBorder}");
                        ulong tsHandle = tsStyle?.Handle ?? 0UL;
                        string tsDoc = tsStyle?.Document == null ? "null" : tsStyle.Document.GetType().Name;
                        Console.WriteLine($"[TS]        textStyle name=\"{tsStyle?.Name}\" handle=0x{tsHandle:X} doc={tsDoc}");
                    }
                    // 该文档的文本样式表
                    int tsc = 0;
                    foreach (var tstyle in srcDoc.TextStyles) { Console.WriteLine($"[TS]     TEXTSTYLE \"{tstyle.Name}\" handle=0x{tstyle.Handle:X}"); tsc++; }
                    Console.WriteLine($"[TS]     文档文本样式数量={tsc}");
                }
                return true;
            }
            case "clonedump":
            {
                // 克隆保真诊断：转储 5 类关键数据（文字样式/线型/多线样式/填充渐变/表格字体），用于源文件 vs 克隆产物对比
                // 用法: clonedump <file.dwg> [--clone]   --clone: 不读文件产物，而是内存克隆源文档实体到新文档后转储（定位 clone 时 vs write 时丢失）
                if (a1 == null) { Console.Error.WriteLine("用法: clonedump <file.dwg> [--clone]"); return true; }
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
                    Console.WriteLine("[CD] *** IN-MEMORY CLONE (write 前) ***");
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

                Console.WriteLine("[CD] === LTYPE USAGE (模型空间非 Continuous/By* 线型的实体) ===");
                foreach (var e in doc.ModelSpace.Entities.OfType<Entity>().OrderBy(x => x.Handle))
                    if (e.LineType != null && !string.IsNullOrEmpty(e.LineType.Name)
                        && !e.LineType.Name.StartsWith("Continuous", StringComparison.OrdinalIgnoreCase)
                        && !e.LineType.Name.StartsWith("By", StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine($"[ENT] 0x{e.Handle:X} {e.GetType().Name} ltype=\"{e.LineType.Name}\"");
                return true;
            }
            case "perclass":
            {
                // 轻量逐类克隆：读源文件 → 按实体类型分组 → 每类克隆到一个全新空白 DWG（保持关联）。
                // 等价于默认流水线里的 RunPerClassClone，但不做坐标变换/多版本保存，便于单独 AUDIT 验证。
                string srcP = ResolvePath(a1 ?? "sample.dwg", baseDir);
                var src = DwgReader.Read(srcP);
                RunPerClassClone(src, baseDir);
                return true;
            }
            case "clonert":
            {
                // 整文档模型空间克隆往返：读源 → 全新 dst(复制 Version+CodePage) → CloneWithDeps 全部实体 → 写出
                if (a1 == null || a2 == null) { Console.Error.WriteLine("用法: clonert <in.dwg> <out.dwg> [nocodepage]"); return true; }
                string inP = ResolvePath(a1, baseDir);
                var srcDoc = DwgReader.Read(inP);
                var dstDoc = new CadDocument();
                dstDoc.Header.Version = srcDoc.Header.Version;
                // 默认复制 CodePage；传 nocodepage 复现"只复制 Version"的旧流水线行为
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
                // IO 性能基准：独立运行（不依赖 sample.dwg，自生成 10 万实体基准图）
                RunIoBench(baseDir);
                PrintReport();
                return true;
            }
            default:
                return false;   // 未命中模式 → 交给默认流程
        }
    }

    private static string ResolvePath(string p, string baseDir)
        => Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p);

    // ================= 工具方法 =================
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

/// <summary>单项结果。</summary>
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
