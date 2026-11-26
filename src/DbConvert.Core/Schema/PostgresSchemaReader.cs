using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Text;

namespace DbConvert.Schema
{
    /// <summary>PostgreSQL / openGauss / KingbaseES / 瀚高 / PolarDB-PG 的结构读取（均走 PG 协议）。</summary>
    public class PostgresSchemaReader : ISchemaReader
    {
        public string ResolveSchema(DbConnection conn, string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return configured;
            object v = SchemaSql.Scalar(conn, "SELECT current_schema()");
            string s = v == null ? "" : Convert.ToString(v);
            return string.IsNullOrEmpty(s) ? "public" : s;
        }

        public List<TableDef> ReadTables(DbConnection conn, string schema, Action<string> log)
        {
            if (string.IsNullOrEmpty(schema)) schema = "public";
            string lit = SqlUtil.Literal(schema);
            var tables = new List<TableDef>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT c.relname AS table_name, obj_description(c.oid, 'pg_class') AS table_comment, c.reltuples " +
                    "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
                    "WHERE n.nspname = " + lit + " AND c.relkind IN ('r','p') ORDER BY c.relname";
                cmd.CommandTimeout = 120;
                using (var rd = cmd.ExecuteReader())
                {
                    var acc = new RowAccess(rd);
                    while (rd.Read())
                    {
                        var t = new TableDef { Schema = schema, Name = acc.Str("table_name") };
                        t.Comment = acc.Str("table_comment") ?? "";
                        long? est = acc.Long("reltuples");
                        t.RowCountEstimate = est.HasValue && est.Value >= 0 ? est.Value : -1;
                        tables.Add(t);
                    }
                }
            }

            var byName = new Dictionary<string, TableDef>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tables) byName[t.Name] = t;

            ReadColumns(conn, schema, byName, log);
            ReadPrimaryKeys(conn, schema, byName, log);
            ReadIndexes(conn, schema, byName, log);
            ReadConstraints(conn, schema, byName, log);
            return tables;
        }

        private void ReadColumns(DbConnection conn, string schema, Dictionary<string, TableDef> byName, Action<string> log)
        {
            string lit = SqlUtil.Literal(schema);
            string baseSql =
                "SELECT table_name, column_name, ordinal_position, column_default, is_nullable, data_type, " +
                "character_maximum_length, numeric_precision, numeric_scale, datetime_precision, udt_name ";

            bool withIdentity = true;
            bool withGenerated = true;
            var rows = new List<Dictionary<string, object>>();
            while (true)
            {
                var extraCols = new List<string>();
                if (withGenerated) { extraCols.Add("is_generated"); extraCols.Add("generation_expression"); }
                if (withIdentity) extraCols.Add("is_identity");
                string sql = baseSql + (extraCols.Count > 0 ? ", " + string.Join(", ", extraCols) + " " : "") +
                             "FROM information_schema.columns WHERE table_schema = " + lit +
                             " ORDER BY table_name, ordinal_position";
                try
                {
                    rows.Clear();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = sql;
                        cmd.CommandTimeout = 120;
                        using (var rd = cmd.ExecuteReader())
                        {
                            var acc = new RowAccess(rd);
                            while (rd.Read())
                            {
                                var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                                foreach (string name in new[]
                                {
                                    "table_name", "column_name", "ordinal_position", "column_default", "is_nullable",
                                    "data_type", "character_maximum_length", "numeric_precision", "numeric_scale",
                                    "datetime_precision", "udt_name", "is_identity", "is_generated", "generation_expression"
                                })
                                {
                                    if (acc.Has(name)) d[name] = acc.Raw(name);
                                }
                                rows.Add(d);
                            }
                        }
                    }
                    break;
                }
                catch (Exception ex)
                {
                    if (withGenerated)
                    {
                        log?.Invoke("info: is_generated 字段不可用（" + ex.Message + "），生成列信息按普通列处理");
                        withGenerated = false;
                        continue;
                    }
                    if (withIdentity)
                    {
                        log?.Invoke("info: is_identity 字段不可用（" + ex.Message + "），退回旧版数据字典查询");
                        withIdentity = false;
                        continue;
                    }
                    log?.Invoke("读取 PG 系列信息失败：" + ex.Message);
                    return;
                }
            }

            foreach (var d in rows)
            {
                string tn = Convert.ToString(d["table_name"]);
                if (tn == null || !byName.ContainsKey(tn)) continue;
                var t = byName[tn];
                var col = new ColumnDef
                {
                    Name = Convert.ToString(d["column_name"]),
                    TargetName = Convert.ToString(d["column_name"]),
                    Ordinal = ToInt(d["ordinal_position"]) ?? 0
                };
                col.Nullable = !string.Equals(Convert.ToString(d["is_nullable"]), "NO", StringComparison.OrdinalIgnoreCase);
                string def = Convert.ToString(d["column_default"]) ?? "";
                string ident = Convert.ToString(d["is_identity"]) ?? "";
                col.IsIdentity = ident.Equals("YES", StringComparison.OrdinalIgnoreCase) ||
                                 def.IndexOf("nextval(", StringComparison.OrdinalIgnoreCase) >= 0;
                if (withGenerated)
                {
                    string genFlag = Convert.ToString(d["is_generated"]) ?? "";
                    if (genFlag.Equals("YES", StringComparison.OrdinalIgnoreCase))
                    {
                        string genExpr = Convert.ToString(d["generation_expression"]) ?? "";
                        if (!string.IsNullOrWhiteSpace(genExpr))
                        {
                            col.IsComputed = true;
                            col.GeneratedStored = true; // PG 目前只支持 STORED
                            col.GenerationExpression = StripPgCast(genExpr);
                        }
                        else
                        {
                            col.MappingWarning = "生成列没读到表达式，已按普通列处理并搬数据";
                        }
                    }
                }
                if (!string.IsNullOrEmpty(def) && !col.IsIdentity && !col.IsComputed)
                {
                    col.HasDefault = true;
                    col.DefaultExpression = StripPgCast(def);
                }
                MapType(col, d);
                t.Columns.Add(col);
            }
        }

        private void MapType(ColumnDef col, Dictionary<string, object> d)
        {
            string dt = (Convert.ToString(d["data_type"]) ?? "").Trim().ToLowerInvariant();
            string udt = (Convert.ToString(d["udt_name"]) ?? "").Trim().ToLowerInvariant();
            col.RawType = udt;
            col.SourceTypeName = dt + (string.IsNullOrEmpty(udt) ? "" : "/" + udt);
            int? charLen = ToInt(d["character_maximum_length"]);
            int? p = ToInt(d["numeric_precision"]);
            int? s = ToInt(d["numeric_scale"]);
            int? dtp = ToInt(d["datetime_precision"]);

            switch (dt)
            {
                case "boolean":
                case "bool":
                    col.Kind = ColumnKind.Boolean;
                    break;
                case "smallint":
                case "int2":
                    col.Kind = ColumnKind.SmallInt;
                    break;
                case "integer":
                case "int":
                case "int4":
                    col.Kind = ColumnKind.Int;
                    break;
                case "bigint":
                case "int8":
                    col.Kind = ColumnKind.BigInt;
                    break;
                case "numeric":
                case "decimal":
                    col.Kind = ColumnKind.Decimal;
                    col.Precision = p;
                    col.Scale = s;
                    break;
                case "real":
                case "float4":
                    col.Kind = ColumnKind.Float;
                    break;
                case "double precision":
                case "float8":
                    col.Kind = ColumnKind.Double;
                    break;
                case "money":
                    col.Kind = ColumnKind.Money;
                    break;
                case "character varying":
                case "varchar":
                    col.Kind = ColumnKind.VarChar;
                    col.Length = charLen;
                    break;
                case "character":
                case "bpchar":
                case "char":
                    col.Kind = ColumnKind.Char;
                    col.Length = charLen;
                    break;
                case "text":
                case "citext":
                    col.Kind = ColumnKind.Text;
                    break;
                case "bytea":
                    col.Kind = ColumnKind.Blob;
                    break;
                case "date":
                    col.Kind = ColumnKind.Date;
                    break;
                case "time without time zone":
                case "time":
                    col.Kind = ColumnKind.Time;
                    col.DateTimePrecision = dtp;
                    break;
                case "timestamp without time zone":
                case "timestamp":
                    col.Kind = ColumnKind.DateTime;
                    col.DateTimePrecision = dtp;
                    break;
                case "timestamp with time zone":
                case "timestamptz":
                    col.Kind = ColumnKind.TimestampTz;
                    col.DateTimePrecision = dtp;
                    break;
                case "uuid":
                    col.Kind = ColumnKind.Guid;
                    break;
                case "json":
                case "jsonb":
                    col.Kind = ColumnKind.Json;
                    break;
                case "xml":
                    col.Kind = ColumnKind.Xml;
                    break;
                case "interval":
                    col.Kind = ColumnKind.Interval;
                    break;
                case "user-defined":
                case "domain":
                    if (udt.StartsWith("_"))
                    {
                        col.Kind = ColumnKind.Unknown;
                        col.MappingWarning = "数组类型暂不支持";
                    }
                    else
                    {
                        col.Kind = ColumnKind.Enum;
                        col.Length = 255;
                        col.MappingWarning = "自定义类型按字符串迁移";
                    }
                    break;
                case "array":
                    col.Kind = ColumnKind.Unknown;
                    col.MappingWarning = "数组类型暂不支持";
                    break;
                default:
                    col.Kind = ColumnKind.Unknown;
                    col.MappingWarning = "未识别的 PG 类型 " + dt + " (" + udt + ")";
                    break;
            }
        }

        private void ReadPrimaryKeys(DbConnection conn, string schema, Dictionary<string, TableDef> byName, Action<string> log)
        {
            string lit = SqlUtil.Literal(schema);
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT tc.table_name, tc.constraint_name, kcu.column_name, kcu.ordinal_position " +
                        "FROM information_schema.table_constraints tc " +
                        "JOIN information_schema.key_column_usage kcu " +
                        "  ON kcu.constraint_name = tc.constraint_name AND kcu.table_schema = tc.table_schema AND kcu.table_name = tc.table_name " +
                        "WHERE tc.table_schema = " + lit + " AND tc.constraint_type = 'PRIMARY KEY' " +
                        "ORDER BY tc.table_name, kcu.ordinal_position";
                    cmd.CommandTimeout = 120;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tn = acc.Str("table_name");
                            if (tn == null || !byName.ContainsKey(tn)) continue;
                            TableDef t = byName[tn];
                            string cname = acc.Str("constraint_name") ?? "";
                            IndexDef ix = null;
                            foreach (var e in t.Indexes)
                            {
                                if (e.IsPrimary) { ix = e; break; }
                            }
                            if (ix == null)
                            {
                                ix = new IndexDef { Name = string.IsNullOrEmpty(cname) ? tn + "_pkey" : cname, IsPrimary = true, IsUnique = true };
                                t.Indexes.Insert(0, ix);
                            }
                            string c = acc.Str("column_name");
                            if (!string.IsNullOrEmpty(c)) ix.Columns.Add(new IndexColumn { Name = c });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 PG 主键失败：" + ex.Message);
            }
        }

        private void ReadIndexes(DbConnection conn, string schema, Dictionary<string, TableDef> byName, Action<string> log)
        {
            string lit = SqlUtil.Literal(schema);
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT tablename, indexname, indexdef FROM pg_indexes WHERE schemaname = " + lit +
                                      " ORDER BY tablename, indexname";
                    cmd.CommandTimeout = 120;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tn = acc.Str("tablename");
                            if (tn == null || !byName.ContainsKey(tn)) continue;
                            TableDef t = byName[tn];
                            string name = acc.Str("indexname") ?? "";
                            string def = acc.Str("indexdef") ?? "";
                            bool unique = def.TrimStart().ToUpperInvariant().StartsWith("CREATE UNIQUE INDEX");
                            List<IndexColumn> cols = ParseIndexColumns(def, out bool expression);
                            if (expression || cols.Count == 0)
                            {
                                log?.Invoke("跳过表达式索引 " + t.QualifiedName + "." + name);
                                continue;
                            }
                            IndexDef existing = null;
                            foreach (var e in t.Indexes)
                            {
                                if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) { existing = e; break; }
                            }
                            if (existing == null)
                            {
                                existing = new IndexDef { Name = name, IsUnique = unique, IsPrimary = false };
                                t.Indexes.Add(existing);
                            }
                            existing.IsUnique |= unique;
                            if (existing.Columns.Count == 0) existing.Columns.AddRange(cols);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 PG 索引失败（不影响数据搬运）：" + ex.Message);
            }
        }

        /// <summary>从 CREATE INDEX 语句里取出索引列，遇到表达式索引返回 expression=true。</summary>
        public static List<IndexColumn> ParseIndexColumns(string indexdef, out bool expression)
        {
            expression = false;
            var cols = new List<IndexColumn>();
            if (string.IsNullOrEmpty(indexdef)) return cols;
            int usingIdx = indexdef.IndexOf("USING", StringComparison.OrdinalIgnoreCase);
            if (usingIdx < 0) return cols;
            int open = indexdef.IndexOf('(', usingIdx);
            if (open < 0) return cols;
            int close = indexdef.LastIndexOf(')');
            if (close <= open) return cols;
            string body = indexdef.Substring(open + 1, close - open - 1);

            foreach (string raw in SplitTopLevel(body))
            {
                string item = raw.Trim();
                if (item.Length == 0) continue;
                if (item.IndexOf('(') >= 0 || item.IndexOf(')') >= 0)
                {
                    expression = true;
                    return new List<IndexColumn>();
                }
                bool desc = false;
                string upper = item.ToUpperInvariant();
                if (upper.EndsWith("NULLS LAST")) item = item.Substring(0, item.Length - "NULLS LAST".Length).Trim();
                if (upper.EndsWith("NULLS FIRST")) item = item.Substring(0, item.Length - "NULLS FIRST".Length).Trim();
                upper = item.ToUpperInvariant();
                if (upper.EndsWith(" DESC"))
                {
                    desc = true;
                    item = item.Substring(0, item.Length - 5).Trim();
                }
                else if (upper.EndsWith(" ASC"))
                {
                    item = item.Substring(0, item.Length - 4).Trim();
                }
                if (item.StartsWith("\"") && item.EndsWith("\"") && item.Length >= 2)
                {
                    item = item.Substring(1, item.Length - 2).Replace("\"\"", "\"");
                }
                if (item.Contains(":")) item = item.Substring(0, item.IndexOf(':'));
                cols.Add(new IndexColumn { Name = item, Descending = desc });
            }
            return cols;
        }

        private static List<string> SplitTopLevel(string body)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            int depth = 0;
            bool inQuote = false;
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '"') inQuote = !inQuote;
                if (!inQuote)
                {
                    if (c == '(') depth++;
                    if (c == ')') depth--;
                    if (c == ',' && depth == 0)
                    {
                        list.Add(sb.ToString());
                        sb.Clear();
                        continue;
                    }
                }
                sb.Append(c);
            }
            if (sb.Length > 0) list.Add(sb.ToString());
            return list;
        }

        /// <summary>去掉 PG 默认值后面的 ::character varying / ::numeric 之类强制转换，交给目标方言重新映射。</summary>
        private static string StripPgCast(string def)
        {
            string s = (def ?? "").Trim();
            int idx = s.IndexOf("::", StringComparison.Ordinal);
            if (idx > 0) s = s.Substring(0, idx).Trim();
            return s;
        }

        private void ReadConstraints(DbConnection conn, string schema, Dictionary<string, TableDef> byName, Action<string> log)
        {
            string lit = SqlUtil.Literal(schema);
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT tc.table_name, tc.constraint_name, kcu.column_name, ccu.table_name AS ref_table, " +
                        "ccu.column_name AS ref_column, rc.update_rule, rc.delete_rule, kcu.ordinal_position " +
                        "FROM information_schema.table_constraints tc " +
                        "JOIN information_schema.key_column_usage kcu ON kcu.constraint_schema = tc.constraint_schema AND kcu.constraint_name = tc.constraint_name " +
                        "JOIN information_schema.referential_constraints rc ON rc.constraint_schema = tc.constraint_schema AND rc.constraint_name = tc.constraint_name " +
                        "JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_schema = rc.constraint_schema AND ccu.constraint_name = rc.unique_constraint_name " +
                        "WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = " + lit +
                        " ORDER BY tc.table_name, tc.constraint_name, kcu.ordinal_position";
                    cmd.CommandTimeout = 120;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tn = acc.Str("table_name");
                            if (tn == null || !byName.ContainsKey(tn)) continue;
                            TableDef t = byName[tn];
                            string name = acc.Str("constraint_name") ?? "";
                            ForeignKeyDef fk = null;
                            foreach (var e in t.ForeignKeys)
                            {
                                if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) { fk = e; break; }
                            }
                            if (fk == null)
                            {
                                fk = new ForeignKeyDef
                                {
                                    Name = name,
                                    RefTable = acc.Str("ref_table") ?? "",
                                    OnUpdate = acc.Str("update_rule") ?? "",
                                    OnDelete = acc.Str("delete_rule") ?? ""
                                };
                                t.ForeignKeys.Add(fk);
                            }
                            string c = acc.Str("column_name");
                            if (!string.IsNullOrEmpty(c)) fk.Columns.Add(c);
                            string rc = acc.Str("ref_column");
                            if (!string.IsNullOrEmpty(rc)) fk.RefColumns.Add(rc);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 PG 外键失败（将不迁移外键）：" + ex.Message);
            }

            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT c.relname AS table_name, con.conname, pg_get_constraintdef(con.oid) AS def " +
                        "FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid " +
                        "JOIN pg_namespace n ON n.oid = c.relnamespace " +
                        "WHERE n.nspname = " + lit + " AND con.contype = 'c' ORDER BY c.relname, con.conname";
                    cmd.CommandTimeout = 120;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string tn = acc.Str("table_name");
                            if (tn == null || !byName.ContainsKey(tn)) continue;
                            string def = acc.Str("def") ?? "";
                            if (def.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase))
                            {
                                int open = def.IndexOf('(');
                                int close = def.LastIndexOf(')');
                                if (open >= 0 && close > open) def = def.Substring(open + 1, close - open - 1);
                            }
                            byName[tn].Checks.Add(new CheckDef { Name = acc.Str("conname") ?? "", Expression = def });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 PG CHECK 约束失败：" + ex.Message);
            }
        }

        public List<ViewDef> ReadViews(DbConnection conn, string schema, Action<string> log)
        {
            var list = new List<ViewDef>();
            string lit = SqlUtil.Literal(schema);
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT viewname, definition FROM pg_views WHERE schemaname = " + lit + " ORDER BY viewname";
                    cmd.CommandTimeout = 120;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            list.Add(new ViewDef { Schema = schema, Name = acc.Str("viewname") ?? "", Definition = acc.Str("definition") ?? "" });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 PG 视图失败：" + ex.Message);
            }
            return list;
        }

        public List<RoutineDef> ReadRoutines(DbConnection conn, string schema, Action<string> log)
        {
            var list = new List<RoutineDef>();
            string lit = SqlUtil.Literal(schema);
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT p.proname, p.prokind, pg_get_functiondef(p.oid) AS def, " +
                        "pg_get_function_arguments(p.oid) AS args, pg_get_function_result(p.oid) AS ret " +
                        "FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
                        "WHERE n.nspname = " + lit + " AND p.prokind <> 'a' ORDER BY p.proname";
                    cmd.CommandTimeout = 120;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string kind = acc.Str("prokind") ?? "f";
                            list.Add(new RoutineDef
                            {
                                Schema = schema,
                                Name = acc.Str("proname") ?? "",
                                Kind = kind == "p" ? "procedure" : "function",
                                Definition = acc.Str("def") ?? "",
                                Arguments = acc.Str("args") ?? "",
                                ReturnsType = acc.Str("ret") ?? ""
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 PG 存储过程/函数失败：" + ex.Message);
            }
            return list;
        }

        public List<TriggerDef> ReadTriggers(DbConnection conn, string schema, Action<string> log)
        {
            var list = new List<TriggerDef>();
            string lit = SqlUtil.Literal(schema);
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "SELECT t.tgname, c.relname AS table_name, pg_get_triggerdef(t.oid) AS def " +
                        "FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid " +
                        "JOIN pg_namespace n ON n.oid = c.relnamespace " +
                        "WHERE NOT t.tgisinternal AND n.nspname = " + lit + " ORDER BY c.relname, t.tgname";
                    cmd.CommandTimeout = 120;
                    using (var rd = cmd.ExecuteReader())
                    {
                        var acc = new RowAccess(rd);
                        while (rd.Read())
                        {
                            string def = acc.Str("def") ?? "";
                            list.Add(new TriggerDef
                            {
                                Schema = schema,
                                Name = acc.Str("tgname") ?? "",
                                Table = acc.Str("table_name") ?? "",
                                Timing = def.IndexOf("BEFORE", StringComparison.OrdinalIgnoreCase) >= 0 ? "BEFORE" :
                                         (def.IndexOf("INSTEAD OF", StringComparison.OrdinalIgnoreCase) >= 0 ? "INSTEAD OF" : "AFTER"),
                                Definition = def
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke("读取 PG 触发器失败：" + ex.Message);
            }
            return list;
        }

        public long CountRows(DbConnection conn, string schema, string table)
        {
            var d = Dialects.Get("postgres");
            return SchemaSql.CountRows(conn, d.Qualify(schema, table));
        }

        private static int? ToInt(object v)
        {
            if (v == null) return null;
            string s = Convert.ToString(v);
            if (string.IsNullOrWhiteSpace(s)) return null;
            try { return Convert.ToInt32(decimal.Parse(s)); } catch { return null; }
        }
    }
}
