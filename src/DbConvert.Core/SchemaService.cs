using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using DbConvert.Schema;

namespace DbConvert
{
    /// <summary>一次连接（源或目标）的上下文。</summary>
    public class SchemaSession : IDisposable
    {
        public EndpointConfig Endpoint { get; private set; }
        public DbConnection Connection { get; private set; }
        public string Schema { get; private set; }
        public Dialect Dialect { get; private set; }
        public ISchemaReader Reader { get; private set; }

        public static SchemaSession Open(EndpointConfig ep, Action<string> log)
        {
            if (ep == null) throw new ArgumentNullException(nameof(ep));
            if (string.IsNullOrWhiteSpace(ep.ConnectionString) && string.IsNullOrWhiteSpace(ep.Host) &&
                string.IsNullOrWhiteSpace(ep.Database))
            {
                throw new ArgumentException("连接信息为空：请填 connectionString，或填 host/database/user/password");
            }

            var dialect = Dialects.Get(ep.Kind);
            DbConnection conn = ProviderHost.Open(ep);
            if (dialect.Family == DialectFamily.Sqlite) TuneSqlite(conn, log);
            string schema = "";
            try
            {
                schema = SchemaReaderFactory.Create(ep.Kind).ResolveSchema(conn, ep.Schema);
            }
            catch (Exception ex)
            {
                log?.Invoke("未能自动识别默认 schema：" + ex.Message);
            }
            if (string.IsNullOrEmpty(schema)) schema = dialect.DefaultSchema;

            log?.Invoke("已连接 " + dialect.Display + "：" + ProviderHost.ServerInfo(conn) + "，schema = " + schema);

            return new SchemaSession
            {
                Endpoint = ep,
                Connection = conn,
                Schema = schema,
                Dialect = dialect,
                Reader = SchemaReaderFactory.Create(ep.Kind)
            };
        }

        /// <summary>SQLite 默认的逐批同步写在机械盘上很慢，迁库场景放宽同步级别。</summary>
        private static void TuneSqlite(DbConnection conn, Action<string> log)
        {
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA busy_timeout=10000; PRAGMA journal_mode=WAL; " +
                                      "PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA cache_size=-64000; " +
                                      // 迁库时先关外键强制：SQLite 的 FK 只能建表内联，父表先建/先删会互相卡住；搬完用 foreign_key_check 复核
                                      "PRAGMA foreign_keys=OFF;";
                    using (var rd = cmd.ExecuteReader())
                    {
                        while (rd.NextResult()) { }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("SQLite 参数优化跳过：" + ex.Message);
            }
        }

        public List<TableDef> LoadTables(OptionsConfig opt, List<TableMapConfig> maps, Action<string> log, List<string> warnings, string targetDefaultSchema = null, Dialect targetDialect = null)
        {
            List<TableDef> all = Reader.ReadTables(Connection, Schema, log);
            var result = new List<TableDef>();
            foreach (TableDef t in all)
            {
                if (!SchemaSql.Match(t.QualifiedName, t.Schema, t.Name, opt.Include, opt.Exclude)) continue;
                result.Add(t);
            }
            log?.Invoke("共发现 " + result.Count + " 张表（匹配 include=" + opt.Include + " exclude=" + opt.Exclude + "）");

            foreach (TableDef t in result) ApplyMap(t, opt, maps, warnings, targetDefaultSchema, targetDialect ?? Dialect);
            return result;
        }

        private void ApplyMap(TableDef t, OptionsConfig opt, List<TableMapConfig> maps, List<string> warnings, string targetDefaultSchema, Dialect targetDialect)
        {
            TableMapConfig map = FindMap(maps, t.Schema, t.Name);

            // 目标 schema 默认取目标库的默认 schema，绝不沿用源库的 schema（SQLite 的 main、MySQL 的库名等不能混用）
            string targetSchema = targetDefaultSchema ?? "";
            string effectiveCase = SqlUtil.ResolveCase(opt.IdentifierCase, targetDialect);
            string targetName = SqlUtil.ApplyCase(t.Name, effectiveCase);
            if (map != null && !string.IsNullOrWhiteSpace(map.Target))
            {
                SqlUtil.SplitQualified(map.Target, out targetSchema, out targetName);
                if (string.IsNullOrEmpty(targetSchema)) targetSchema = targetDefaultSchema ?? "";
            }
            t.TargetSchema = targetSchema;
            t.TargetName = targetName;

            // 需要迁移的列
            var keep = new List<ColumnDef>();
            foreach (ColumnDef c in t.Columns)
            {
                if (map != null && map.SkipColumns.Count > 0 && ContainsIgnoreCase(map.SkipColumns, c.Name)) continue;
                if (map != null && map.Columns.Count > 0 && !ContainsIgnoreCase(map.Columns, c.Name)) continue;
                keep.Add(c);
            }
            if (keep.Count != t.Columns.Count)
            {
                t.Columns = keep;
            }

            foreach (ColumnDef c in t.Columns)
            {
                c.TargetName = SqlUtil.ApplyCase(c.Name, effectiveCase);
                if (map == null) continue;

                foreach (ColumnOverride ov in map.Overrides)
                {
                    if (!string.Equals(ov.Source, c.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrWhiteSpace(ov.Target)) c.TargetName = ov.Target;
                    if (!string.IsNullOrWhiteSpace(ov.TargetType)) c.TypeOverride = ov.TargetType;
                    if (ov.Nullable.HasValue) c.Nullable = ov.Nullable.Value;
                    if (ov.Identity.HasValue) c.IsIdentity = ov.Identity.Value;
                    if (!string.IsNullOrWhiteSpace(ov.Comment)) c.Comment = ov.Comment;
                }
                if (map.Identity.Count > 0 && ContainsIgnoreCase(map.Identity, c.Name))
                {
                    c.IsIdentity = true;
                }
            }

            // 计算目标类型
            foreach (ColumnDef c in t.Columns)
            {
                c.TargetType = !string.IsNullOrWhiteSpace(c.TypeOverride)
                    ? c.TypeOverride
                    : targetDialect.MapType(c, opt);
                if (string.IsNullOrEmpty(c.TargetType))
                {
                    string msg = "表 " + t.QualifiedName + " 列 " + c.Name + " 类型 " + c.RawType + " 无法映射到 " + Dialect.Display;
                    warnings?.Add(msg);
                }
                if (!string.IsNullOrEmpty(c.MappingWarning))
                {
                    warnings?.Add("表 " + t.QualifiedName + " 列 " + c.Name + "：" + c.MappingWarning);
                }
            }

            // 主键列如果目标不允许 NULL，自动去掉可空
            IndexDef pk = t.PrimaryKey;
            if (pk != null)
            {
                foreach (IndexColumn ic in pk.Columns)
                {
                    ColumnDef c = t.FindColumn(ic.Name);
                    if (c != null) c.Nullable = false;
                }

                // 大文本/二进制不能做 MySQL/Oracle/SQL Server 的主键，收敛成定长字符串
                if (targetDialect.Family != DialectFamily.Sqlite)
                {
                    foreach (IndexColumn ic in pk.Columns)
                    {
                        ColumnDef c = t.FindColumn(ic.Name);
                        if (c == null || string.IsNullOrEmpty(c.TargetType)) continue;
                        string tt = c.TargetType.ToUpperInvariant();
                        if (tt.Contains("TEXT") || tt.Contains("CLOB") || tt.Contains("BLOB") || tt == "XML" || tt.Contains("MAX"))
                        {
                            string old = c.TargetType;
                            c.TargetType = KeySafeTextType(targetDialect);
                            warnings?.Add("表 " + t.QualifiedName + " 主键列 " + c.Name + " 的 " + old +
                                          " 不能做键，已改为 " + c.TargetType);
                        }
                    }
                }
            }
        }

        private static string KeySafeTextType(Dialect d)
        {
            switch (d.Family)
            {
                case DialectFamily.MySql: return "VARCHAR(255)";
                case DialectFamily.Oracle: return "VARCHAR2(255)";
                case DialectFamily.SqlServer: return "NVARCHAR(255)";
                case DialectFamily.Postgres: return "VARCHAR(255)";
                default: return "VARCHAR(255)";
            }
        }

        private static bool ContainsIgnoreCase(List<string> list, string value)
        {
            foreach (string s in list)
            {
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static TableMapConfig FindMap(List<TableMapConfig> maps, string schema, string table)
        {
            if (maps == null) return null;
            foreach (TableMapConfig m in maps)
            {
                string ms, mt;
                SqlUtil.SplitQualified(m.Source, out ms, out mt);
                if (!string.Equals(mt, table, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(ms) && !string.Equals(ms, schema, StringComparison.OrdinalIgnoreCase)) continue;
                return m;
            }
            return null;
        }

        public void Dispose()
        {
            try { if (Connection != null) Connection.Dispose(); } catch { }
        }
    }

    /// <summary>迁移报告落地：script.sql、类型映射 CSV、Markdown 摘要。</summary>
    public static class ReportWriter
    {
        public static string ResolveOutDir(OptionsConfig opt)
        {
            string dir = opt?.OutDir;
            if (string.IsNullOrWhiteSpace(dir)) dir = Path.Combine(AppContext.BaseDirectory, "output");
            Directory.CreateDirectory(dir);
            return Path.GetFullPath(dir);
        }

        public static string Timestamp()
        {
            return DateTime.Now.ToString("yyyyMMdd_HHmmss");
        }
    }
}


