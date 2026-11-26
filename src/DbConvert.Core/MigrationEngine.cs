using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DbConvert
{
    /// <summary>迁移编排：发现表 → 生成/执行 DDL → 搬数据 → 建索引 → 校验 → 出报告。</summary>
    public class MigrationEngine
    {
        private readonly MigrationConfig _cfg;
        private readonly Action<string> _log;
        private readonly List<string> _warnings = new List<string>();
        private readonly IssueLog _issues = new IssueLog();

        public MigrationEngine(MigrationConfig cfg, Action<string> log)
        {
            _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            _log = log ?? (s => { });
        }

        public List<string> Warnings => _warnings;

        /// <summary>全对象迁移的问题清单（阻断/需人工/已改写/已跳过）。</summary>
        public IssueLog Issues => _issues;

        public OptionsConfig Options => _cfg.Options ?? new OptionsConfig();

        /// <summary>连接源库并读取表结构（已套用 include/exclude 与表映射、列覆盖）。</summary>
        public List<TableDef> DiscoverTables()
        {
            using (var src = SchemaSession.Open(_cfg.Source, _log))
            {
                string targetSchema = string.IsNullOrEmpty(_cfg.Target.Schema) ? Dialects.Get(_cfg.Target.Kind).DefaultSchema : _cfg.Target.Schema;
                Dialect targetDialect = Dialects.Get(_cfg.Target.Kind);
                return src.LoadTables(_cfg.Options, _cfg.Tables, _log, _warnings, targetSchema, targetDialect);
            }
        }

        /// <summary>给 GUI 用：补上源方言与名称映射、视图，生成和 CLI plan 一致的完整脚本。</summary>
        public string GenerateFullScript(List<TableDef> tables)
        {
            using (var src = SchemaSession.Open(_cfg.Source, _log))
            {
                var target = Dialects.Get(_cfg.Target.Kind);
                string targetSchema = string.IsNullOrEmpty(_cfg.Target.Schema) ? target.DefaultSchema : _cfg.Target.Schema;
                NameMap names = BuildNameMap(tables, targetSchema);
                List<ViewDef> views = DiscoverViews(src, targetSchema, target, names);
                List<RoutineDef> routines = Options.IncludeRoutines
                    ? src.Reader.ReadRoutines(src.Connection, src.Schema, _log) : new List<RoutineDef>();
                List<TriggerDef> triggers = Options.IncludeTriggers
                    ? src.Reader.ReadTriggers(src.Connection, src.Schema, _log) : new List<TriggerDef>();
                return GenerateScript(tables, views, src.Dialect, names, routines, triggers);
            }
        }

        private NameMap BuildNameMap(List<TableDef> tables, string targetSchema)
        {
            var names = new NameMap();
            foreach (TableDef t in tables)
            {
                names.Add(t.Schema, t.Name, t.TargetSchema, t.TargetName);
                foreach (ColumnDef c in t.Columns)
                {
                    if (!c.IsComputed) names.AddColumn(c.Name, c.TargetName);
                }
            }
            return names;
        }

        /// <summary>读取视图并分配目标名，再按“视图依赖视图”的顺序排好。</summary>
        private List<ViewDef> DiscoverViews(SchemaSession src, string targetSchema, Dialect target, NameMap names)
        {
            var views = new List<ViewDef>();
            if (!Options.IncludeViews) return views;
            List<ViewDef> raw = src.Reader.ReadViews(src.Connection, src.Schema, _log);
            string effectiveCase = SqlUtil.ResolveCase(Options.IdentifierCase, target);
            foreach (ViewDef v in raw)
            {
                v.TargetSchema = targetSchema;
                v.TargetName = SqlUtil.ApplyCase(v.Name, effectiveCase);
                names.Add(v.Schema, v.Name, v.TargetSchema, v.TargetName);
                views.Add(v);
            }
            views = SortViews(views);
            _log("发现 " + views.Count + " 个视图");
            return views;
        }

        /// <summary>视图可能引用其它视图，按依赖排序；有环就按原名顺序并登记问题。</summary>
        private List<ViewDef> SortViews(List<ViewDef> views)
        {
            var byName = new Dictionary<string, ViewDef>(StringComparer.OrdinalIgnoreCase);
            foreach (ViewDef v in views) byName[v.Name] = v;

            var result = new List<ViewDef>();
            var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            Action<ViewDef> visit = null;
            visit = v =>
            {
                int st = state.TryGetValue(v.Name, out int x) ? x : 0;
                if (st == 2) return;
                if (st == 1)
                {
                    _issues.Add("视图", v.QualifiedName(), IssueLevel.Manual, "视图存在循环依赖，顺序可能不对", "", "", "人工确认创建顺序");
                    return;
                }
                state[v.Name] = 1;
                foreach (string dep in FindViewDependencies(v, byName))
                {
                    if (!string.Equals(dep, v.Name, StringComparison.OrdinalIgnoreCase)) visit(byName[dep]);
                }
                state[v.Name] = 2;
                result.Add(v);
            };

            foreach (ViewDef v in views) visit(v);
            return result;
        }

        private static List<string> FindViewDependencies(ViewDef v, Dictionary<string, ViewDef> byName)
        {
            var deps = new List<string>();
            string body = v.Definition ?? "";
            foreach (var kv in byName)
            {
                if (string.Equals(kv.Key, v.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(body, "\\b" + System.Text.RegularExpressions.Regex.Escape(kv.Key) + "\\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    deps.Add(kv.Key);
                }
            }
            return deps;
        }

        private string BuildManualDrafts(List<RoutineDef> routines, List<TriggerDef> triggers, DdlGenerator gen,
                                         bool reportIssues = true)
        {
            var sb = new StringBuilder();
            sb.AppendLine("-- DBConvertDB 过程/函数/触发器改造草稿（不会自动执行）");
            sb.AppendLine("-- 目标方言：" + Dialects.Get(_cfg.Target.Kind).Display + "，生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("-- 用法：逐段评审、按目标库语法改写后执行；所有不确定点见 issues 清单。");
            sb.AppendLine();
            foreach (RoutineDef r in routines) sb.AppendLine(gen.BuildRoutineDraft(r, reportIssues));
            foreach (TriggerDef t in triggers) sb.AppendLine(gen.BuildTriggerDraft(t, reportIssues));
            return sb.ToString();
        }

        public string GenerateScript(IEnumerable<TableDef> tables, List<ViewDef> views = null,
                                     Dialect source = null, NameMap names = null,
                                     List<RoutineDef> routines = null, List<TriggerDef> triggers = null)
        {
            var target = Dialects.Get(_cfg.Target.Kind);
            string targetSchema = string.IsNullOrEmpty(_cfg.Target.Schema) ? target.DefaultSchema : _cfg.Target.Schema;
            var gen = new DdlGenerator(target, _cfg.Options, _warnings, source, _issues);
            if (names != null) gen.Names = names;
            var sb = new StringBuilder();
            sb.AppendLine("-- DBConvertDB 生成的目标库建表/对象脚本");
            sb.AppendLine("-- 目标方言：" + target.Display);
            sb.AppendLine("-- 生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();
            List<TableDef> list = tables as List<TableDef> ?? new List<TableDef>(tables);
            foreach (TableDef t in list)
            {
                sb.AppendLine("-- =============================================================");
                sb.AppendLine("-- 源表：" + t.QualifiedName + "  →  目标表：" + TargetLabel(t));
                foreach (ColumnDef c in t.Columns)
                {
                    sb.AppendLine("--   " + c.Name + " " + c.RawType + "  →  " + c.TargetName + " " + (c.TargetType ?? "<未映射>"));
                }
                sb.AppendLine("-- =============================================================");
                foreach (DdlStatement st in gen.Build(t, true))
                {
                    sb.AppendLine(st.Sql);
                }
                sb.AppendLine();
            }

            if (_cfg.Options.IncludeForeignKeys)
            {
                sb.AppendLine("-- 外键（必须在所有表建好之后执行）");
                foreach (TableDef t in list)
                {
                    foreach (DdlStatement st in gen.BuildForeignKeys(t)) sb.AppendLine(st.Sql);
                }
                sb.AppendLine();
            }

            if (routines != null && routines.Count > 0)
            {
                sb.AppendLine("-- 过程/函数（同类型库可直接执行；跨类型见 objects_manual）");
                foreach (RoutineDef r in routines)
                {
                    string reason;
                    string sql = gen.BuildRoutineExecutable(r, out reason);
                    if (sql == null)
                    {
                        sb.AppendLine("-- [需人工] " + (r.Kind == "function" ? "函数" : "过程") + "：" + r.Name + "，" + reason);
                        sb.AppendLine(gen.BuildRoutineDraft(r, false));
                        continue;
                    }
                    string drop = gen.BuildRoutineDrop(r, targetSchema);
                    if (!string.IsNullOrEmpty(drop)) sb.AppendLine(drop);
                    sb.AppendLine(sql);
                    sb.AppendLine();
                }
            }

            if (views != null && views.Count > 0)
            {
                sb.AppendLine("-- 视图（按依赖顺序生成）");
                foreach (ViewDef v in views)
                {
                    sb.AppendLine("-- 源视图：" + v.QualifiedName() + " → " + (string.IsNullOrEmpty(v.TargetSchema) ? "" : v.TargetSchema + ".") + v.TargetName);
                    sb.AppendLine(gen.BuildView(v, v.TargetSchema, v.TargetName).Sql);
                    sb.AppendLine();
                }
            }

            if (triggers != null && triggers.Count > 0)
            {
                sb.AppendLine("-- 触发器（数据搬完后执行）");
                foreach (TriggerDef tr in triggers)
                {
                    string reason;
                    string sql = gen.BuildTriggerExecutable(tr, out reason);
                    if (sql == null)
                    {
                        sb.AppendLine("-- [需人工] 触发器：" + tr.Name + "，" + reason);
                        sb.AppendLine(gen.BuildTriggerDraft(tr, false));
                        continue;
                    }
                    string targetTable = TargetTableName(names, tr);
                    string drop = gen.BuildTriggerDrop(tr, targetSchema, targetTable);
                    if (!string.IsNullOrEmpty(drop)) sb.AppendLine(drop);
                    sb.AppendLine(sql);
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        public string GenerateMappingCsv(IEnumerable<TableDef> tables)
        {
            var sb = new StringBuilder();
            sb.AppendLine("源表,源列,源类型,目标表,目标列,目标类型,可空,自增,备注");
            foreach (TableDef t in tables)
            {
                foreach (ColumnDef c in t.Columns)
                {
                    sb.Append(Csv(t.QualifiedName)).Append(',')
                      .Append(Csv(c.Name)).Append(',')
                      .Append(Csv(c.RawType)).Append(',')
                      .Append(Csv(TargetLabel(t))).Append(',')
                      .Append(Csv(c.TargetName)).Append(',')
                      .Append(Csv(c.TargetType ?? "")).Append(',')
                      .Append(c.Nullable ? "Y" : "N").Append(',')
                      .Append(c.IsIdentity ? "Y" : "N").Append(',')
                      .Append(Csv(c.MappingWarning)).AppendLine();
                }
            }
            return sb.ToString();
        }

        private static string Csv(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>只生成脚本和映射报告，不连目标库、不搬数据。</summary>
        public MigrationReport Plan()
        {
            var report = new MigrationReport { Config = _cfg };
            string dir = ReportWriter.ResolveOutDir(_cfg.Options);
            string ts = ReportWriter.Timestamp();
            string scriptPath = "";
            string csvPath = Path.Combine(dir, "mapping_" + ts + ".csv");
            string issuesPath = Path.Combine(dir, "issues_" + ts + ".csv");
            string manualPath = Path.Combine(dir, "objects_manual_" + ts + ".sql");

            using (var src = SchemaSession.Open(_cfg.Source, _log))
            {
                var target = Dialects.Get(_cfg.Target.Kind);
                string targetSchema = string.IsNullOrEmpty(_cfg.Target.Schema) ? target.DefaultSchema : _cfg.Target.Schema;
                List<TableDef> tables = src.LoadTables(_cfg.Options, _cfg.Tables, _log, _warnings, targetSchema, target);
                if (tables.Count == 0)
                {
                    report.Errors.Add("没有匹配到任何表，请检查 include/exclude 或 schema/数据库名");
                    report.FinishedAt = DateTime.Now;
                    return report;
                }

                NameMap names = BuildNameMap(tables, targetSchema);
                List<ViewDef> views = _cfg.Options.IncludeViews ? DiscoverViews(src, targetSchema, target, names) : new List<ViewDef>();
                List<RoutineDef> routines = _cfg.Options.IncludeRoutines
                    ? src.Reader.ReadRoutines(src.Connection, src.Schema, _log) : new List<RoutineDef>();
                List<TriggerDef> triggers = _cfg.Options.IncludeTriggers
                    ? src.Reader.ReadTriggers(src.Connection, src.Schema, _log) : new List<TriggerDef>();
                _log("发现 " + routines.Count + " 个过程/函数、" + triggers.Count + " 个触发器");

                var gen = new DdlGenerator(target, _cfg.Options, _warnings, src.Dialect, _issues);
                gen.Names = names;

                string script = GenerateScript(tables, views, src.Dialect, names, routines, triggers);
                scriptPath = Path.Combine(dir, "schema_" + ts + ".sql");
                File.WriteAllText(scriptPath, script, new UTF8Encoding(true));

                File.WriteAllText(csvPath, GenerateMappingCsv(tables), new UTF8Encoding(true));

                if (routines.Count + triggers.Count > 0)
                {
                    var manualRoutines = new List<RoutineDef>();
                    foreach (RoutineDef r in routines)
                    {
                        string reason;
                        if (!gen.CanExecuteRoutine(r, out reason)) manualRoutines.Add(r);
                    }
                    var manualTriggers = new List<TriggerDef>();
                    foreach (TriggerDef tr in triggers)
                    {
                        string reason;
                        if (!gen.CanExecuteTrigger(tr, out reason)) manualTriggers.Add(tr);
                    }
                    if (manualRoutines.Count + manualTriggers.Count > 0)
                    {
                        File.WriteAllText(manualPath, BuildManualDrafts(manualRoutines, manualTriggers, gen), new UTF8Encoding(true));
                        _log("需人工的过程/触发器草稿：" + manualPath);
                    }
                    else
                    {
                        manualPath = "";
                    }
                }
                else
                {
                    manualPath = "";
                }

                _issues.WriteCsv(issuesPath);
                if (_issues.Count(IssueLevel.Blocking) > 0)
                {
                    report.Errors.Add("对象问题清单中有 " + _issues.Count(IssueLevel.Blocking) + " 个阻断项，请按 issues 处理");
                }

                foreach (TableDef t in tables)
                {
                    report.Tables.Add(new TableResult
                    {
                        Source = t.QualifiedName,
                        Target = TargetLabel(t),
                        SourceRows = t.RowCountEstimate,
                        Status = "planned",
                        DdlFile = scriptPath
                    });
                }
            }

            WriteMarkdownReport(report, dir, ts, scriptPath, csvPath, issuesPath, manualPath);
            report.FinishedAt = DateTime.Now;
            _log("已生成脚本：" + scriptPath);
            _log("已生成映射报告：" + csvPath);
            _log("对象问题清单：" + issuesPath + "（阻断 " + _issues.Count(IssueLevel.Blocking) +
                 " / 需人工 " + _issues.Count(IssueLevel.Manual) +
                 " / 已改写 " + _issues.Count(IssueLevel.Rewritten) + "）");
            return report;
        }

        public MigrationReport Run(bool dryRun)
        {
            if (Options.ScriptOnly)
            {
                _log("只生成 SQL 模式：不连接目标库、不搬数据");
                return Plan();
            }

            var report = new MigrationReport { Config = _cfg };
            string dir = ReportWriter.ResolveOutDir(_cfg.Options);
            string ts = ReportWriter.Timestamp();
            var opt = Options;

            string mode = (opt.Mode ?? "both").ToLowerInvariant();
            bool doSchema = mode == "both" || mode == "schema" || dryRun;
            bool doData = mode == "both" || mode == "data";
            bool doVerify = mode == "verify" || (doData && !string.Equals(opt.VerifyMode, "none", StringComparison.OrdinalIgnoreCase));

            if (opt.Parallel > 1) _warnings.Add("并行度暂时只支持 1，已按单线程执行");

            // 续传时目标表通常已存在，建表语句要容忍“已存在”
            if (opt.Resume && doSchema) opt.CreateIfNotExists = true;

            using (var src = SchemaSession.Open(_cfg.Source, _log))
            using (var dst = SchemaSession.Open(_cfg.Target, _log))
            {
                List<TableDef> tables = src.LoadTables(opt, _cfg.Tables, _log, _warnings, dst.Schema, dst.Dialect);
                if (tables.Count == 0)
                {
                    report.Errors.Add("没有匹配到任何表，请检查 include/exclude 或 schema/数据库名");
                    report.FinishedAt = DateTime.Now;
                    return report;
                }

                var targetDialect = dst.Dialect;
                AutoDetectIdentityMode(dst, opt, targetDialect);

                NameMap names = BuildNameMap(tables, dst.Schema);
                List<ViewDef> views = doSchema && opt.IncludeViews ? DiscoverViews(src, dst.Schema, targetDialect, names) : new List<ViewDef>();
                List<RoutineDef> routines = doSchema && opt.IncludeRoutines
                    ? src.Reader.ReadRoutines(src.Connection, src.Schema, _log) : new List<RoutineDef>();
                List<TriggerDef> triggers = doSchema && opt.IncludeTriggers
                    ? src.Reader.ReadTriggers(src.Connection, src.Schema, _log) : new List<TriggerDef>();
                if (doSchema) _log("发现 " + views.Count + " 个视图、" + routines.Count + " 个过程/函数、" + triggers.Count + " 个触发器");

                var gen = new DdlGenerator(targetDialect, opt, _warnings, src.Dialect, _issues);
                gen.Names = names;
                var routineDrafts = new List<RoutineDef>();
                var triggerDrafts = new List<TriggerDef>();
                bool directAttempted = opt.DirectObjects && !dryRun;

                // MySQL 重跑时父表会被子表外键引用，DROP TABLE 会失败；迁移期间临时关闭外键检查
                bool fkChecksOff = false;
                if (doSchema && !dryRun && opt.DropExisting && targetDialect.Family == DialectFamily.MySql)
                {
                    string err = ExecuteStatement(dst.Connection,
                        new DdlStatement { Sql = "SET FOREIGN_KEY_CHECKS=0", Kind = "session" }, opt, _log);
                    fkChecksOff = string.IsNullOrEmpty(err);
                }
                var scriptBuilder = new StringBuilder();
                scriptBuilder.AppendLine("-- DBConvertDB 目标库脚本（" + targetDialect.Display + "）");
                scriptBuilder.AppendLine();

                foreach (TableDef t in tables)
                {
                    var result = new TableResult
                    {
                        Source = t.QualifiedName,
                        Target = TargetLabel(t),
                        SourceRows = t.RowCountEstimate
                    };
                    report.Tables.Add(result);
                    var sw = Stopwatch.StartNew();

                    try
                    {
                        List<DdlStatement> statements;
                        try
                        {
                            statements = gen.Build(t, true);
                        }
                        catch (InvalidOperationException ex)
                        {
                            result.Status = "skipped";
                            result.Message = ex.Message;
                            _warnings.Add(ex.Message);
                            _log("跳过表 " + t.QualifiedName + "：" + ex.Message);
                            continue;
                        }

                        foreach (DdlStatement st in statements)
                        {
                            scriptBuilder.AppendLine(st.Sql);
                            if (doSchema && !dryRun)
                            {
                                string err = ExecuteStatement(dst.Connection, st, opt, _log);
                                if (!string.IsNullOrEmpty(err))
                                {
                                    if (opt.Resume && IsAlreadyExists(err))
                                    {
                                        _warnings.Add(t.QualifiedName + " " + st.Kind + "：对象已存在，续传时跳过");
                                        continue;
                                    }
                                    if (st.Critical)
                                    {
                                        result.Status = "failed";
                                        result.Message = "建表失败：" + err;
                                        report.Errors.Add(t.QualifiedName + "：" + err);
                                        break;
                                    }
                                    _warnings.Add(t.QualifiedName + " " + st.Kind + " 失败：" + err);
                                }
                            }
                        }

                        if (result.Status == "failed") continue;

                        if (dryRun)
                        {
                            result.Status = "planned";
                            continue;
                        }

                        if (doData)
                        {
                            if (opt.TruncateTarget || opt.DropExisting)
                            {
                                string trunc = targetDialect.TruncateStatement(t.TargetSchema, t.TargetName);
                                ExecuteStatement(dst.Connection, new DdlStatement { Sql = trunc, Kind = "truncate" }, opt, _log);
                            }

                            var map = FindMap(t);
                            string where = map?.Where ?? "";
                            var pump = new DataPump(src, dst, opt, _log, _warnings);
                            long copied = pump.CopyTable(t, where, null);
                            result.RowsCopied = copied;

                            if (opt.CreateIndexes && opt.CreateIndexesAfterLoad && opt.CreateTable)
                            {
                                foreach (DdlStatement st in gen.BuildIndexes(t, t.Columns))
                                {
                                    string err = ExecuteStatement(dst.Connection, st, opt, _log);
                                    if (!string.IsNullOrEmpty(err) && !IsAlreadyExists(err))
                                        _warnings.Add(t.QualifiedName + " 索引创建失败：" + err);
                                }
                            }

                            if (doVerify && !string.Equals(opt.VerifyMode, "none", StringComparison.OrdinalIgnoreCase))
                            {
                                var verifier = new Verifier(src, dst, opt, _log);
                                VerifyOutcome outcome = verifier.Verify(t, where);
                                result.SourceRows = outcome.SourceCount;
                                result.TargetRows = outcome.TargetCount;
                                result.ChecksumSource = outcome.SourceSum;
                                result.ChecksumTarget = outcome.TargetSum;
                                result.Verified = outcome.Ok;
                                result.Message = outcome.Message;
                                result.Status = outcome.Ok ? "verified" : "failed";
                                if (!outcome.Ok) report.Errors.Add(t.QualifiedName + " 校验失败：" + outcome.Message);
                            }
                            else
                            {
                                result.Status = "ok";
                            }
                        }
                        else if (doSchema)
                        {
                            result.Status = "ok";
                        }
                        else if (mode == "verify")
                        {
                            var map = FindMap(t);
                            var verifier = new Verifier(src, dst, opt, _log);
                            VerifyOutcome outcome = verifier.Verify(t, map?.Where ?? "");
                            result.SourceRows = outcome.SourceCount;
                            result.TargetRows = outcome.TargetCount;
                            result.ChecksumSource = outcome.SourceSum;
                            result.ChecksumTarget = outcome.TargetSum;
                            result.Verified = outcome.Ok;
                            result.Message = outcome.Message;
                            result.Status = outcome.Ok ? "verified" : "failed";
                            if (!outcome.Ok) report.Errors.Add(t.QualifiedName + " 校验失败：" + outcome.Message);
                        }
                        else
                        {
                            result.Status = "skipped";
                            result.Message = "未执行任何动作，请检查 options.mode";
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Status = "failed";
                        result.Message = ex.Message;
                        report.Errors.Add(t.QualifiedName + "：" + ex.Message);
                        _log("表 " + t.QualifiedName + " 失败：" + ex.Message);
                        if (!opt.ContinueOnError) throw;
                    }
                    finally
                    {
                        sw.Stop();
                        result.Elapsed = sw.Elapsed;
                        _log("[" + result.Status + "] " + t.QualifiedName + " → " + result.Target +
                             " 行数 " + result.RowsCopied + " 用时 " + sw.Elapsed.ToString(@"hh\:mm\:ss") +
                             (string.IsNullOrEmpty(result.Message) ? "" : "：" + result.Message));
                    }
                }

                // ---- SQLite 目标：外键是内联的，搬数期间关闭强制，这里复核数据是否真的满足外键 ----
                if (doSchema && !dryRun && targetDialect.Family == DialectFamily.Sqlite && opt.IncludeForeignKeys)
                {
                    try
                    {
                        int bad = 0;
                        using (var cmd = dst.Connection.CreateCommand())
                        {
                            cmd.CommandText = "PRAGMA foreign_key_check";
                            using (var rd = cmd.ExecuteReader()) while (rd.Read()) bad++;
                        }
                        if (bad > 0)
                        {
                            _issues.Add("外键", "数据完整性", IssueLevel.Blocking,
                                "外键检查发现 " + bad + " 行数据不满足引用关系", "", "",
                                "补齐父表数据或清理子表孤儿行后重新校验");
                            _warnings.Add("SQLite 外键检查发现 " + bad + " 行不一致");
                        }
                        else
                        {
                            _log("SQLite 外键数据检查通过");
                        }
                    }
                    catch (Exception ex)
                    {
                        _warnings.Add("SQLite 外键检查跳过：" + ex.Message);
                    }
                }

                // ---- 外键：所有表建完之后统一加 ----
                if (doSchema && opt.IncludeForeignKeys)
                {
                    _log("开始创建外键（必须在所有表建好之后）");
                    foreach (TableDef t in tables)
                    {
                        foreach (DdlStatement st in gen.BuildForeignKeys(t))
                        {
                            scriptBuilder.AppendLine(st.Sql);
                            if (dryRun) continue;
                            string err = ExecuteStatement(dst.Connection, st, opt, _log);
                            if (!string.IsNullOrEmpty(err) && !IsAlreadyExists(err))
                            {
                                _issues.Add("外键", t.QualifiedName + "." + st.Kind, IssueLevel.Blocking,
                                    "外键创建失败：" + err, st.Sql, "", "确认列类型/引用表是否一致后手工补建");
                                _warnings.Add(t.QualifiedName + " 外键创建失败：" + err);
                                if (opt.StrictObjects) throw new InvalidOperationException("外键创建失败：" + err);
                            }
                        }
                    }
                    scriptBuilder.AppendLine();
                }

                // ---- 过程/函数：同类型库且拿到完整 DDL 时直接创建；否则留草稿 ----
                if (doSchema && routines.Count > 0)
                {
                    _log("开始处理过程/函数，共 " + routines.Count + " 个" +
                         (directAttempted && src.Dialect.Family == targetDialect.Family
                             ? "（同类型库直接创建）" : "（生成改造草稿）"));
                    foreach (RoutineDef r in routines)
                    {
                        string reason = "";
                        string sql = directAttempted ? gen.BuildRoutineExecutable(r, out reason) : null;
                        if (sql == null)
                        {
                            routineDrafts.Add(r);
                            if (directAttempted)
                            {
                                _issues.Add(r.Kind == "function" ? "函数" : "存储过程", r.Name, IssueLevel.Manual,
                                    "未直接执行：" + reason, r.Definition, "", "按 objects_manual 草稿在目标库编译");
                            }
                            continue;
                        }

                        scriptBuilder.AppendLine("-- 过程/函数：" + r.Name + "（直接创建）");
                        scriptBuilder.AppendLine(sql);
                        scriptBuilder.AppendLine();

                        string drop = gen.BuildRoutineDrop(r, dst.Schema);
                        if (!string.IsNullOrEmpty(drop))
                            ExecuteStatement(dst.Connection, new DdlStatement { Sql = drop, Kind = "drop-routine" }, opt, _log);

                        string err = ExecuteStatement(dst.Connection,
                            new DdlStatement { Sql = sql, Kind = "routine" }, opt, _log);
                        if (!string.IsNullOrEmpty(err))
                        {
                            routineDrafts.Add(r);
                            _issues.Add(r.Kind == "function" ? "函数" : "存储过程", r.Name, IssueLevel.Blocking,
                                "直接创建失败：" + err, sql, "", "按 objects_manual 草稿在目标库编译");
                            _warnings.Add((r.Kind == "function" ? "函数 " : "过程 ") + r.Name + " 直接创建失败：" + err);
                            if (opt.StrictObjects) throw new InvalidOperationException("过程/函数创建失败：" + err);
                        }
                        else
                        {
                            _log("[routine] " + r.Name + " 已直接创建");
                        }
                    }
                }

                // ---- 视图：按依赖顺序创建 ----
                if (doSchema && views.Count > 0)
                {
                    _log("开始创建视图，共 " + views.Count + " 个");
                    foreach (ViewDef v in views)
                    {
                        DdlStatement st;
                        try
                        {
                            st = gen.BuildView(v, v.TargetSchema, v.TargetName);
                        }
                        catch (Exception ex)
                        {
                            _issues.Add("视图", v.QualifiedName(), IssueLevel.Blocking, "视图 DDL 生成失败：" + ex.Message,
                                v.Definition, "", "人工检查视图定义");
                            continue;
                        }
                        scriptBuilder.AppendLine("-- 视图：" + v.QualifiedName() + " → " + v.TargetName);
                        scriptBuilder.AppendLine(st.Sql);
                        scriptBuilder.AppendLine();
                        if (dryRun) continue;
                        string err = ExecuteStatement(dst.Connection, st, opt, _log);
                        if (!string.IsNullOrEmpty(err))
                        {
                            _issues.Add("视图", v.QualifiedName(), IssueLevel.Blocking, "视图创建失败：" + err,
                                v.Definition, st.Sql, "按错误提示改写视图，或先建依赖对象");
                            _warnings.Add("视图 " + v.QualifiedName() + " 创建失败：" + err);
                            if (opt.StrictObjects) throw new InvalidOperationException("视图创建失败：" + err);
                        }
                        else
                        {
                            // 建成功不等于可用：SQLite 允许建引用不存在表的视图，这里探一次能否查询
                            string probe = "SELECT * FROM " + targetDialect.Qualify(v.TargetSchema, v.TargetName) + " WHERE 1=0";
                            string probeErr = ExecuteStatement(dst.Connection, new DdlStatement { Sql = probe, Kind = "view-probe" }, opt, _log);
                            if (!string.IsNullOrEmpty(probeErr))
                            {
                                _issues.Add("视图", v.QualifiedName(), IssueLevel.Blocking,
                                    "视图建好后不可查询：" + probeErr, st.Sql, "", "确认视图依赖的表/列/函数是否都已迁移");
                                _warnings.Add("视图 " + v.QualifiedName() + " 建好后不可查询：" + probeErr);
                                if (opt.StrictObjects) throw new InvalidOperationException("视图不可查询：" + probeErr);
                            }
                            else
                            {
                                _log("[view] " + v.QualifiedName() + " → " + v.TargetName + " 已创建");
                            }
                        }
                    }
                }

                // ---- 触发器：必须等数据搬完之后创建，避免搬数期间触发业务规则 ----
                if (doSchema && triggers.Count > 0)
                {
                    _log("开始处理触发器，共 " + triggers.Count + " 个" +
                         (directAttempted && src.Dialect.Family == targetDialect.Family
                             ? "（数据已搬完，直接创建）" : "（生成改造草稿）"));
                    foreach (TriggerDef tr in triggers)
                    {
                        string reason = "";
                        string sql = directAttempted ? gen.BuildTriggerExecutable(tr, out reason) : null;
                        if (sql == null)
                        {
                            triggerDrafts.Add(tr);
                            if (directAttempted)
                            {
                                _issues.Add("触发器", tr.Name, IssueLevel.Manual,
                                    "未直接执行：" + reason, tr.Definition, "", "按 objects_manual 草稿在目标库编译");
                            }
                            continue;
                        }

                        scriptBuilder.AppendLine("-- 触发器：" + tr.Name + "（直接创建）");
                        scriptBuilder.AppendLine(sql);
                        scriptBuilder.AppendLine();

                        string targetTable = TargetTableName(names, tr);
                        string drop = gen.BuildTriggerDrop(tr, dst.Schema, targetTable);
                        if (!string.IsNullOrEmpty(drop))
                            ExecuteStatement(dst.Connection, new DdlStatement { Sql = drop, Kind = "drop-trigger" }, opt, _log);

                        string err = ExecuteStatement(dst.Connection,
                            new DdlStatement { Sql = sql, Kind = "trigger" }, opt, _log);
                        if (!string.IsNullOrEmpty(err))
                        {
                            triggerDrafts.Add(tr);
                            _issues.Add("触发器", tr.Name, IssueLevel.Blocking,
                                "直接创建失败：" + err, sql, "", "按 objects_manual 草稿在目标库编译");
                            _warnings.Add("触发器 " + tr.Name + " 直接创建失败：" + err);
                            if (opt.StrictObjects) throw new InvalidOperationException("触发器创建失败：" + err);
                        }
                        else
                        {
                            _log("[trigger] " + tr.Name + " 已直接创建");
                        }
                    }
                }

                string manualPath = Path.Combine(dir, "objects_manual_" + ts + ".sql");
                if (doSchema && (routineDrafts.Count + triggerDrafts.Count) > 0)
                {
                    File.WriteAllText(manualPath, BuildManualDrafts(routineDrafts, triggerDrafts, gen, !directAttempted), new UTF8Encoding(true));
                    _log("未直接创建的对象草稿：" + manualPath);
                }
                else
                {
                    manualPath = "";
                }

                string scriptPath = "";
                if (doSchema || dryRun)
                {
                    scriptPath = Path.Combine(dir, "schema_" + ts + ".sql");
                    File.WriteAllText(scriptPath, scriptBuilder.ToString(), new UTF8Encoding(true));
                    foreach (var r in report.Tables) if (string.IsNullOrEmpty(r.DdlFile)) r.DdlFile = scriptPath;
                }
                string csvPath = Path.Combine(dir, "mapping_" + ts + ".csv");
                File.WriteAllText(csvPath, GenerateMappingCsv(tables), new UTF8Encoding(true));
                string issuesPath = Path.Combine(dir, "issues_" + ts + ".csv");
                _issues.WriteCsv(issuesPath);
                if (_issues.Count(IssueLevel.Blocking) > 0)
                {
                    report.Errors.Add("对象问题清单中有 " + _issues.Count(IssueLevel.Blocking) + " 个阻断项（视图/外键等未创建），请按 issues 处理");
                }
                _log("对象问题清单：" + issuesPath + "（阻断 " + _issues.Count(IssueLevel.Blocking) +
                     " / 需人工 " + _issues.Count(IssueLevel.Manual) +
                     " / 已改写 " + _issues.Count(IssueLevel.Rewritten) +
                     " / 已跳过 " + _issues.Count(IssueLevel.Skipped) + "）");

                if (fkChecksOff)
                    ExecuteStatement(dst.Connection,
                        new DdlStatement { Sql = "SET FOREIGN_KEY_CHECKS=1", Kind = "session" }, opt, _log);

                report.FinishedAt = DateTime.Now;
                WriteMarkdownReport(report, dir, ts, scriptPath, csvPath, issuesPath, manualPath);
            }

            return report;
        }

        /// <summary>Oracle 12c 以下没有 IDENTITY，自动改成序列 + 触发器；达梦不需要。</summary>
        private void AutoDetectIdentityMode(SchemaSession dst, OptionsConfig opt, Dialect dialect)
        {
            if (!string.Equals(opt.IdentityMode, "auto", StringComparison.OrdinalIgnoreCase)) return;
            if (dialect.Key != "oracle") return;
            try
            {
                Version v = dst.Connection.ServerVersion == null ? null : new Version(ParseVersion(dst.Connection.ServerVersion));
                if (v != null && v.Major < 12)
                {
                    opt.IdentityMode = "sequence";
                    _warnings.Add("目标 Oracle " + v + " 不支持 IDENTITY，已自动改用序列 + 触发器");
                    _log("目标 Oracle 版本 " + v + " 较低，自增列改用序列 + 触发器");
                }
            }
            catch
            {
                // 版本识别失败就保持默认
            }
        }

        private static string ParseVersion(string serverVersion)
        {
            var sb = new StringBuilder();
            foreach (char ch in serverVersion)
            {
                if (char.IsDigit(ch) || ch == '.') sb.Append(ch);
                else if (sb.Length > 0) break;
            }
            string s = sb.ToString().TrimEnd('.');
            if (s.Count(c => c == '.') < 1) s += ".0";
            if (s.Count(c => c == '.') < 2) s += ".0";
            return s;
        }

        private static string TargetLabel(TableDef t)
        {
            return string.IsNullOrEmpty(t.TargetSchema) ? t.TargetName : t.TargetSchema + "." + t.TargetName;
        }

        private static string TargetTableName(NameMap names, TriggerDef tr)
        {
            string mapped = names == null ? null : names.Resolve(tr.Schema, tr.Table);
            if (string.IsNullOrEmpty(mapped)) return tr.Table;
            int dot = mapped.LastIndexOf('.');
            return dot >= 0 ? mapped.Substring(dot + 1) : mapped;
        }

        private TableMapConfig FindMap(TableDef t)
        {
            foreach (TableMapConfig m in _cfg.Tables)
            {
                SqlUtil.SplitQualified(m.Source, out string ms, out string mt);
                if (!string.Equals(mt, t.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(ms) && !string.Equals(ms, t.Schema, StringComparison.OrdinalIgnoreCase)) continue;
                return m;
            }
            return null;
        }

        private static string ExecuteStatement(DbConnection conn, DdlStatement st, OptionsConfig opt, Action<string> log)
        {
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = st.Sql;
                    if (opt.CommandTimeout > 0) cmd.CommandTimeout = opt.CommandTimeout;
                    cmd.ExecuteNonQuery();
                }
                return null;
            }
            catch (Exception ex)
            {
                string msg = ex.Message.Replace("\r", " ").Replace("\n", " ");
                if (st.Kind == "drop-table" && IsMissingObject(msg))
                {
                    log?.Invoke("info: 目标表原本不存在，跳过 DROP");
                    return null;
                }
                if ((st.Kind == "index" || st.Kind == "create-index" || st.Kind == "comment") && IsAlreadyExists(msg))
                {
                    // 重跑同一份配置时索引/注释已存在，属于正常情况
                    return null;
                }
                log?.Invoke("warn: " + st.Kind + " 执行失败：" + msg);
                return msg;
            }
        }

        private static bool IsMissingObject(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            return message.IndexOf("no such table", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("unknown table", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("ORA-00942", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("无效的表", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("表或视图不存在", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsAlreadyExists(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            return message.IndexOf("already exists", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("ORA-00955", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("已存在", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("重名", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void WriteMarkdownReport(MigrationReport report, string dir, string ts, string scriptPath,
                                         string csvPath, string issuesPath = "", string manualPath = "")
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 数据库迁移报告");
            sb.AppendLine();
            sb.AppendLine("- 源库：" + Dialects.Get(_cfg.Source.Kind).Display + "（" + _cfg.Source.Kind + "）");
            sb.AppendLine("- 目标库：" + Dialects.Get(_cfg.Target.Kind).Display + "（" + _cfg.Target.Kind + "）");
            sb.AppendLine("- 开始：" + report.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("- 结束：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("- 总行数：" + report.TotalRowsCopied.ToString("N0", CultureInfo.InvariantCulture));
            sb.AppendLine("- 结论：" + (report.Success ? "成功" : "存在失败项"));
            if (!string.IsNullOrEmpty(scriptPath)) sb.AppendLine("- 建表脚本：" + scriptPath);
            sb.AppendLine("- 类型映射：" + csvPath);
            if (!string.IsNullOrEmpty(issuesPath)) sb.AppendLine("- 对象问题清单：" + issuesPath);
            if (!string.IsNullOrEmpty(manualPath) && File.Exists(manualPath)) sb.AppendLine("- 过程/触发器草稿：" + manualPath);
            sb.AppendLine();
            sb.AppendLine("| 源表 | 目标表 | 源行数 | 已写入 | 校验 | 状态 | 说明 |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (TableResult t in report.Tables)
            {
                sb.AppendLine("| " + t.Source + " | " + t.Target + " | " + (t.SourceRows < 0 ? "?" : t.SourceRows.ToString()) +
                              " | " + t.RowsCopied + " | " + (t.Verified ? "通过" : (string.IsNullOrEmpty(t.ChecksumSource) ? "-" : "不一致")) +
                              " | " + t.Status + " | " + (t.Message ?? "").Replace("|", "/") + " |");
            }
            sb.AppendLine();
            if (_warnings.Count > 0)
            {
                sb.AppendLine("## 警告");
                sb.AppendLine();
                var seen = new HashSet<string>();
                foreach (string w in _warnings)
                {
                    if (seen.Add(w)) sb.AppendLine("- " + w);
                }
                sb.AppendLine();
            }
            if (report.Errors.Count > 0)
            {
                sb.AppendLine("## 错误");
                sb.AppendLine();
                foreach (string e in report.Errors) sb.AppendLine("- " + e);
                sb.AppendLine();
            }
            _issues.AppendMarkdown(sb);
            string path = Path.Combine(dir, "report_" + ts + ".md");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            _log("迁移报告：" + path);
        }
    }
}




