using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Text;
using System.Text.Json;
using DbConvert;
using DbConvert.Providers;

internal static class Program
{
    private static readonly Dictionary<string, string> Args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> Flags = new List<string>();
    private static StreamWriter _logFile;

    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch { }

        string command = "";
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith("--"))
            {
                string key = a.Substring(2);
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    Args[key] = args[++i];
                }
                else
                {
                    Flags.Add(key);
                    Args[key] = "true";
                }
            }
            else if (a.StartsWith("-") && a.Length == 2)
            {
                string key = a.Substring(1);
                if (i + 1 < args.Length && !args[i + 1].StartsWith("-"))
                {
                    Args[key] = args[++i];
                }
                else
                {
                    Flags.Add(key);
                    Args[key] = "true";
                }
            }
            else if (string.IsNullOrEmpty(command))
            {
                command = a.ToLowerInvariant();
            }
        }

        BuiltinProviders.Register();

        try
        {
            string logPath = Get("log-file");
            if (string.IsNullOrEmpty(logPath))
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(dir);
                logPath = Path.Combine(dir, "dbconvert_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
            }
            else
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(logPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            _logFile = new StreamWriter(logPath, true, Encoding.UTF8) { AutoFlush = true };

            switch (command)
            {
                case "":
                case "help":
                case "-h":
                    PrintHelp();
                    return 0;
                case "providers":
                    return ListProviders();
                case "init":
                case "init-config":
                    return InitConfig();
                case "test":
                    return TestConnection();
                case "inspect":
                    return Inspect();
                case "query":
                    return Query();
                case "plan":
                    return Plan();
                case "script":
                    return Script();
                case "migrate":
                    return Migrate(false);
                case "verify":
                    return Migrate(true);
                default:
                    Console.Error.WriteLine("未知命令：" + command);
                    PrintHelp();
                    return 2;
            }
        }
        catch (Exception ex)
        {
            WriteLine("错误：" + ex.Message);
            if (HasFlag("--verbose") || HasFlag("-v")) WriteLine(ex.ToString());
            else WriteLine("（加 --verbose 查看调用栈）");
            return 1;
        }
        finally
        {
            try { _logFile?.Dispose(); } catch { }
        }
    }

    // ------------------------------------------------------------------
    // 命令实现
    // ------------------------------------------------------------------

    private static int ListProviders()
    {
        WriteLine("数据库类型          方言            默认驱动                     状态    说明");
        WriteLine("---------------------------------------------------------------------------------------------");
        foreach (string kind in Dialects.Kinds)
        {
            Dialect d = Dialects.Get(kind);
            string invariant = ProviderHost.DefaultInvariant(kind);
            bool ok;
            string note = "";
            try
            {
                DbProviderFactory f = DbProviderFactories.GetFactory(invariant);
                ok = f != null;
            }
            catch
            {
                ok = false;
                note = "需要把驱动 DLL 放到程序目录/providers，或用 providerAssembly 指定";
            }
            if (string.Equals(kind, "kingbase", StringComparison.OrdinalIgnoreCase))
            {
                note = "建议用 Kdbndp.dll；未放置时 Npgsql 只能用于连通性试探";
            }
            WriteLine(string.Format("{0,-18} {1,-14} {2,-28} {3,-8} {4}", kind, d.Display, invariant, ok ? "可用" : "缺失", note));
        }
        WriteLine("");
        WriteLine("提示：达梦用 DmProvider.dll，人大金仓用 Kdbndp.dll；把 DLL 丢到程序目录即可被自动发现。");
        return 0;
    }

    private static int InitConfig()
    {
        string path = Get("out") ?? Get("config") ?? "migration.json";
        string sk = Get("source-kind") ?? Get("sk") ?? "mysql";
        string tk = Get("target-kind") ?? Get("tk") ?? "dm";
        var cfg = ConfigLoader.Sample(sk, tk);
        ConfigLoader.Save(cfg, path);
        WriteLine("已生成配置模板：" + Path.GetFullPath(path));
        WriteLine("请填写 source/target 的 host、port、database、user、password，然后执行：");
        WriteLine("  DbConvert.Cli test --config " + path + " --side source");
        WriteLine("  DbConvert.Cli inspect --config " + path + " --side source");
        WriteLine("  DbConvert.Cli plan --config " + path);
        WriteLine("  DbConvert.Cli migrate --config " + path);
        return 0;
    }

    private static int TestConnection()
    {
        EndpointConfig ep = BuildEndpointFromArgs();
        using (var session = SchemaSession.Open(ep, WriteLine))
        {
            WriteLine("连接成功。");
            WriteLine("  类型：" + session.Dialect.Display + "（" + ep.Kind + "）");
            WriteLine("  服务端：" + ProviderHost.ServerInfo(session.Connection));
            WriteLine("  默认 schema：" + session.Schema);
            var tables = session.LoadTables(new OptionsConfig { Include = "*" }, null, WriteLine, null);
            WriteLine("  表数量：" + tables.Count);
        }
        return 0;
    }

    private static int Inspect()
    {
        MigrationConfig cfg = LoadConfig();
        var ep = Side(cfg);
        using (var session = SchemaSession.Open(ep, WriteLine))
        {
            var opt = cfg.Options;
            string only = Get("only");
            if (!string.IsNullOrEmpty(only)) opt.Include = only;
            var warnings = new List<string>();
            var tables = session.LoadTables(opt, cfg.Tables, WriteLine, warnings);
            foreach (string w in warnings) WriteLine("warn: " + w);

            string tableFilter = Get("table");
            if (!string.IsNullOrEmpty(tableFilter))
            {
                foreach (TableDef t in tables)
                {
                    if (!t.Name.Equals(tableFilter, StringComparison.OrdinalIgnoreCase) &&
                        !t.QualifiedName.Equals(tableFilter, StringComparison.OrdinalIgnoreCase)) continue;
                    WriteLine("");
                    WriteLine("表 " + t.QualifiedName + "：" + (t.Comment ?? ""));
                    WriteLine(string.Format("{0,-4} {1,-28} {2,-22} {3,-10} {4,-8} {5}", "#", "列名", "源类型", "长度/精度", "可空", "自增"));
                    foreach (ColumnDef c in t.Columns)
                    {
                        string size = c.Length.HasValue ? c.Length.Value.ToString() : (c.Precision.HasValue ? c.Precision + "," + c.Scale : "");
                        WriteLine(string.Format("{0,-4} {1,-28} {2,-22} {3,-10} {4,-8} {5}", c.Ordinal, c.Name, c.RawType, size,
                            c.Nullable ? "Y" : "N", c.IsIdentity ? "Y" : "N"));
                    }
                    if (t.Indexes.Count > 0)
                    {
                        WriteLine("索引：");
                        foreach (IndexDef ix in t.Indexes) WriteLine("  " + (ix.IsPrimary ? "[PK] " : "") + (ix.IsUnique ? "UNIQUE " : "") + ix);
                    }
                }
                return 0;
            }

            WriteLine(string.Format("{0,-40} {1,-8} {2,-12} {3}", "表", "列数", "行数估算", "备注"));
            foreach (TableDef t in tables)
            {
                WriteLine(string.Format("{0,-40} {1,-8} {2,-12} {3}", t.QualifiedName, t.Columns.Count, t.RowCountEstimate, t.Comment));
            }
            WriteLine("总计 " + tables.Count + " 张表。加 --table <表名> 查看列明细，--json <文件> 导出结构。");

            string jsonPath = Get("json");
            if (!string.IsNullOrEmpty(jsonPath))
            {
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(tables, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }), new UTF8Encoding(true));
                WriteLine("结构已导出：" + Path.GetFullPath(jsonPath));
            }
        }
        return 0;
    }

    private static int Plan()
    {
        MigrationConfig cfg = LoadConfig();
        ApplyOverrides(cfg);
        var engine = new MigrationEngine(cfg, WriteLine);
        var report = engine.Plan();
        PrintReport(report);
        return report.Success ? 0 : 1;
    }

    /// <summary>直接执行一条 SQL，排查结构与字符集问题时很方便。</summary>
    private static int Query()
    {
        string sql = Get("sql");
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("缺少 --sql 参数");
        EndpointConfig ep = BuildEndpointFromArgs();
        using (var session = SchemaSession.Open(ep, WriteLine))
        using (var cmd = session.Connection.CreateCommand())
        {
            cmd.CommandText = sql;
            cmd.CommandTimeout = 60;
            using (var rd = cmd.ExecuteReader())
            {
                int rows = 0;
                while (rd.Read())
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < rd.FieldCount; i++)
                    {
                        if (i > 0) sb.Append(" | ");
                        sb.Append(rd.GetName(i)).Append('=');
                        sb.Append(rd[i] == DBNull.Value ? "NULL" : Convert.ToString(rd[i]));
                    }
                    WriteLine(sb.ToString());
                    if (++rows >= 200) break;
                }
                WriteLine("行数：" + rows);
            }
        }
        return 0;
    }

    private static int Script()
    {
        MigrationConfig cfg = LoadConfig();
        string onlyScript = Get("only");
        if (!string.IsNullOrEmpty(onlyScript)) cfg.Options.Include = onlyScript;
        string excludeScript = Get("exclude");
        if (!string.IsNullOrEmpty(excludeScript)) cfg.Options.Exclude = excludeScript;
        var engine = new MigrationEngine(cfg, WriteLine);
        var tables = engine.DiscoverTables();
        string script = engine.GenerateFullScript(tables);
        string outPath = Get("out");
        if (!string.IsNullOrEmpty(outPath))
        {
            File.WriteAllText(outPath, script, new UTF8Encoding(true));
            WriteLine("脚本已写入 " + Path.GetFullPath(outPath));
        }
        else
        {
            Console.WriteLine(script);
        }
        return 0;
    }

    private static int Migrate(bool verifyOnly)
    {
        MigrationConfig cfg = LoadConfig();
        ApplyOverrides(cfg);

        if (verifyOnly) cfg.Options.Mode = "verify";
        bool dryRun = verifyOnly ? false : HasFlag("dry-run");

        var engine = new MigrationEngine(cfg, WriteLine);
        MigrationReport report = engine.Run(dryRun);
        PrintReport(report);
        return report.Success ? 0 : 1;
    }

    private static void ApplyOverrides(MigrationConfig cfg)
    {
        string only = Get("only");
        if (!string.IsNullOrEmpty(only)) cfg.Options.Include = only;
        string exclude = Get("exclude");
        if (!string.IsNullOrEmpty(exclude)) cfg.Options.Exclude = exclude;
        string mode = Get("mode");
        if (!string.IsNullOrEmpty(mode)) cfg.Options.Mode = mode;
        string verifyMode = Get("verify-mode");
        if (!string.IsNullOrEmpty(verifyMode)) cfg.Options.VerifyMode = verifyMode;
        string batch = Get("batch");
        if (!string.IsNullOrEmpty(batch)) cfg.Options.BatchSize = int.Parse(batch);
        string outDir = Get("out");
        if (!string.IsNullOrEmpty(outDir)) cfg.Options.OutDir = outDir;
        if (HasFlag("drop")) cfg.Options.DropExisting = true;
        if (HasFlag("truncate")) cfg.Options.TruncateTarget = true;
        if (HasFlag("resume")) cfg.Options.Resume = true;
        if (HasFlag("continue-on-error")) cfg.Options.ContinueOnError = true;
        if (HasFlag("no-index")) cfg.Options.CreateIndexes = false;
        if (HasFlag("no-create")) cfg.Options.CreateTable = false;
        if (HasFlag("if-not-exists")) cfg.Options.CreateIfNotExists = true;
        if (HasFlag("script-only")) cfg.Options.ScriptOnly = true;
        string identityMode = Get("identity-mode");
        if (!string.IsNullOrEmpty(identityMode)) cfg.Options.IdentityMode = identityMode;
    }

    private static void PrintReport(MigrationReport report)
    {
        WriteLine("");
        WriteLine("================ 迁移结果 ================");
        foreach (TableResult t in report.Tables)
        {
            WriteLine("[" + t.Status + "] " + t.Source + " → " + t.Target +
                      "  行数 " + t.RowsCopied + "  " + (t.Message ?? ""));
        }
        WriteLine("总行数：" + report.TotalRowsCopied + "，表数：" + report.Tables.Count +
                  "，耗时：" + (report.FinishedAt - report.StartedAt).ToString(@"hh\:mm\:ss"));
        if (report.Errors.Count > 0)
        {
            WriteLine("错误：");
            foreach (string e in report.Errors) WriteLine("  - " + e);
        }
        WriteLine("结论：" + (report.Success ? "成功" : "有失败项，请查看报告"));
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    private static MigrationConfig LoadConfig()
    {
        string path = Get("config");
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("缺少 --config 参数");
        return ConfigLoader.Load(path);
    }

    private static EndpointConfig Side(MigrationConfig cfg)
    {
        string side = (Get("side") ?? "source").ToLowerInvariant();
        if (side != "source" && side != "target") throw new ArgumentException("--side 只能是 source 或 target");
        return side == "source" ? cfg.Source : cfg.Target;
    }

    private static EndpointConfig BuildEndpointFromArgs()
    {
        string configPath = Get("config");
        if (!string.IsNullOrEmpty(configPath))
        {
            MigrationConfig cfg = ConfigLoader.Load(configPath);
            return Side(cfg);
        }

        var ep = new EndpointConfig
        {
            Kind = Get("kind") ?? "mysql",
            ConnectionString = Get("conn") ?? "",
            Host = Get("host") ?? "",
            Database = Get("db") ?? Get("database") ?? "",
            User = Get("user") ?? "",
            Password = Get("password") ?? Get("pwd") ?? "",
            Schema = Get("schema") ?? ""
        };
        int port;
        if (int.TryParse(Get("port"), out port)) ep.Port = port;
        else ep.Port = ConnectionStrings.DefaultPort(ep.Kind);
        return ep;
    }

    private static string Get(string key)
    {
        return Args.TryGetValue(key, out string v) ? v : null;
    }

    private static bool HasFlag(string key)
    {
        return Args.ContainsKey(key) && !"false".Equals(Args[key], StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteLine(string s)
    {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s;
        Console.WriteLine(line);
        try { _logFile?.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + s); } catch { }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("DBConvertDB — 异种数据库 / 国产数据库迁移工具");
        Console.WriteLine();
        Console.WriteLine("用法：DbConvert.Cli <命令> [参数]");
        Console.WriteLine();
        Console.WriteLine("命令：");
        Console.WriteLine("  providers                     列出支持的数据库类型与驱动状态");
        Console.WriteLine("  init                          生成配置模板（--out migration.json --source-kind mysql --target-kind dm）");
        Console.WriteLine("  test                          测试连接（--config x.json --side source，或 --kind dm --host ... --user ...）");
        Console.WriteLine("  inspect                       查看表结构（--config x.json --side source [--table 表名] [--json out.json]）");
        Console.WriteLine("  script                        只输出建表脚本（--config x.json [--out schema.sql]）");
        Console.WriteLine("  plan                          生成建表脚本 + 类型映射 CSV + 报告，不碰目标库");
        Console.WriteLine("  migrate                       建表 + 搬数据 + 校验（--config x.json [--dry-run] [--drop] [--truncate] [--resume] [--script-only]）");
        Console.WriteLine("  verify                        只做行数/校验和核对");
        Console.WriteLine();
        Console.WriteLine("常用参数：");
        Console.WriteLine("  --config <file>   迁移配置文件（JSON）");
        Console.WriteLine("  --only <pattern>  只迁移匹配的表，支持 * 通配，逗号分隔");
        Console.WriteLine("  --exclude <pattern> 排除表");
        Console.WriteLine("  --mode both|schema|data|verify");
        Console.WriteLine("  --verify-mode checksum|count|none");
        Console.WriteLine("  --batch <n>       每批行数（默认 2000）");
        Console.WriteLine("  --out <dir|file>  输出目录/文件");
        Console.WriteLine("  --identity-mode auto|sequence|none   （Oracle 11g 请用 sequence）");
        Console.WriteLine("  --script-only     只生成 SQL/报告，不连目标库、不搬数据");
        Console.WriteLine("  --log-file <path> 指定日志文件");
        Console.WriteLine();
        Console.WriteLine("示例：");
        Console.WriteLine("  DbConvert.Cli init --out mysql2dm.json --source-kind mysql --target-kind dm");
        Console.WriteLine("  DbConvert.Cli test --kind dm --host 127.0.0.1 --port 5236 --user SYSDBA --password xxx --db TEST");
        Console.WriteLine("  DbConvert.Cli inspect --config mysql2dm.json --side source");
        Console.WriteLine("  DbConvert.Cli migrate --config mysql2dm.json --drop");
    }
}
